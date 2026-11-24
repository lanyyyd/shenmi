"""客户端更新管理（自建更新源，不依赖 GitHub）。

管理员在「更新管理」后台上传安装包并填写版本号/更新内容；客户端「检查更新」从这里取
最新版本并下载，实现私有仓库无法公开分发更新产物的问题。

按平台分发：`platform` 取 `windows`（桌面端安装包）/ `android`（安卓 APK），
两个平台各自维护一条 active 版本，互不影响。老库与老客户端不带 platform 时按 windows 处理。

用户侧：
  GET    /api/update/latest?platform=android   最新版本元数据（版本/更新内容/大小/时间/下载地址/sha256）
  GET    /api/update/download/{id}             下载安装包（自增下载计数）
管理侧（require_admin）：
  GET    /api/admin/releases?platform=android  版本列表
  POST   /api/admin/releases                   上传安装包并置为该平台当前下发版本（multipart）
  POST   /api/admin/releases/activate          切换该平台当前下发版本
  DELETE /api/admin/releases/{id}              删除版本（含文件）
"""
from __future__ import annotations

import hashlib
import pathlib
import threading

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, Query, UploadFile
from fastapi.responses import FileResponse

from .. import gh_update
from .. import security
from ..config import UPDATE_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["update"])

# 安装包上限（桌面安装包约 55MB、安卓 APK 约 15MB，留足余量）
MAX_INSTALLER = 500 * 1024 * 1024

PLATFORMS = ("windows", "android")
EDITIONS = ("standard", "nojiyu")


def _norm_platform(value: str | None) -> str:
    """非法/缺省一律按 windows，保证老客户端行为不变。"""
    p = (value or "").strip().lower()
    return p if p in PLATFORMS else "windows"


def _norm_edition(value: str | None) -> str:
    """standard=含极域安装包，nojiyu=无极域安装包；手机端没有这个维度。"""
    e = (value or "").strip().lower()
    return e if e in EDITIONS else "standard"


def _release_out(r) -> dict:
    return {
        "id": r["id"],
        "version": r["version"] or "",
        "notes": r["notes"] or "",
        "fileName": r["file_name"] or "",
        "fileSize": r["file_size"] or 0,
        "sha256": r["sha256"] or "",
        "platform": (r["platform"] if "platform" in r.keys() else "") or "windows",
        "edition": (r["edition"] if "edition" in r.keys() else "") or "standard",
        "active": bool(r["active"]),
        "downloadCount": r["download_count"] or 0,
        "updatedAt": r["created_at"],
        "url": f"/api/update/download/{r['id']}",
    }


@router.get("/update/public/latest")
def public_latest(platform: str = Query("windows"), edition: str = Query("standard")):
    """官网/下载页用的公开版本信息（无需登录）。

    只暴露版本号、文件名、大小与下载地址，不含公告正文、sha256、下载次数等内部字段。
    """
    p = _norm_platform(platform)
    e = _norm_edition(edition)
    row = query_one(
        "SELECT id, version, file_name, file_size FROM app_releases "
        "WHERE active = 1 AND platform = ? AND COALESCE(edition, 'standard') = ? "
        "ORDER BY id DESC LIMIT 1", (p, e)
    )
    if not row:
        return {"ok": True, "available": False, "platform": p, "edition": e}
    return {"ok": True, "available": True, "platform": p, "edition": e,
            "version": row["version"] or "",
            "fileName": row["file_name"] or "",
            "fileSize": row["file_size"] or 0,
            "url": f"/api/update/download/{row['id']}"}


@router.get("/update/latest")
def latest(platform: str = Query("windows"), edition: str = Query("standard"),
           user: dict = Depends(get_current_user)):
    """指定平台/版本形态的最新下发版本；无版本时 available=False。"""
    p = _norm_platform(platform)
    e = _norm_edition(edition)
    row = query_one(
        "SELECT * FROM app_releases WHERE active = 1 AND platform = ? "
        "AND COALESCE(edition, 'standard') = ? ORDER BY id DESC LIMIT 1", (p, e)
    )
    if not row:
        return {"ok": True, "available": False, "platform": p, "edition": e}
    return {"ok": True, "available": True, "platform": p, "edition": e, **_release_out(row)}


@router.get("/update/download/{rid}")
def download(rid: int):
    """下载指定版本的安装包（自增下载计数）。"""
    row = query_one("SELECT * FROM app_releases WHERE id = ?", (rid,))
    if not row:
        raise HTTPException(404, "版本不存在")
    path = UPDATE_DIR / pathlib.Path(row["file_path"]).name
    if not path.exists():
        raise HTTPException(404, "安装包文件缺失")
    execute_update("UPDATE app_releases SET download_count = download_count + 1 WHERE id = ?", (rid,))
    return FileResponse(str(path), filename=row["file_name"] or path.name,
                        media_type="application/octet-stream")


# ---------------------------------------------------------------- 管理侧
@router.get("/admin/releases")
def admin_list(platform: str | None = Query(None), edition: str | None = Query(None),
               admin: dict = Depends(require_admin)):
    if platform:
        rows = query_all("SELECT * FROM app_releases WHERE platform = ? ORDER BY id DESC", (_norm_platform(platform),))
        if edition:
            e = _norm_edition(edition)
            rows = [r for r in rows if ((r["edition"] if "edition" in r.keys() else "") or "standard") == e]
    else:
        rows = query_all("SELECT * FROM app_releases ORDER BY id DESC")
    return {"ok": True, "releases": [_release_out(r) for r in rows]}


