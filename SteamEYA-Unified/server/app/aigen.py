"""AI 自动配图核心。

流程：
  新建店铺/菜品（image 为空）→ 入队 ai_image_jobs
  → 后台线程取任务
  → 文本模型根据「名称 + 所属店铺 + 位置 + 同店菜品」生成英文提示词
  → OpenAI 兼容 /images/generations 出图
  → 下载/解码 → Pillow 转 webp 压缩 → 落盘 /uploads/eat/ai-xxx.webp
  → 回写 eat_stores / eat_dishes 的 image 与 image_status

设计取舍：
- 创建接口只入队，绝不在请求里等出图；
- 文本模型失败时用内置模板兜底，不让整条任务失败；
- 只有 image 为空时才生成，用户自己上传的图永远不会被覆盖；
- 配置存 settings 的 `ai_image_config`，读写方式与 ai_config 一致。
"""
from __future__ import annotations

import base64
import io
import ipaddress
import json
import logging
import re
import socket
import threading
import time
from typing import Optional
from urllib.parse import urlparse

import requests

from . import security
from .config import UPLOAD_DIR
from .db import execute, execute_update, query_all, query_one

log = logging.getLogger("gateway.aigen")

AI_IMG_DIR = UPLOAD_DIR / "eat"
AI_IMG_DIR.mkdir(parents=True, exist_ok=True)

MAX_BYTES = 10 * 1024 * 1024          # 单张图片下载上限
MAX_PROMPT_LEN = 800                  # 原提示词 + 用户补充词的总长度上限
POLL_SEC = 2.5

DEFAULT_CONFIG: dict = {
    # ---- 总开关 ----
    "enabled": False,                 # 默认关闭，管理员配置好模型后再开
    "auto_on_create": True,           # 新建店铺/菜品无图时自动入队
    "daily_limit": 50,                # 每日最多生成多少张
    "retry_max": 2,                   # 单条失败后的重试次数
    "retry_backoff_sec": 20,          # 失败后至少等待多久再重试（避免连续打接口）
    "job_timeout_sec": 120,           # 单条任务整体超时（含文本 + 生图 + 下载）

    # ---- 文本模型：生成提示词 ----
    "text_base_url": "https://api.deepseek.com",
    "text_api_key": "",
    "text_model": "deepseek-chat",
    "text_temperature": 0.7,
    "text_max_tokens": 200,
    "text_timeout_sec": 30,
    "prompt_system": (
        "你是美食摄影提示词专家。根据用户给出的店铺与菜品信息，输出一句可直接用于文生图模型的英文提示词。"
        "要求：只输出提示词本身，不要解释、不要引号、不要换行；"
        "描述主体、配料、餐具、光线、构图与景深；画面干净诱人；"
        "不要出现文字、招牌字、logo、人物、手部。"
    ),
    "prompt_template": (
        "菜品：{dish}\n店铺：{store}\n位置：{location}\n同店其他菜品：{siblings}\n"
        "请输出一条英文美食摄影提示词。"
    ),

    # ---- 生图模型 ----
    "image_base_url": "https://api.openai.com/v1",
    "image_api_key": "",
    "image_model": "sensenova-u1.5-lite",
    "store_size": "2048x1536",        # 店铺封面 4:3（2K，边长 32 的倍数）
    "dish_size": "2048x2048",         # 菜品方图（2K）
    "image_timeout_sec": 90,
    # 生图请求参数（SenseNova / OpenAI 兼容）
    "watermark": False,               # false = 不加水印
    "response_format": "url",         # url = 返回公网下载地址；b64_json = Base64
    "output_format": "webp",          # png / jpeg / webp
    "prompt_extend": True,            # 开启提示词自动润色
    "style_suffix": (
        "professional food photography, warm natural lighting, 45-degree angle, "
        "shallow depth of field, appetizing, high detail"
    ),
    "extra_prompt": "",
    "negative_prompt": "no text, no watermark, no logo, no people",
}

_ALLOWED_PATCH = set(DEFAULT_CONFIG.keys())

_started = False
_lock = threading.Lock()


