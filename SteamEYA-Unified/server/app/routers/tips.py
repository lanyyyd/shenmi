"""左侧导航「tips 气泡」内容下发。

客户端在导航栏底部显示一句短文案，每 60 秒自动换一条，点击区域也能手动换下一条。

内容有两个来源，按权重加权随机：
  1. 后台手动添加的条目（每条独立权重，默认 10）
  2. 一言 API（作为**一个**虚拟条目参与，权重默认 1，可在后台关掉）

因为手动条目的权重之和通常远大于一言的 1，所以表现为「优先显示后台手动添加的文字」；
想让一言更多出现，把手动条目权重调小或把一言权重调大即可。

用户侧（无需登录，客户端启动即可取）：
  GET  /api/tips/next                取一条 {content, source}
管理侧（require_admin）：
  GET    /api/admin/tips             列表 + 一言配置
  POST   /api/admin/tips             新增条目
  PUT    /api/admin/tips/{id}        修改内容 / 权重 / 启用
  DELETE /api/admin/tips/{id}        删除条目
  POST   /api/admin/tips/config      保存一言 API、权重、开关
"""
from __future__ import annotations

import random
import threading
import time
from urllib.parse import urlsplit, urlunsplit

import requests
from fastapi import APIRouter, Body, Depends, HTTPException, Response

from .. import security
from ..db import execute, execute_update, query_all, query_one
from ..deps import require_admin

router = APIRouter(prefix="/api", tags=["tips"])

DEFAULT_HITOKOTO_API = "https://v1.hitokoto.cn/?encode=text"
DEFAULT_HITOKOTO_WEIGHT = 1
DEFAULT_MANUAL_WEIGHT = 10
MAX_CONTENT_LEN = 200

# 一言结果缓冲池：外部接口慢且不稳定（单次可能要 3~10 秒），如果在请求里同步等，
# 既容易超时回落到同一句兜底文案，也会把客户端卡住。
# 所以改由后台线程预先拉取若干条放进池子，取用时随机挑一条（不回退池子），
# 这样连续点击也能拿到不同内容；池子有空位时再触发补货（最多 3 个线程并行）。
_HK_POOL_MAX = 8
_HK_ITEM_TTL = 900.0
_HK_TIMEOUT = 10.0
_HK_FETCH_GAP = 0.2
_HK_WORKERS = 3

# 缓冲池总开关：False = 每次请求都同步直连一言接口（用于测试上游速度）；
# True = 后台预取 + 池子随机取（生产推荐，避免被慢接口拖住）。
_HK_POOL_ENABLED = False

_hk_pool: list[tuple[str, float]] = []
_hk_last = ""
_hk_lock = threading.Lock()
_hk_fetching = 0
# 复用连接（TLS 握手约占单次耗时的一大半），显著提高补货速度；
# requests.Session 不保证线程安全，所以每个补货线程用自己的连接。
_hk_local = threading.local()


def _hk_http() -> requests.Session:
    sess = getattr(_hk_local, "session", None)
    if sess is None:
        sess = requests.Session()
        _hk_local.session = sess
    return sess

FALLBACK_TEXT = "今天也要顺顺利利。"


# ---------------------------------------------------------------- settings
def _setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _save_setting(key: str, value: str) -> None:
    execute(
        "INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
        (key, value, security.now_ms()),
    )


def _int_setting(key: str, default: int) -> int:
    raw = _setting(key, str(default))
    try:
        return int(str(raw).strip())
    except (TypeError, ValueError):
        return default


def _clamp_weight(value) -> int:
    try:
        return max(0, min(1000, int(value)))
    except (TypeError, ValueError):
        return DEFAULT_MANUAL_WEIGHT


def _clean_content(value) -> str:
    text = str(value or "").replace("\r", " ").replace("\n", " ").strip()
    return text[:MAX_CONTENT_LEN]


# ---------------------------------------------------------------- 一言
def _hitokoto_url() -> str:
    url = (_setting("tips.hitokoto_api", DEFAULT_HITOKOTO_API) or DEFAULT_HITOKOTO_API).strip()
    # 只允许 http/https，避免管理员误填 file:// 之类导致本地文件被读。
    if not url.lower().startswith(("http://", "https://")):
        return ""
    return url