@router.post("/admin/releases")
async def admin_upload(file: UploadFile = File(...), version: str = Form(...), notes: str = Form(""),
                       platform: str = Form("windows"), edition: str = Form("standard"),
                       admin: dict = Depends(require_admin)):
    version = version.strip()
    if not version:
        raise HTTPException(400, "请填写版本号")
    p = _norm_platform(platform)
    e = _norm_edition(edition) if p == "windows" else "standard"
    data = await file.read()
    if not data:
        raise HTTPException(400, "安装包为空")
    if len(data) > MAX_INSTALLER:
        raise HTTPException(413, "安装包超过 500MB")

    raw_name = file.filename or ("app.apk" if p == "android" else "setup.exe")
    ext = pathlib.Path(raw_name).suffix.lower()[:12] or (".apk" if p == "android" else ".exe")
    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    (UPDATE_DIR / name).write_bytes(data)
    sha = hashlib.sha256(data).hexdigest()

    # 新上传即置为该平台/形态的当前下发版本（不影响含极域/无极域的另一份）
    execute_update("UPDATE app_releases SET active = 0 WHERE platform = ? "
                   "AND COALESCE(edition, 'standard') = ?", (p, e))
    rid = execute(
        "INSERT INTO app_releases (version, notes, file_path, file_name, file_size, sha256, "
        "platform, edition, active, created_at) VALUES (?,?,?,?,?,?,?,?,1,?)",
        (version, notes or "", f"/uploads/releases/{name}", raw_name, len(data), sha, p, e, security.now_ms()),
    )
    gh_update.prune_versions(p, edition=e)
    return {"ok": True, "id": rid, "platform": p, "edition": e}


@router.post("/admin/releases/activate")
def admin_activate(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    rid = int(payload.get("id") or 0)
    row = query_one("SELECT id, platform, edition FROM app_releases WHERE id = ?", (rid,))
    if not row:
        raise HTTPException(404, "版本不存在")
    p = _norm_platform(row["platform"])
    e = _norm_edition(row["edition"] if "edition" in row.keys() else "")
    execute_update("UPDATE app_releases SET active = 0 WHERE platform = ? "
                   "AND COALESCE(edition, 'standard') = ?", (p, e))
    execute_update("UPDATE app_releases SET active = 1 WHERE id = ?", (rid,))
    return {"ok": True, "platform": p, "edition": e}


@router.post("/admin/releases/update")
def admin_update_notes(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """修改已上传版本的更新公告（notes），无需重新上传安装包。"""
    rid = int(payload.get("id") or 0)
    if not query_one("SELECT id FROM app_releases WHERE id = ?", (rid,)):
        raise HTTPException(404, "版本不存在")
    notes = payload.get("notes")
    if notes is None:
        raise HTTPException(400, "缺少更新内容")
    execute_update("UPDATE app_releases SET notes = ? WHERE id = ?", (str(notes)[:5000], rid))
    return {"ok": True}


@router.delete("/admin/releases/{rid}")
def admin_delete(rid: int, admin: dict = Depends(require_admin)):
    row = query_one("SELECT * FROM app_releases WHERE id = ?", (rid,))
    if not row:
        raise HTTPException(404, "版本不存在")
    try:
        (UPDATE_DIR / pathlib.Path(row["file_path"]).name).unlink(missing_ok=True)
    except Exception:
        pass
    execute_update("DELETE FROM app_releases WHERE id = ?", (rid,))
    return {"ok": True}


# ---------------------------------------------------------------- GitHub 自动更新
def _gh_config_out() -> dict:
    cfg = gh_update.load_config()
    out = {k: v for k, v in cfg.items() if k != "token"}
    out["hasToken"] = bool(cfg.get("token"))
    return out


@router.get("/admin/gh-update")
def gh_update_state(admin: dict = Depends(require_admin)):
    """自动更新配置 + 最近一次检查/下载结果 + 当前下载进度。"""
    return {"ok": True, "config": _gh_config_out(), "status": gh_update.load_status(),
            "progress": gh_update.download_progress()}


@router.post("/admin/gh-update/config")
def gh_update_config(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    gh_update.save_config(payload)
    return {"ok": True, "config": _gh_config_out()}


@router.post("/admin/gh-update/run")
def gh_update_run(payload: dict | None = Body(default=None), admin: dict = Depends(require_admin)):
    """立即检查并下载（后台线程执行，接口立即返回）。"""
    force = bool((payload or {}).get("force"))

    def _work():
        gh_update.run_once(force=force)

    threading.Thread(target=_work, name="gh-update-manual", daemon=True).start()
    return {"ok": True, "msg": "已开始检查，稍后刷新状态查看结果"}


@router.get("/admin/gh-update/probe")
def gh_update_probe(admin: dict = Depends(require_admin)):
    """测试各下载源延迟，便于管理员判断加速源是否可用。"""
    cfg = gh_update.load_config()
    try:
        latest = gh_update.fetch_latest(cfg)
    except Exception as e:
        return {"ok": False, "msg": f"获取 Release 失败：{e}"}
    asset = (latest.get("assets") or {}).get("standard") or next(iter((latest.get("assets") or {}).values()), None)
    if not asset:
        return {"ok": False, "msg": "Release 中没有安装包"}
    probed = gh_update.probe_mirrors(asset.get("browser_download_url") or "", cfg)
    return {"ok": True, "version": latest["version"], "asset": asset.get("name") or "",
            "mirrors": probed, "blocked": gh_update.load_blocks()}