# ---------------------------------------------------------------- 配置
def get_config() -> dict:
    cfg = dict(DEFAULT_CONFIG)
    row = query_one("SELECT value FROM settings WHERE key = 'ai_image_config'")
    if row and row["value"]:
        try:
            cfg.update(json.loads(row["value"]))
        except Exception:
            pass
    return cfg


def save_config(patch: dict) -> dict:
    cfg = get_config()
    for k, v in (patch or {}).items():
        if k in _ALLOWED_PATCH:
            cfg[k] = v
    execute(
        "INSERT INTO settings (key, value, updated_at) VALUES ('ai_image_config', ?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
        (json.dumps(cfg, ensure_ascii=False), security.now_ms()),
    )
    return cfg


def masked_config() -> dict:
    """后台读取用：API Key 只回掩码，避免明文回传浏览器。"""
    cfg = get_config()
    for k in ("text_api_key", "image_api_key"):
        v = str(cfg.get(k) or "")
        cfg[k] = ("****" + v[-4:]) if len(v) > 4 else ("****" if v else "")
    return cfg


def is_ready() -> bool:
    cfg = get_config()
    return bool(cfg.get("enabled") and cfg.get("image_base_url") and cfg.get("image_api_key")
                and cfg.get("image_model"))


# ---------------------------------------------------------------- 小工具
def _now() -> int:
    return security.now_ms()


def _endpoint(base: str, path: str) -> str:
    """拼接口地址：base 可以是 .../v1、.../v1/、甚至完整 .../images/generations。"""
    base = str(base or "").strip().rstrip("/")
    if not base:
        return ""
    if base.endswith(path):
        return base
    return base + path


def _clean_prompt(text: str) -> str:
    s = str(text or "").strip()
    s = s.strip("\"'“”‘’` \n\r\t")
    s = re.sub(r"```.*?```", "", s, flags=re.S)
    s = re.sub(r"\s+", " ", s).strip()
    return s[:MAX_PROMPT_LEN]


def _clip_err(msg) -> str:
    return str(msg or "")[:200]


def _rv(row, key: str, default=""):
    """sqlite3.Row 没有 .get，统一用这个安全取值。"""
    try:
        v = row[key]
        return default if v is None else v
    except (KeyError, IndexError, TypeError):
        return default


def _private_host(host: str) -> bool:
    """SSRF 防护：拒绝 localhost / 内网 / 链路本地 / 云元数据地址。"""
    host = (host or "").strip().lower()
    if not host or host in ("localhost", "localhost.localdomain"):
        return True
    try:
        infos = socket.getaddrinfo(host, None)
    except Exception:
        return True
    for info in infos:
        try:
            ip = ipaddress.ip_address(info[4][0])
        except Exception:
            return True
        if (ip.is_private or ip.is_loopback or ip.is_link_local
                or ip.is_multicast or ip.is_reserved or ip.is_unspecified):
            return True
    return False


# ---------------------------------------------------------------- 提示词
def _target_row(kind: str, target_id: int) -> Optional[dict]:
    if kind == "store":
        return query_one("SELECT * FROM eat_stores WHERE id = ?", (target_id,))
    return query_one("SELECT * FROM eat_dishes WHERE id = ?", (target_id,))


def _context(kind: str, row: dict) -> dict:
    """收集给文本模型的上下文：店铺、位置、同店其他菜品。"""
    if kind == "store":
        dishes = query_all(
            "SELECT name FROM eat_dishes WHERE store_id = ? ORDER BY id ASC LIMIT 8", (row["id"],))
        return {
            "dish": "",
            "store": row["name"],
            "location": _rv(row, "location"),
            "siblings": "、".join(d["name"] for d in dishes) or "暂无",
        }
    store = query_one("SELECT * FROM eat_stores WHERE id = ?", (row["store_id"],))
    dishes = query_all(
        "SELECT name FROM eat_dishes WHERE store_id = ? AND id <> ? ORDER BY id ASC LIMIT 6",
        (row["store_id"], row["id"]))
    return {
        "dish": row["name"],
        "store": _rv(store, "name"),
        "location": _rv(store, "location"),
        "siblings": "、".join(d["name"] for d in dishes) or "暂无",
    }