def _fetch_hitokoto_once(url: str) -> str:
    """真正发一次请求。失败返回空串，由调用方决定回落策略。"""
    if not url:
        return ""
    try:
        # 加随机时间戳，避免上游/中间层按 URL 缓存导致每次都返回同一条
        parts = urlsplit(url)
        q = parts.query + ("&" if parts.query else "") + "_t=" + str(int(time.time() * 1000))
        req_url = urlunsplit((parts.scheme, parts.netloc, parts.path, q, parts.fragment))
        resp = _hk_http().get(req_url, timeout=_HK_TIMEOUT,
                              headers={"User-Agent": "TeaTT-Pocket/1.0"})
        if resp.status_code != 200:
            return ""

        text = resp.text.strip()
        # 一言官方 API 带 encode=text 返回纯文本；换成 JSON 版接口时也能兼容。
        if text.startswith("{"):
            data = resp.json()
            text = str(data.get("hitokoto") or data.get("text") or "").strip()

        return _clean_content(text)
    except Exception:
        return ""


def _hk_refill(url: str) -> None:
    """后台补货：把池子填到上限，最多 _HK_WORKERS 个线程并行拉取。"""
    global _hk_fetching
    try:
        while True:
            with _hk_lock:
                if len(_hk_pool) >= _HK_POOL_MAX:
                    return
            text = _fetch_hitokoto_once(url)
            if text:
                with _hk_lock:
                    if not any(t == text for t, _ in _hk_pool):
                        if len(_hk_pool) >= _HK_POOL_MAX:
                            _hk_pool.pop(0)
                        _hk_pool.append((text, time.time()))
            # 上游本来就不快，稍微间隔一下，避免连续打爆
            time.sleep(_HK_FETCH_GAP)
    finally:
        with _hk_lock:
            _hk_fetching = max(0, _hk_fetching - 1)


def _ensure_pool(url: str) -> None:
    """有空位就补货；池子满了则等最老的一条过期后挤掉它，让内容慢慢轮换。"""
    global _hk_fetching
    if not url or not _HK_POOL_ENABLED:
        return
    while True:
        now = time.time()
        with _hk_lock:
            if _hk_fetching >= _HK_WORKERS:
                return
            if len(_hk_pool) >= _HK_POOL_MAX:
                if now - _hk_pool[0][1] < _HK_ITEM_TTL:
                    return
                _hk_pool.pop(0)
            if len(_hk_pool) + _hk_fetching >= _HK_POOL_MAX:
                return
            _hk_fetching += 1
        threading.Thread(target=_hk_refill, args=(url,), name="hitokoto-prefetch", daemon=True).start()


def start_prefetch() -> None:
    """服务启动时预热池子，避免重启后的第一批用户只拿到兜底句。"""
    if not _HK_POOL_ENABLED:
        return
    try:
        if _setting("tips.hitokoto_enabled", "1") == "0":
            return
        if _int_setting("tips.hitokoto_weight", DEFAULT_HITOKOTO_WEIGHT) <= 0:
            return
        _ensure_pool(_hitokoto_url())
    except Exception:
        pass


def _take_hitokoto(url: str) -> str:
    """从池子里随机挑一条；取不到立即返回空串（由调用方回落），不阻塞请求。"""
    global _hk_last
    text = ""
    with _hk_lock:
        if _hk_pool:
            idx = random.randrange(len(_hk_pool))
            # 尽量不和上一次重复，让连续点击也能看到新内容
            if len(_hk_pool) > 1 and _hk_pool[idx][0] == _hk_last:
                idx = (idx + 1) % len(_hk_pool)
            text = _hk_pool[idx][0]
            _hk_last = text
    _ensure_pool(url)
    return text


# ---------------------------------------------------------------- 用户侧
@router.get("/tips/next")
def next_tip(response: Response):
    """返回一条文案。任何异常都不应让客户端拿不到东西，最差也回落到内置短句。"""
    response.headers["Cache-Control"] = "no-store, max-age=0"
    rows = query_all("SELECT content, weight FROM tips WHERE active = 1 ORDER BY id")
    manual = [
        (_clean_content(r["content"]), max(1, _clamp_weight(r["weight"]) or DEFAULT_MANUAL_WEIGHT))
        for r in rows
        if _clean_content(r["content"])
    ]

    hitokoto_weight = 0
    if _setting("tips.hitokoto_enabled", "1") != "0":
        hitokoto_weight = max(0, _int_setting("tips.hitokoto_weight", DEFAULT_HITOKOTO_WEIGHT))

    total = sum(weight for _, weight in manual) + hitokoto_weight
    if total <= 0:
        return {"ok": True, "content": FALLBACK_TEXT, "source": "fallback"}

    roll = random.uniform(0, total)
    acc = 0.0
    for text, weight in manual:
        acc += weight
        if roll < acc:
            return {"ok": True, "content": text, "source": "manual"}

    text = ""
    if hitokoto_weight > 0:
        url = _hitokoto_url()
        # 直连模式：同步请求一次，耗时就是上游的真实耗时（测试用）
        text = _take_hitokoto(url) if _HK_POOL_ENABLED else _fetch_hitokoto_once(url)
    if text:
        return {"ok": True, "content": text, "source": "hitokoto"}

    # 一言挂了：退回权重最高的手动条目，保证有条目可见。
    if manual:
        best = max(manual, key=lambda item: item[1])
        return {"ok": True, "content": best[0], "source": "manual"}

    return {"ok": True, "content": FALLBACK_TEXT, "source": "fallback"}


