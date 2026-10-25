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

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, Query, UploadFile
from fastapi.responses import FileResponse

from .. import security
from ..config import UPDATE_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["update"])

# 安装包上限（桌面安装包约 55MB、安卓 APK 约 15MB，留足余量）
MAX_INSTALLER = 500 * 1024 * 1024

PLATFORMS = ("windows", "android")


def _norm_platform(value: str | None) -> str:
    """非法/缺省一律按 windows，保证老客户端行为不变。"""
    p = (value or "").strip().lower()
    return p if p in PLATFORMS else "windows"


def _release_out(r) -> dict:
    return {
        "id": r["id"],
        "version": r["version"] or "",
        "notes": r["notes"] or "",
        "fileName": r["file_name"] or "",
        "fileSize": r["file_size"] or 0,
        "sha256": r["sha256"] or "",
        "platform": (r["platform"] if "platform" in r.keys() else "") or "windows",
        "active": bool(r["active"]),
        "downloadCount": r["download_count"] or 0,
        "updatedAt": r["created_at"],
        "url": f"/api/update/download/{r['id']}",
    }


@router.get("/update/latest")
def latest(platform: str = Query("windows"), user: dict = Depends(get_current_user)):
    """指定平台的最新下发版本；无版本时 available=False。"""
    p = _norm_platform(platform)
    row = query_one(
        "SELECT * FROM app_releases WHERE active = 1 AND platform = ? ORDER BY id DESC LIMIT 1", (p,)
    )
    if not row:
        return {"ok": True, "available": False, "platform": p}
    return {"ok": True, "available": True, "platform": p, **_release_out(row)}


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
def admin_list(platform: str | None = Query(None), admin: dict = Depends(require_admin)):
    if platform:
        rows = query_all("SELECT * FROM app_releases WHERE platform = ? ORDER BY id DESC", (_norm_platform(platform),))
    else:
        rows = query_all("SELECT * FROM app_releases ORDER BY id DESC")
    return {"ok": True, "releases": [_release_out(r) for r in rows]}


@router.post("/admin/releases")
async def admin_upload(file: UploadFile = File(...), version: str = Form(...), notes: str = Form(""),
                       platform: str = Form("windows"),
                       admin: dict = Depends(require_admin)):
    version = version.strip()
    if not version:
        raise HTTPException(400, "请填写版本号")
    p = _norm_platform(platform)
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

    # 新上传即置为该平台的当前下发版本（只影响同平台，另一平台的 active 保持不动）
    execute_update("UPDATE app_releases SET active = 0 WHERE platform = ?", (p,))
    rid = execute(
        "INSERT INTO app_releases (version, notes, file_path, file_name, file_size, sha256, platform, active, created_at) "
        "VALUES (?,?,?,?,?,?,?,1,?)",
        (version, notes or "", f"/uploads/releases/{name}", raw_name, len(data), sha, p, security.now_ms()),
    )
    return {"ok": True, "id": rid, "platform": p}


@router.post("/admin/releases/activate")
def admin_activate(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    rid = int(payload.get("id") or 0)
    row = query_one("SELECT id, platform FROM app_releases WHERE id = ?", (rid,))
    if not row:
        raise HTTPException(404, "版本不存在")
    p = _norm_platform(row["platform"])
    execute_update("UPDATE app_releases SET active = 0 WHERE platform = ?", (p,))
    execute_update("UPDATE app_releases SET active = 1 WHERE id = ?", (rid,))
    return {"ok": True, "platform": p}


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