def _fallback_prompt(kind: str, ctx: dict) -> str:
    cfg = get_config()
    if kind == "store":
        subject = f"{ctx['store']}, restaurant storefront and cozy dining scene, signature dishes"
    else:
        subject = f"{ctx['dish']}, authentic Chinese dish from {ctx['store']}"
    parts = [subject, str(cfg.get("style_suffix") or "")]
    if cfg.get("extra_prompt"):
        parts.append(str(cfg["extra_prompt"]))
    if cfg.get("negative_prompt"):
        parts.append(str(cfg["negative_prompt"]))
    return _clean_prompt(", ".join(p for p in parts if p))


def _ask_text_model(prompt: str) -> str:
    """调用文本模型生成提示词；失败返回空串由调用方兜底。"""
    cfg = get_config()
    base = str(cfg.get("text_base_url") or "").strip()
    key = str(cfg.get("text_api_key") or "").strip()
    model = str(cfg.get("text_model") or "").strip()
    if not (base and key and model):
        return ""
    try:
        res = requests.post(
            _endpoint(base, "/chat/completions"),
            headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
            json={
                "model": model,
                "messages": [
                    {"role": "system", "content": str(cfg.get("prompt_system") or "")},
                    {"role": "user", "content": prompt},
                ],
                "temperature": float(cfg.get("text_temperature") or 0.7),
                "max_tokens": int(cfg.get("text_max_tokens") or 200),
            },
            timeout=int(cfg.get("text_timeout_sec") or 30),
        )
        if res.status_code >= 400:
            log.warning("文本模型返回 %s: %s", res.status_code, res.text[:160])
            return ""
        data = res.json()
        return _clean_prompt((data.get("choices") or [{}])[0].get("message", {}).get("content", ""))
    except Exception as exc:
        log.warning("文本模型调用失败：%s", exc)
        return ""


def build_prompt(kind: str, row: dict) -> str:
    ctx = _context(kind, row)
    tpl = str(get_config().get("prompt_template") or "")
    try:
        user_msg = tpl.format(**ctx)
    except Exception:
        user_msg = f"菜品：{ctx['dish']} 店铺：{ctx['store']} 位置：{ctx['location']}"
    prompt = _ask_text_model(user_msg)
    if prompt:
        cfg = get_config()
        extras = [p for p in (cfg.get("style_suffix"), cfg.get("extra_prompt"), cfg.get("negative_prompt")) if p]
        if extras:
            prompt = _clean_prompt(prompt + ", " + ", ".join(str(x) for x in extras))
        return prompt
    return _fallback_prompt(kind, ctx)


def preview_prompt(kind: str, name: str, store: str = "", location: str = "",
                   siblings: str = "") -> dict:
    """后台「测试文本模型」用：不依赖数据库对象。"""
    kind = "store" if kind == "store" else "dish"
    name = str(name or "").strip()[:60]
    ctx = {"dish": name, "store": str(store or "").strip()[:60],
           "location": str(location or "").strip()[:60], "siblings": siblings or "暂无"}
    tpl = str(get_config().get("prompt_template") or "")
    try:
        user_msg = tpl.format(**ctx)
    except Exception:
        user_msg = f"菜品：{ctx['dish']} 店铺：{ctx['store']} 位置：{ctx['location']}"
    prompt = _ask_text_model(user_msg)
    source = "model"
    if prompt:
        cfg = get_config()
        extras = [p for p in (cfg.get("style_suffix"), cfg.get("extra_prompt"), cfg.get("negative_prompt")) if p]
        if extras:
            prompt = _clean_prompt(prompt + ", " + ", ".join(str(x) for x in extras))
    else:
        source = "template"
        prompt = _fallback_prompt(kind, ctx)
    return {"prompt": prompt, "source": source, "userMessage": user_msg}


# ---------------------------------------------------------------- 生图
def _looks_image(raw: bytes) -> bool:
    if not raw or len(raw) < 16:
        return False
    if raw.startswith(b"\x89PNG\r\n\x1a\n") or raw.startswith(b"\xff\xd8"):
        return True
    if raw[:6] in (b"GIF87a", b"GIF89a"):
        return True
    if raw[:4] == b"RIFF" and raw[8:12] == b"WEBP":
        return True
    if raw[:2] == b"BM":
        return True
    return False


