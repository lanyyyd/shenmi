"""GitHub Release 自动更新：定时检查最新 Release，下载「含极域版本」安装包并设为当前下发版本。

背景：客户端更新源是本服务器（app_releases 表），而安装包由 GitHub Actions 构建后发布到
GitHub Release。这个模块让服务器自己去 Release 拉包，管理员不用每次手动下载再上传。

要点：
1. 只取电脑端安装包：文件名形如 TeaTT-1.0.64-win-x64-setup.exe，排除 nojiyu（无极域）版本；
2. 支持多个 GitHub 加速源（如 https://gh.dayiu.cn），每次下载前逐个测延迟，按最快优先、
   失败自动切下一个；
3. Release 的说明正文直接作为更新公告（notes）下发给客户端，所以正文要保持简短、面向用户；
4. 版本号不倒退：只有比当前下发版本更高时才自动替换。
"""
from __future__ import annotations

import json
import logging
import pathlib
import re
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Optional

import requests

from . import security
from .config import UPDATE_DIR
from .db import execute, execute_update, query_all, query_one

CONFIG_KEY = "gh_update_config"
STATUS_KEY = "gh_update_status"

DEFAULT_CONFIG = {
    "enabled": False,
    "interval": 360,                       # 分钟
    "keep": 5,                             # 每个平台最多保留几个安装包，0=不限
    "repo": "lanyyyd/shenmi",
    "mirrors": ["https://gh.dayiu.cn"],    # 每行一个；可写 https://xxx/{url} 占位
    "token": "",                           # 可选，GitHub API 限流时用
}

UA = "SteamEYA-GH-Updater/1.0"
MAX_INSTALLER = 500 * 1024 * 1024
PROBE_TIMEOUT = 5            # 单个下载源测速上限（秒），含 HEAD 失败后的那次重试
PROBE_BATCH = 10             # 同时测速的节点数
DOWNLOAD_DEADLINE_SECONDS = 60    # 单源下载超过这个时长仍未完成，判定为该源不可用
SOURCE_BLOCK_SECONDS = 24 * 3600  # 被判定不可用的源暂停使用 1 天
BLOCK_KEY = "gh_update_blocks"

DOWNLOAD_TIMEOUT = (10, 60)   # 读超时压到 60 秒：卡住的源尽快判失败并切下一个

log = logging.getLogger("gh_update")

_lock = threading.Lock()
_progress_lock = threading.Lock()
_progress: dict = {"active": False}


def download_progress() -> dict:
    """当前下载进度（内存态，不落库，避免每读一块都写 SQLite）。"""
    with _progress_lock:
        return dict(_progress)


def _progress_start(version: str, asset: str) -> None:
    with _progress_lock:
        _progress.clear()
        _progress.update({"active": True, "version": version, "asset": asset, "source": "",
                          "downloaded": 0, "total": 0, "percent": 0, "speed": 0,
                          "startedTs": int(time.time())})


def _progress_update(**kw) -> None:
    with _progress_lock:
        _progress.update(kw)


def _progress_end() -> None:
    with _progress_lock:
        _progress["active"] = False
        _progress["speed"] = 0


# ---------------------------------------------------------------- 配置读写
def _get_setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str) -> None:
    execute(
        "INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
        (key, value, security.now_ms()),
    )


class _SourceSlow(RuntimeError):
    """单源下载超时/过慢，调用方据此拉黑该源。"""


def load_blocks() -> dict:
    """当前仍在暂停期内的下载源：{label: 过期时间(ms)}。"""
    now = security.now_ms()
    try:
        raw = _get_setting(BLOCK_KEY, "")
        data = json.loads(raw) if raw else {}
    except Exception:
        data = {}
    if not isinstance(data, dict):
        return {}
    out = {}
    for key, exp in data.items():
        try:
            exp = int(exp)
        except (TypeError, ValueError):
            continue
        if exp > now:
            out[str(key)] = exp
    return out


