"""客户端更新管理（自建更新源，不依赖 GitHub）。

管理员在「更新管理」后台上传安装包并填写版本号/更新内容；客户端「检查更新」从这里取
最新版本并下载，实现私有仓库无法公开分发更新产物的问题。

用户侧：
  GET    /api/update/latest          最新版本元数据（版本/更新内容/大小/时间/下载地址/sha256）
  GET    /api/update/download/{id}   下载安装包（自增下载计数）
管理侧（require_admin）：
  GET    /api/admin/releases              版本列表
  POST   /api/admin/releases              上传安装包并置为当前下发版本（multipart）
  POST   /api/admin/releases/activate     切换当前下发版本
  DELETE /api/admin/releases/{id}         删除版本（含文件）
"""
from __future__ import annotations

import hashlib
import pathlib

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse

from .. import security
from ..config import UPDATE_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["update"])

# 安装包上限（普通安装包约 55MB，留足余量）
MAX_INSTALLER = 500 * 1024 * 1024


def _release_out(r) -> dict:
    return {
        "id": r["id"],
        "version": r["version"] or "",
        "notes": r["notes"] or "",
        "fileName": r["file_name"] or "",
        "fileSize": r["file_size"] or 0,
        "sha256": r["sha256"] or "",
        "active": bool(r["active"]),
        "downloadCount": r["download_count"] or 0,
        "updatedAt": r["created_at"],
        "url": f"/api/update/download/{r['id']}",
    }


@router.get("/update/latest")
def latest(user: dict = Depends(get_current_user)):
    """最新下发版本；无版本时 available=False。"""
    row = query_one("SELECT * FROM app_releases WHERE active = 1 ORDER BY id DESC LIMIT 1")
    if not row:
        return {"ok": True, "available": False}
    return {"ok": True, "available": True, **_release_out(row)}


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
def admin_list(admin: dict = Depends(require_admin)):
    rows = query_all("SELECT * FROM app_releases ORDER BY id DESC")
    return {"ok": True, "releases": [_release_out(r) for r in rows]}


@router.post("/admin/releases")
async def admin_upload(file: UploadFile = File(...), version: str = Form(...), notes: str = Form(""),
                       admin: dict = Depends(require_admin)):
    version = version.strip()
    if not version:
        raise HTTPException(400, "请填写版本号")
    data = await file.read()
    if not data:
        raise HTTPException(400, "安装包为空")
    if len(data) > MAX_INSTALLER:
        raise HTTPException(413, "安装包超过 500MB")

    raw_name = file.filename or "setup.exe"
    ext = pathlib.Path(raw_name).suffix.lower()[:12] or ".exe"
    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    (UPDATE_DIR / name).write_bytes(data)
    sha = hashlib.sha256(data).hexdigest()

    # 新上传即置为当前下发版本（其余置 0）
    execute_update("UPDATE app_releases SET active = 0", ())
    rid = execute(
        "INSERT INTO app_releases (version, notes, file_path, file_name, file_size, sha256, active, created_at) "
        "VALUES (?,?,?,?,?,?,1,?)",
        (version, notes or "", f"/uploads/releases/{name}", raw_name, len(data), sha, security.now_ms()),
    )
    return {"ok": True, "id": rid}


@router.post("/admin/releases/activate")
def admin_activate(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    rid = int(payload.get("id") or 0)
    if not query_one("SELECT id FROM app_releases WHERE id = ?", (rid,)):
        raise HTTPException(404, "版本不存在")
    execute_update("UPDATE app_releases SET active = 0", ())
    execute_update("UPDATE app_releases SET active = 1 WHERE id = ?", (rid,))
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