def _download(url: str) -> bytes:
    p = urlparse(url)
    if p.scheme not in ("http", "https") or not p.hostname:
        raise ValueError("图片地址不合法")
    if _private_host(p.hostname):
        raise ValueError("图片地址指向内网，已拒绝")
    with requests.get(url, stream=True, timeout=30) as res:
        if res.status_code >= 400:
            raise ValueError(f"下载图片失败：HTTP {res.status_code}")
        ctype = (res.headers.get("Content-Type") or "").lower()
        if ctype and not (ctype.startswith("image/") or ctype.startswith("application/octet-stream")):
            raise ValueError("返回内容不是图片")
        buf = bytearray()
        for chunk in res.iter_content(64 * 1024):
            buf.extend(chunk)
            if len(buf) > MAX_BYTES:
                raise ValueError("图片超过 10MB")
        data = bytes(buf)
    if not _looks_image(data):
        raise ValueError("下载内容不是有效图片")
    return data


def generate_image(prompt: str, size: str) -> bytes:
    cfg = get_config()
    key = str(cfg.get("image_api_key") or "").strip()
    if not key:
        raise ValueError("未配置生图 API Key")
    res = requests.post(
        _endpoint(str(cfg.get("image_base_url") or ""), "/images/generations"),
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
        json={
            "model": str(cfg.get("image_model") or ""),
            "prompt": prompt,
            "n": 1,
            "size": size or "1024x1024",
            # 不加水印；公网 url 返回（24 小时有效，下面立即下载落盘）；开启提示词润色
            "watermark": bool(cfg.get("watermark", False)),
            "response_format": str(cfg.get("response_format") or "url"),
            "output_format": str(cfg.get("output_format") or "webp"),
            "prompt_extend": bool(cfg.get("prompt_extend", True)),
        },
        timeout=min(int(cfg.get("image_timeout_sec") or 90), int(cfg.get("job_timeout_sec") or 120)),
    )
    if res.status_code >= 400:
        raise ValueError(f"生图失败：HTTP {res.status_code} {res.text[:160]}")
    data = res.json()
    item = (data.get("data") or [{}])[0] if isinstance(data, dict) else {}
    if item.get("b64_json"):
        raw = base64.b64decode(item["b64_json"])
    elif item.get("url"):
        raw = _download(item["url"])
    elif (res.headers.get("Content-Type") or "").lower().startswith("image/"):
        # 少数中转直接把图片二进制放在响应体里
        if len(res.content) > MAX_BYTES:
            raise ValueError("图片超过 10MB")
        raw = res.content
    else:
        raise ValueError("生图接口未返回图片")
    if not _looks_image(raw):
        raise ValueError("生图接口返回的不是有效图片")
    return raw


def _save_image(raw: bytes) -> str:
    """压缩为 webp（最长边 1600，质量 .8）；Pillow 不可用时原样保存。"""
    name = f"ai-{_now()}-{security.gen_token(6)}"
    try:
        from PIL import Image

        im = Image.open(io.BytesIO(raw))
        if im.mode not in ("RGB", "RGBA"):
            im = im.convert("RGB")
        im.thumbnail((1600, 1600))
        out = AI_IMG_DIR / (name + ".webp")
        im.save(out, "WEBP", quality=80, method=6)
        return f"/uploads/eat/{out.name}"
    except Exception as exc:
        log.warning("图片压缩失败，按原图保存：%s", exc)

    ext = ".png"
    head = raw[:12]
    if head.startswith(b"\xff\xd8"):
        ext = ".jpg"
    elif head[:4] == b"RIFF" and b"WEBP" in raw[:16]:
        ext = ".webp"
    out = AI_IMG_DIR / (name + ext)
    out.write_bytes(raw)
    return f"/uploads/eat/{out.name}"


def _delete_old_image(old_url: str, keep_url: str = "") -> None:
    """重新生图成功后删除旧图：只删 /uploads/ 下的本地文件，外链与当前图一律跳过。"""
    u = str(old_url or "").strip()
    if not u or u == keep_url or not u.startswith("/uploads/"):
        return
    try:
        root = UPLOAD_DIR.resolve()
        p = (UPLOAD_DIR / u[len("/uploads/"):]).resolve()
        if root != p and root not in p.parents:
            log.warning("旧图路径越界，已跳过：%s", u)
            return
        if p.is_file():
            p.unlink()
            log.info("已删除旧配图：%s", u)
    except Exception as exc:
        log.warning("删除旧配图失败 %s：%s", u, exc)