def _block_source(label: str, reason: str = "") -> None:
    """把下载源加入暂停列表，1 天内不再用于下载（原站与加速源一视同仁）。"""
    blocks = load_blocks()
    blocks[label] = security.now_ms() + SOURCE_BLOCK_SECONDS * 1000
    _set_setting(BLOCK_KEY, json.dumps(blocks, ensure_ascii=False))
    log.warning("下载源暂停使用 %d 小时：%s（%s）", SOURCE_BLOCK_SECONDS // 3600, label, reason)


def load_config() -> dict:
    cfg = dict(DEFAULT_CONFIG)
    raw = _get_setting(CONFIG_KEY, "")
    if raw:
        try:
            data = json.loads(raw)
            if isinstance(data, dict):
                cfg.update({k: data[k] for k in data if k in cfg})
        except Exception:
            pass
    cfg["enabled"] = bool(cfg.get("enabled"))
    try:
        cfg["interval"] = max(5, int(cfg.get("interval") or 360))
    except (TypeError, ValueError):
        cfg["interval"] = 360
    mirrors = cfg.get("mirrors") or []
    if isinstance(mirrors, str):
        mirrors = mirrors.splitlines()
    cfg["mirrors"] = [str(m).strip() for m in mirrors if str(m).strip()]
    try:
        cfg["keep"] = max(0, int(_get_setting("release_keep", str(DEFAULT_CONFIG["keep"]))))
    except (TypeError, ValueError):
        cfg["keep"] = DEFAULT_CONFIG["keep"]
    return cfg


def save_config(data: dict) -> dict:
    cfg = load_config()
    if "enabled" in data:
        cfg["enabled"] = bool(data["enabled"])
    if "interval" in data:
        try:
            cfg["interval"] = max(5, int(data["interval"]))
        except (TypeError, ValueError):
            cfg["interval"] = 360
    if "repo" in data:
        repo = str(data["repo"] or "").strip().strip("/")
        if repo:
            cfg["repo"] = repo
    if "mirrors" in data:
        raw = data["mirrors"]
        if isinstance(raw, str):
            raw = raw.splitlines()
        cfg["mirrors"] = [str(m).strip() for m in (raw or []) if str(m).strip()]
    if "token" in data:
        token = str(data["token"] or "").strip()
        # 前端不回显 token；留空表示保持原值。
        if token and not token.startswith("*"):
            cfg["token"] = token
    if "keep" in data:
        try:
            cfg["keep"] = max(0, int(data["keep"]))
        except (TypeError, ValueError):
            cfg["keep"] = DEFAULT_CONFIG["keep"]
        _set_setting("release_keep", str(cfg["keep"]))
    _set_setting(CONFIG_KEY, json.dumps(cfg, ensure_ascii=False))
    return cfg


def load_status() -> dict:
    raw = _get_setting(STATUS_KEY, "")
    if not raw:
        return {}
    try:
        data = json.loads(raw)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def _save_status(**kw) -> dict:
    st = load_status()
    st.update(kw)
    st["updatedTs"] = security.now_ms()
    _set_setting(STATUS_KEY, json.dumps(st, ensure_ascii=False))
    return st


def keep_limit() -> int:
    try:
        return max(0, int(_get_setting("release_keep", str(DEFAULT_CONFIG["keep"]))))
    except (TypeError, ValueError):
        return int(DEFAULT_CONFIG["keep"])


def prune_versions(platform: str = "windows", keep: int | None = None,
                   edition: str | None = None) -> int:
    """只保留最近 keep 个安装包（当前下发版本永远保留），返回清理条数。

    keep=0 表示不限；被清理的版本连同磁盘文件一起删除。edition 为 None 时
    按平台整体清理，指定后只清理该形态（含极域 / 无极域）。
    """
    keep = keep_limit() if keep is None else keep
    if keep <= 0:
        return 0
    rows = query_all(
        "SELECT id, file_path, active, edition FROM app_releases WHERE platform=? ORDER BY id DESC",
        (platform,),
    )
    if edition is not None:
        rows = [r for r in rows if ((r["edition"] if "edition" in r.keys() else "") or "standard") == edition]
    if len(rows) <= keep:
        return 0
    keep_ids = {r["id"] for r in rows if r["active"]}
    for r in rows:
        if len(keep_ids) >= keep:
            break
        keep_ids.add(r["id"])
    removed = 0
    for r in rows:
        if r["id"] in keep_ids:
            continue
        try:
            (UPDATE_DIR / pathlib.Path(r["file_path"] or "").name).unlink(missing_ok=True)
        except Exception:
            pass
        execute_update("DELETE FROM app_releases WHERE id = ?", (r["id"],))
        removed += 1
    return removed


# ---------------------------------------------------------------- GitHub API
def _api_headers(token: str = "") -> dict:
    headers = {"Accept": "application/vnd.github+json", "User-Agent": UA}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return headers


def fetch_latest(cfg: dict) -> dict:
    """取最新**桌面端**正式 Release：版本号、公告正文，以及两个安装包。

    不能直接用 /releases/latest：安卓包（android-v*）和这次新增的服务端包
    （server-*）也会发 Release，一旦它们排到最前，latest 会返回一个没有 exe 的
    版本，自动更新就会报「没有找到安装包」。这里改为遍历列表，只认 v 开头、
    且真的带 Windows 安装包的正式 Release。
    """
    repo = cfg.get("repo") or DEFAULT_CONFIG["repo"]
    url = f"https://api.github.com/repos/{repo}/releases?per_page=30"
    resp = requests.get(url, headers=_api_headers(cfg.get("token") or ""), timeout=20)
    if resp.status_code == 403:
        raise RuntimeError("GitHub API 被限流（403），可在后台填写 GitHub Token 后重试")
    if resp.status_code != 200:
        raise RuntimeError(f"GitHub API 返回 HTTP {resp.status_code}")
    releases = resp.json() or []
    if not isinstance(releases, list):
        raise RuntimeError("GitHub API 返回格式异常")

    for data in releases:
        if not isinstance(data, dict) or data.get("draft") or data.get("prerelease"):
            continue
        tag = str(data.get("tag_name") or "").strip()
        # 桌面端 tag 形如 v1.0.95；android-v* / server-* 一律跳过。
        if not re.match(r"^[vV]\d+(?:\.\d+)*$", tag):
            continue
        assets = _pick_installers(data.get("assets") or [])
        if not assets:
            continue
        return {
            "tag": tag,
            "version": tag.lstrip("vV"),
            "notes": _clean_notes(data.get("body") or ""),
            "assets": assets,
        }
    raise RuntimeError("没有找到带 Windows 安装包的正式 Release")


ASSET_SUFFIXES = {
    "standard": "-win-x64-setup.exe",
    "nojiyu": "-win-x64-nojiyu-setup.exe",
}


def _pick_installers(assets: list) -> dict:
    """返回 {edition: asset}；standard 排除 nojiyu，两个形态各自挑最匹配的 exe。

    结果同时保留 GitHub 原始字段（name/browser_download_url/size）和下载代码使用的
    简写字段（assetName/assetUrl/assetSize）。
    """
    picked: dict = {}
    for a in assets:
        name = str(a.get("name") or "").lower()
        if not name.endswith(".exe"):
            continue
        edition = "nojiyu" if "nojiyu" in name else "standard"
        suffix = ASSET_SUFFIXES[edition]
        score = 2 if name.endswith(suffix) else 1
        if edition not in picked or score > picked[edition]["_score"]:
            picked[edition] = {**a, "_score": score}
    out = {}
    for edition, a in picked.items():
        raw_name = a.get("name") or "setup.exe"
        out[edition] = {
            **{k: v for k, v in a.items() if k != "_score"},
            "assetName": raw_name,
            "assetUrl": a.get("browser_download_url") or "",
            "assetSize": int(a.get("size") or 0),
        }
    return out


def _clean_notes(body: str) -> str:
    """Release 正文即更新公告：去掉多余空行，限制长度，避免客户端弹窗过长。"""
    text = (body or "").replace("\r\n", "\n").strip()
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text[:2000]


# ---------------------------------------------------------------- 加速源 / 下载
def _mirror_url(mirror: str, url: str) -> str:
    m = (mirror or "").strip()
    if not m:
        return url
    if "{url}" in m:
        return m.replace("{url}", url)
    return m.rstrip("/") + "/" + url


def _probe_one(url: str) -> Optional[int]:
    """测一次响应延迟（毫秒）；不可用返回 None。单个节点总共最多等 PROBE_TIMEOUT 秒。

    HEAD 被拒或超时后，只用剩余时间再试一次 Range 请求，不会再叠加一个完整超时，
    避免节点卡住时整体长时间停在「测速中」。
    """
    deadline = time.time() + PROBE_TIMEOUT
    started = time.time()
    try:
        resp = requests.head(url, headers={"User-Agent": UA}, allow_redirects=True,
                             timeout=PROBE_TIMEOUT)
        if resp.status_code < 400:
            return int((time.time() - started) * 1000)
    except Exception:
        pass
    left = deadline - time.time()
    if left <= 0.3:
        return None
    started = time.time()
    try:
        resp = requests.get(url, headers={"User-Agent": UA, "Range": "bytes=0-0"},
                            stream=True, timeout=left)
        try:
            if resp.status_code < 400:
                return int((time.time() - started) * 1000)
        finally:
            resp.close()
    except Exception:
        return None
    return None


def probe_mirrors(asset_url: str, cfg: dict) -> list:
    """返回各下载源（原站 + 加速源）的延迟，按从快到慢排序。

    每批最多并发 PROBE_BATCH 个节点，批内并行、批间串行：
    节点再多，最坏耗时也只是「批数 × 单节点超时」。
    """
    items = [{"label": "GitHub 原站", "url": asset_url}]
    for m in cfg.get("mirrors") or []:
        items.append({"label": m, "url": _mirror_url(m, asset_url)})
    blocks = load_blocks()
    if blocks:
        allowed = [it for it in items if it["label"] not in blocks]
        # 全部源都在暂停期时仍全部尝试一次，避免完全无法下载。
        items = allowed or items
    for i in range(0, len(items), PROBE_BATCH):
        batch = items[i:i + PROBE_BATCH]
        with ThreadPoolExecutor(max_workers=len(batch), thread_name_prefix="gh-probe") as pool:
            for it, ms in zip(batch, pool.map(lambda x: _probe_one(x["url"]), batch)):
                it["ms"] = ms
    return sorted(items, key=lambda x: (x["ms"] is None, x["ms"] or 0))


def _download(asset: dict, cfg: dict, version: str = "") -> tuple[Optional[str], str, list]:
    """按延迟从快到慢依次尝试下载；返回 (本地文件名, 使用的源, 测速结果)。"""
    _progress_start(version or asset.get("assetName", ""), asset.get("assetName", ""))
    try:
        probed = probe_mirrors(asset["assetUrl"], cfg)
        errors = []
        for it in probed:
            if it["ms"] is None:
                errors.append(f"{it['label']} 不可达")
                continue
            try:
                name = _save_asset(it["url"], asset, label=it["label"])
                return name, it["label"], probed
            except _SourceSlow as e:
                _block_source(it["label"], str(e))
                errors.append(f"{it['label']}：{e}，已暂停该源 1 天")
            except Exception as e:
                errors.append(f"{it['label']}: {e}")
        raise RuntimeError("全部下载源均失败：" + "；".join(errors[:4]))
    finally:
        _progress_end()


def _save_asset(url: str, asset: dict, label: str = "") -> str:
    ext = pathlib.Path(asset["assetName"]).suffix.lower()[:12] or ".exe"
    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    dest = UPDATE_DIR / name
    tmp = dest.with_suffix(dest.suffix + ".part")
    written = 0
    deadline_at = time.time() + DOWNLOAD_DEADLINE_SECONDS
    try:
        with requests.get(url, headers={"User-Agent": UA}, stream=True,
                          timeout=DOWNLOAD_TIMEOUT) as resp:
            if resp.status_code >= 400:
                raise RuntimeError(f"HTTP {resp.status_code}")
            ctype = (resp.headers.get("Content-Type") or "").lower()
            if "text/html" in ctype:
                raise RuntimeError("返回的是网页而不是安装包")
            expected = int(asset.get("assetSize") or 0)
            total = int(resp.headers.get("Content-Length") or 0) or expected
            _progress_update(source=label, downloaded=0, total=total, percent=0, speed=0)
            last_t, last_written, speed = time.time(), 0, 0.0
            with open(tmp, "wb") as f:
                for chunk in resp.iter_content(256 * 1024):
                    if not chunk:
                        continue
                    if time.time() > deadline_at:
                        raise _SourceSlow(f"{DOWNLOAD_DEADLINE_SECONDS} 秒内未下载完成")
                    written += len(chunk)
                    if written > MAX_INSTALLER:
                        raise RuntimeError("安装包超过 500MB")
                    f.write(chunk)
                    now = time.time()
                    if now - last_t >= 0.5:
                        inst = (written - last_written) / (now - last_t)
                        speed = inst if speed <= 0 else speed * 0.6 + inst * 0.4
                        last_t, last_written = now, written
                        _progress_update(downloaded=written, speed=int(speed),
                                         percent=round(written * 100.0 / total, 1) if total else 0)
        if expected and written != expected:
            raise RuntimeError(f"文件大小不符（{written}/{expected}）")
        tmp.replace(dest)
        return name
    except Exception as e:
        try:
            tmp.unlink(missing_ok=True)
        except Exception:
            pass
        if isinstance(e, requests.exceptions.ReadTimeout):
            raise _SourceSlow(f"{DOWNLOAD_DEADLINE_SECONDS} 秒内未下载完成") from e
        raise


# ---------------------------------------------------------------- 主流程
def _vtuple(v: str) -> tuple:
    nums = re.findall(r"\d+", str(v or ""))
    return tuple(int(n) for n in nums[:4]) if nums else (0,)


def _current_version(edition: str = "standard") -> tuple[str, str]:
    row = query_one(
        "SELECT version, file_path FROM app_releases WHERE platform='windows' AND active=1 "
        "AND COALESCE(edition, 'standard') = ? ORDER BY id DESC LIMIT 1", (edition,)
    )
    if not row:
        return "", ""
    return row["version"] or "", row["file_path"] or ""


def _publish_edition(edition: str, asset: dict, latest: dict, cfg: dict,
                     cur_version: str, cur_path: str, force: bool) -> dict:
    cur_file = UPDATE_DIR / pathlib.Path(cur_path).name if cur_path else None
    same = _vtuple(latest["version"]) == _vtuple(cur_version)
    file_ok = bool(cur_file and cur_file.exists())
    if not force and same and file_ok:
        return {"edition": edition, "skipped": True,
                "msg": f"{edition} 已是最新版本 {latest['version']}"}
    if _vtuple(latest["version"]) < _vtuple(cur_version):
        return {"edition": edition, "skipped": True,
                "msg": f"{edition} 的 GitHub 版本 {latest['version']} 低于当前下发 {cur_version}，已跳过"}

    saved, source, probed = _download(asset, cfg, latest["version"])
    size = (UPDATE_DIR / saved).stat().st_size
    sha = _sha256(UPDATE_DIR / saved)

    execute_update("UPDATE app_releases SET active=0 WHERE platform='windows' "
                   "AND COALESCE(edition, 'standard') = ?", (edition,))
    rid = execute(
        "INSERT INTO app_releases (version, notes, file_path, file_name, file_size, sha256, "
        "platform, edition, active, created_at) VALUES (?,?,?,?,?,?,?,?,1,?)",
        (latest["version"], latest["notes"], f"/uploads/releases/{saved}",
         asset.get("name") or "setup.exe", size, sha, "windows", edition, security.now_ms()),
    )
    prune_versions("windows", edition=edition)
    return {"edition": edition, "skipped": False, "version": latest["version"], "id": rid,
            "asset": asset.get("name") or "setup.exe", "source": source,
            "mirrors": [{"label": x["label"], "ms": x["ms"]} for x in probed]}


def run_once(force: bool = False) -> dict:
    """检查并下载一次：含极域、无极域两个安装包各自独立下发。"""
    if not _lock.acquire(blocking=False):
        return {"ok": False, "msg": "已有检查在进行中"}
    try:
        cfg = load_config()
        _save_status(lastCheckTs=security.now_ms(), lastResult="checking", message="正在检查 GitHub Release…")
        latest = fetch_latest(cfg)

        results, errors = [], []
        for edition, asset in (latest.get("assets") or {}).items():
            cur_version, cur_path = _current_version(edition)
            try:
                results.append(_publish_edition(edition, asset, latest, cfg,
                                                cur_version, cur_path, force))
            except Exception as e:
                errors.append(f"{edition}: {e}")

        if not results and errors:
            raise RuntimeError("；".join(errors))

        done = [r for r in results if not r.get("skipped")]
        skipped = [r for r in results if r.get("skipped")]
        parts = [f"已更新 {r['edition']} → {r['version']}（来源：{r['source']}）" for r in done]
        parts += [r["msg"] for r in skipped]
        if errors:
            parts.append("失败：" + "；".join(errors))
        msg = "；".join(parts) or "没有可下发的安装包"

        main = next((r for r in done if r["edition"] == "standard"), done[0] if done else {})
        _save_status(
            lastResult="ok" if not errors else ("error" if not done else "ok"),
            message=msg,
            version=latest["version"],
            asset=main.get("asset") or "",
            source=main.get("source") or "",
            releaseId=main.get("id") or 0,
            editions=results,
            mirrors=main.get("mirrors") or [],
        )
        return {"ok": not errors, "version": latest["version"],
                "id": main.get("id") or 0, "msg": msg, "editions": results}
    except Exception as e:
        _save_status(lastResult="error", message=str(e))
        return {"ok": False, "msg": str(e)}
    finally:
        _lock.release()


def _sha256(path: pathlib.Path) -> str:
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- 后台定时器
def start_loop() -> None:
    threading.Thread(target=_loop, name="gh-update", daemon=True).start()


def _loop() -> None:
    # 启动后先等一会儿，避免和建库/其它启动任务抢资源。
    time.sleep(15)
    while True:
        try:
            cfg = load_config()
            if cfg.get("enabled"):
                st = load_status()
                last = int(st.get("lastCheckTs") or 0)
                interval_ms = max(5, int(cfg.get("interval") or 360)) * 60 * 1000
                if security.now_ms() - last >= interval_ms:
                    run_once()
        except Exception:
            pass
        time.sleep(30)
