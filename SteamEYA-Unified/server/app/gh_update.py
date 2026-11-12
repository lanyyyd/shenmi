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
import pathlib
import re
import threading
import time
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
PROBE_TIMEOUT = 8
DOWNLOAD_TIMEOUT = (10, 60)   # 读超时压到 60 秒：卡住的源尽快判失败并切下一个

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


def prune_versions(platform: str = "windows", keep: int | None = None) -> int:
    """只保留最近 keep 个安装包（当前下发版本永远保留），返回清理条数。

    keep=0 表示不限；被清理的版本连同磁盘文件一起删除。
    """
    keep = keep_limit() if keep is None else keep
    if keep <= 0:
        return 0
    rows = query_all(
        "SELECT id, file_path, active FROM app_releases WHERE platform=? ORDER BY id DESC",
        (platform,),
    )
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
    """取最新正式 Release：版本号、公告正文、含极域安装包信息。"""
    repo = cfg.get("repo") or DEFAULT_CONFIG["repo"]
    url = f"https://api.github.com/repos/{repo}/releases/latest"
    resp = requests.get(url, headers=_api_headers(cfg.get("token") or ""), timeout=20)
    if resp.status_code == 403:
        raise RuntimeError("GitHub API 被限流（403），可在后台填写 GitHub Token 后重试")
    if resp.status_code != 200:
        raise RuntimeError(f"GitHub API 返回 HTTP {resp.status_code}")
    data = resp.json() or {}
    if data.get("draft") or data.get("prerelease"):
        raise RuntimeError("最新 Release 是草稿/预发布，已跳过")

    tag = str(data.get("tag_name") or "").strip()
    version = tag.lstrip("vV")
    if not version:
        raise RuntimeError("Release 没有版本号")

    asset = _pick_installer(data.get("assets") or [])
    if not asset:
        raise RuntimeError("Release 中没有找到「含极域版本」的 Windows 安装包")
    return {
        "tag": tag,
        "version": version,
        "notes": _clean_notes(data.get("body") or ""),
        "assetName": asset.get("name") or "setup.exe",
        "assetUrl": asset.get("browser_download_url") or "",
        "assetSize": int(asset.get("size") or 0),
    }


def _pick_installer(assets: list) -> Optional[dict]:
    """优先 TeaTT-<ver>-win-x64-setup.exe（含极域），排除 nojiyu。"""
    candidates = [a for a in assets if str(a.get("name") or "").lower().endswith(".exe")
                  and "nojiyu" not in str(a.get("name") or "").lower()]
    if not candidates:
        return None
    for a in candidates:
        name = str(a.get("name") or "").lower()
        if name.endswith("-win-x64-setup.exe"):
            return a
    return candidates[0]


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
    """测一次响应延迟（毫秒）；不可用返回 None。HEAD 不被支持时退回 Range 请求。"""
    started = time.time()
    try:
        resp = requests.head(url, headers={"User-Agent": UA}, allow_redirects=True,
                             timeout=PROBE_TIMEOUT)
        if resp.status_code < 400:
            return int((time.time() - started) * 1000)
    except Exception:
        pass
    try:
        started = time.time()
        resp = requests.get(url, headers={"User-Agent": UA, "Range": "bytes=0-0"},
                            stream=True, timeout=PROBE_TIMEOUT)
        try:
            if resp.status_code < 400:
                return int((time.time() - started) * 1000)
        finally:
            resp.close()
    except Exception:
        return None
    return None


def probe_mirrors(asset_url: str, cfg: dict) -> list:
    """返回各下载源（原站 + 加速源）的延迟，按从快到慢排序。"""
    items = [{"label": "GitHub 原站", "url": asset_url}]
    for m in cfg.get("mirrors") or []:
        items.append({"label": m, "url": _mirror_url(m, asset_url)})
    for it in items:
        it["ms"] = _probe_one(it["url"])
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
    except Exception:
        try:
            tmp.unlink(missing_ok=True)
        except Exception:
            pass
        raise


# ---------------------------------------------------------------- 主流程
def _vtuple(v: str) -> tuple:
    nums = re.findall(r"\d+", str(v or ""))
    return tuple(int(n) for n in nums[:4]) if nums else (0,)


def _current_version() -> tuple[str, str]:
    row = query_one("SELECT version, file_path FROM app_releases "
                    "WHERE platform='windows' AND active=1 ORDER BY id DESC LIMIT 1")
    if not row:
        return "", ""
    return row["version"] or "", row["file_path"] or ""


def run_once(force: bool = False) -> dict:
    """检查并下载一次。force=True 时即使版本相同也重新下载（用于文件缺失修复）。"""
    if not _lock.acquire(blocking=False):
        return {"ok": False, "msg": "已有检查在进行中"}
    try:
        cfg = load_config()
        _save_status(lastCheckTs=security.now_ms(), lastResult="checking", message="正在检查 GitHub Release…")
        latest = fetch_latest(cfg)

        cur_version, cur_path = _current_version()
        cur_file = UPDATE_DIR / pathlib.Path(cur_path).name if cur_path else None
        same = _vtuple(latest["version"]) == _vtuple(cur_version)
        file_ok = bool(cur_file and cur_file.exists())
        if not force and (same and file_ok):
            msg = f"已是最新版本 {latest['version']}"
            _save_status(lastResult="skipped", message=msg, version=latest["version"], asset=latest["assetName"])
            return {"ok": True, "skipped": True, "msg": msg, "version": latest["version"]}
        if _vtuple(latest["version"]) < _vtuple(cur_version):
            msg = f"GitHub 最新 {latest['version']} 低于当前下发版本 {cur_version}，已跳过"
            _save_status(lastResult="skipped", message=msg, version=latest["version"])
            return {"ok": True, "skipped": True, "msg": msg, "version": latest["version"]}

        saved, source, probed = _download(latest, cfg, latest["version"])
        size = (UPDATE_DIR / saved).stat().st_size
        sha = _sha256(UPDATE_DIR / saved)

        execute_update("UPDATE app_releases SET active=0 WHERE platform='windows'")
        rid = execute(
            "INSERT INTO app_releases (version, notes, file_path, file_name, file_size, sha256, platform, active, created_at) "
            "VALUES (?,?,?,?,?,?,?,1,?)",
            (latest["version"], latest["notes"], f"/uploads/releases/{saved}",
             latest["assetName"], size, sha, "windows", security.now_ms()),
        )
        prune_versions("windows")
        msg = f"已自动更新到 {latest['version']}（来源：{source}）"
        _save_status(lastResult="ok", message=msg, version=latest["version"],
                     releaseId=rid, asset=latest["assetName"], source=source,
                     mirrors=[{"label": x["label"], "ms": x["ms"]} for x in probed])
        return {"ok": True, "version": latest["version"], "id": rid, "msg": msg}
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
