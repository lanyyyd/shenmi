"""云壁纸库：后台统一上传，客户端设置页直接挑选使用。

文件落在 UPLOAD_DIR/wallpapers，通过 /uploads 静态目录直出；数据库只存标题和文件名。
"""
from __future__ import annotations

import pathlib

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, UploadFile

from .. import security
from ..config import UPLOAD_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import require_admin

router = APIRouter(prefix="/api/wallpapers", tags=["wallpapers"])

WALL_DIR = UPLOAD_DIR / "wallpapers"
WALL_DIR.mkdir(parents=True, exist_ok=True)

_IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
_MAX_SIZE = 20 * 1024 * 1024


def _row_to_dict(row: dict) -> dict:
    return {
        "id": row["id"],
        "title": row["title"] or "",
        "url": f"/uploads/wallpapers/{row['file_name']}",
        "size": row["size"] or 0,
        "created_at": row["created_at"] or 0,
    }


@router.get("")
def list_wallpapers():
    """客户端设置页的壁纸列表。图片本身就是公开静态资源，这里不强制登录。"""
    rows = query_all("SELECT * FROM wallpapers ORDER BY id DESC")
    return {"ok": True, "wallpapers": [_row_to_dict(r) for r in rows]}


@router.post("/admin/upload")
async def admin_upload(file: UploadFile = File(...), title: str = Form(""),
                       admin: dict = Depends(require_admin)):
    ext = pathlib.Path(file.filename or "").suffix.lower()
    if ext not in _IMG_EXT:
        raise HTTPException(400, "只支持 png / jpg / jpeg / gif / webp / bmp")
    data = await file.read()
    if not data:
        raise HTTPException(400, "文件为空")
    if len(data) > _MAX_SIZE:
        raise HTTPException(400, "图片不能超过 20MB")

    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    (WALL_DIR / name).write_bytes(data)
    wid = execute(
        "INSERT INTO wallpapers (title, file_name, size, created_by, created_at) VALUES (?,?,?,?,?)",
        ((title or pathlib.Path(file.filename or "").stem)[:60], name, len(data), admin["id"], security.now_s()),
    )
    return {"ok": True, "id": wid, "url": f"/uploads/wallpapers/{name}"}


@router.put("/admin/{wid}")
def admin_rename(wid: int, payload: dict = Body(...), admin: dict = Depends(require_admin)):
    row = query_one("SELECT id FROM wallpapers WHERE id=?", (wid,))
    if not row:
        raise HTTPException(404, "壁纸不存在")
    title = str(payload.get("title") or "").strip()[:60]
    execute_update("UPDATE wallpapers SET title=? WHERE id=?", (title, wid))
    return {"ok": True}


@router.delete("/admin/{wid}")
def admin_delete(wid: int, admin: dict = Depends(require_admin)):
    row = query_one("SELECT file_name FROM wallpapers WHERE id=?", (wid,))
    if not row:
        raise HTTPException(404, "壁纸不存在")
    execute_update("DELETE FROM wallpapers WHERE id=?", (wid,))
    try:
        (WALL_DIR / row["file_name"]).unlink(missing_ok=True)
    except Exception:
        # 文件删不掉不影响列表，最多留一个孤儿文件
        pass
    return {"ok": True}