# ---------------------------------------------------------------- 队列
def enqueue(kind: str, target_id: int, force: bool = False,
            extra_prompt: str = "", regen: bool = False) -> Optional[int]:
    """入队一张配图。

    - 普通模式：仅「无图 + 已开启自动生图」时入队；
    - regen=True：编辑页「重新生图」，忽略已有图片与自动开关，先取消该对象未完成的任务；
    - extra_prompt：用户补充提示词，执行时追加到原有提示词后面。
    """
    cfg = get_config()
    if not force and not regen and not (cfg.get("enabled") and cfg.get("auto_on_create")):
        return None
    row = _target_row(kind, target_id)
    if not row:
        return None
    if not regen and str(_rv(row, "image") or "").strip():
        return None
    if regen:
        execute_update(
            "UPDATE ai_image_jobs SET status = 'canceled', error = ?, updated_at = ?, finished_at = ? "
            "WHERE kind = ? AND target_id = ? AND status IN ('queued','running')",
            ("已被重新生图替换", _now(), _now(), kind, target_id))
    elif query_one("SELECT id FROM ai_image_jobs WHERE kind = ? AND target_id = ? "
                   "AND status IN ('queued','running') LIMIT 1", (kind, target_id)):
        return None
    jid = execute(
        "INSERT INTO ai_image_jobs (kind, target_id, status, extra_prompt, mode, created_at, updated_at) "
        "VALUES (?,?,?,?,?,?,?)",
        (kind, target_id, "queued", str(extra_prompt or "").strip()[:300],
         "regen" if regen else "auto", _now(), _now()))
    _set_target_status(kind, target_id, "queued")
    return jid


def _set_target_status(kind: str, target_id: int, status: str, error: str = "") -> None:
    table = "eat_stores" if kind == "store" else "eat_dishes"
    execute_update(f"UPDATE {table} SET image_status = ?, image_error = ? WHERE id = ?",
                   (status, _clip_err(error), target_id))


def _set_job(job_id: int, **kw) -> None:
    fields, vals = [], []
    for k, v in kw.items():
        fields.append(f"{k} = ?")
        vals.append(v)
    fields.append("updated_at = ?")
    vals.append(_now())
    execute_update(f"UPDATE ai_image_jobs SET {', '.join(fields)} WHERE id = ?", (*vals, job_id))


def _today_done() -> int:
    import datetime
    start = datetime.datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
    start_ms = int(start.timestamp() * 1000)
    row = query_one("SELECT COUNT(*) AS c FROM ai_image_jobs WHERE status = 'ok' AND finished_at >= ?",
                    (start_ms,))
    return int(row["c"] or 0) if row else 0