# ---------------------------------------------------------------- 管理侧
def _tip_out(row) -> dict:
    return {
        "id": row["id"],
        "content": row["content"] or "",
        "weight": row["weight"] if row["weight"] is not None else DEFAULT_MANUAL_WEIGHT,
        "active": bool(row["active"]),
        "createdAt": row["created_at"],
    }


@router.get("/admin/tips")
def admin_list_tips(_=Depends(require_admin)):
    rows = query_all(
        "SELECT id, content, weight, active, created_at FROM tips ORDER BY weight DESC, id DESC")
    return {
        "ok": True,
        "tips": [_tip_out(r) for r in rows],
        "config": {
            "api": _setting("tips.hitokoto_api", DEFAULT_HITOKOTO_API),
            "hitokotoWeight": max(0, _int_setting("tips.hitokoto_weight", DEFAULT_HITOKOTO_WEIGHT)),
            "hitokotoEnabled": _setting("tips.hitokoto_enabled", "1") != "0",
        },
        "defaults": {
            "api": DEFAULT_HITOKOTO_API,
            "hitokotoWeight": DEFAULT_HITOKOTO_WEIGHT,
            "manualWeight": DEFAULT_MANUAL_WEIGHT,
        },
    }


@router.post("/admin/tips")
def admin_create_tip(payload: dict = Body(...), _=Depends(require_admin)):
    content = _clean_content(payload.get("content"))
    if not content:
        raise HTTPException(status_code=400, detail="内容不能为空")

    weight = _clamp_weight(payload.get("weight", DEFAULT_MANUAL_WEIGHT))
    active = 0 if payload.get("active") is False else 1
    tid = execute(
        "INSERT INTO tips (content, weight, active, created_at) VALUES (?, ?, ?, ?)",
        (content, weight, active, security.now_ms()),
    )
    return {"ok": True, "id": tid}


@router.put("/admin/tips/{tip_id}")
def admin_update_tip(tip_id: int, payload: dict = Body(...), _=Depends(require_admin)):
    row = query_one("SELECT id FROM tips WHERE id = ?", (tip_id,))
    if not row:
        raise HTTPException(status_code=404, detail="条目不存在")

    fields: list[str] = []
    params: list[object] = []

    if "content" in payload:
        content = _clean_content(payload.get("content"))
        if not content:
            raise HTTPException(status_code=400, detail="内容不能为空")
        fields.append("content = ?")
        params.append(content)
    if "weight" in payload:
        fields.append("weight = ?")
        params.append(_clamp_weight(payload.get("weight")))
    if "active" in payload:
        fields.append("active = ?")
        params.append(1 if payload.get("active") else 0)

    if not fields:
        return {"ok": True}

    params.append(tip_id)
    execute_update(f"UPDATE tips SET {', '.join(fields)} WHERE id = ?", params)
    return {"ok": True}


@router.delete("/admin/tips/{tip_id}")
def admin_delete_tip(tip_id: int, _=Depends(require_admin)):
    affected = execute_update("DELETE FROM tips WHERE id = ?", (tip_id,))
    if not affected:
        raise HTTPException(status_code=404, detail="条目不存在")
    return {"ok": True}


@router.post("/admin/tips/config")
def admin_save_config(payload: dict = Body(...), _=Depends(require_admin)):
    if "api" in payload:
        api = str(payload.get("api") or "").strip() or DEFAULT_HITOKOTO_API
        if not api.lower().startswith(("http://", "https://")):
            raise HTTPException(status_code=400, detail="API 地址必须以 http:// 或 https:// 开头")
        _save_setting("tips.hitokoto_api", api[:500])

    if "hitokotoWeight" in payload:
        _save_setting("tips.hitokoto_weight", str(max(0, min(1000, _clamp_weight(payload.get("hitokotoWeight"))))))

    if "hitokotoEnabled" in payload:
        _save_setting("tips.hitokoto_enabled", "1" if payload.get("hitokotoEnabled") else "0")

    # 配置变了就丢掉池子里的旧内容，下一次请求立即按新地址补货。
    global _hk_last
    with _hk_lock:
        _hk_pool.clear()
        _hk_last = ""
    return {"ok": True}