def process_job(job: dict) -> None:
    cfg = get_config()
    jid = int(job["id"])
    kind = str(job["kind"])
    tid = int(job["target_id"])
    row = _target_row(kind, tid)
    if not row:
        _set_job(jid, status="failed", error="对象已删除", finished_at=_now())
        return
    mode = str(_rv(job, "mode", "auto") or "auto")
    if mode != "regen" and str(_rv(row, "image") or "").strip():
        _set_job(jid, status="canceled", error="已有图片，跳过", finished_at=_now())
        _set_target_status(kind, tid, "ok")
        return
    old_image = str(_rv(row, "image") or "").strip()

    _set_job(jid, status="running")
    _set_target_status(kind, tid, "running")
    try:
        prompt = build_prompt(kind, row)
        if not prompt:
            raise ValueError("提示词为空")
        extra = str(_rv(job, "extra_prompt", "") or "").strip()
        if extra:
            # 用户补充的提示词追加在原有提示词后面
            prompt = _clean_prompt(prompt + ", " + extra)
        size = str(cfg.get("store_size") if kind == "store" else cfg.get("dish_size") or "1024x1024")
        raw = generate_image(prompt, size)
        # 出图期间管理员可能已取消：取消后不再写回，图片文件留作孤儿（可忽略）
        cur = query_one("SELECT status FROM ai_image_jobs WHERE id = ?", (jid,))
        if not cur or str(cur["status"]) == "canceled":
            return
        url = _save_image(raw)
        table = "eat_stores" if kind == "store" else "eat_dishes"
        execute_update(
            f"UPDATE {table} SET image = ?, image_status = 'ok', image_source = 'ai', "
            "image_error = '', image_generated_at = ? WHERE id = ?",
            (url, _now(), tid))
        _set_job(jid, status="ok", prompt=prompt, image_url=url, error="", finished_at=_now())
        log.info("AI 配图完成 %s#%s", kind, tid)
        if mode == "regen" and old_image:
            _delete_old_image(old_image, url)
    except Exception as exc:
        attempts = int(_rv(job, "attempts", 0) or 0) + 1
        retry_max = int(cfg.get("retry_max") or 0)
        msg = _clip_err(exc)
        if attempts <= retry_max:
            _set_job(jid, status="queued", attempts=attempts, error=msg)
            _set_target_status(kind, tid, "queued", msg)
            log.warning("AI 配图失败，稍后重试（%s/%s）%s#%s：%s", attempts, retry_max, kind, tid, msg)
        else:
            _set_job(jid, status="failed", attempts=attempts, error=msg, finished_at=_now())
            _set_target_status(kind, tid, "failed", msg)
            log.warning("AI 配图最终失败 %s#%s：%s", kind, tid, msg)


def _worker() -> None:
    # 进程重启后，把卡在 running 的任务放回队列
    execute_update("UPDATE ai_image_jobs SET status = 'queued' WHERE status = 'running'", ())
    while True:
        try:
            if is_ready():
                _lim = get_config().get("daily_limit")
                limit = 50 if _lim is None else int(_lim)
                if _today_done() < limit:
                    backoff = max(0, int(get_config().get("retry_backoff_sec") or 0)) * 1000
                    job = query_one(
                        "SELECT * FROM ai_image_jobs WHERE status = 'queued' "
                        "AND updated_at + COALESCE(attempts, 0) * ? <= ? "
                        "ORDER BY id ASC LIMIT 1", (backoff, _now()))
                    if job:
                        process_job(job)
        except Exception as exc:
            log.warning("AI 配图 worker 异常：%s", exc)
        time.sleep(POLL_SEC)


def start_worker() -> None:
    global _started
    with _lock:
        if _started:
            return
        _started = True
    threading.Thread(target=_worker, name="ai-image-worker", daemon=True).start()
    log.info("AI 配图 worker 已启动")


# ---------------------------------------------------------------- 后台运维
def scan(scope: str = "all", limit: int = 200, dry_run: bool = True) -> dict:
    """扫描未配图的店铺/菜品。dry_run=True 只返回数量，不入队。"""
    out = {"stores": [], "dishes": [], "queued": 0}
    if scope in ("all", "stores"):
        out["stores"] = query_all(
            "SELECT id, name, location FROM eat_stores "
            "WHERE TRIM(COALESCE(image,'')) = '' AND COALESCE(image_status,'') NOT IN ('queued','running') "
            "ORDER BY id ASC LIMIT ?", (limit,))
    if scope in ("all", "dishes"):
        out["dishes"] = query_all(
            "SELECT d.id, d.name, s.name AS store_name FROM eat_dishes d "
            "LEFT JOIN eat_stores s ON s.id = d.store_id "
            "WHERE TRIM(COALESCE(d.image,'')) = '' AND COALESCE(d.image_status,'') NOT IN ('queued','running') "
            "ORDER BY d.id ASC LIMIT ?", (limit,))
    if not dry_run:
        for s in out["stores"]:
            if enqueue("store", int(s["id"]), force=True):
                out["queued"] += 1
        for d in out["dishes"]:
            if enqueue("dish", int(d["id"]), force=True):
                out["queued"] += 1
    return out


def stats() -> dict:
    rows = query_all("SELECT status, COUNT(*) AS c FROM ai_image_jobs GROUP BY status")
    by_status = {r["status"]: int(r["c"]) for r in rows}
    return {
        "byStatus": by_status,
        "todayDone": _today_done(),
        "pending": by_status.get("queued", 0) + by_status.get("running", 0),
    }
