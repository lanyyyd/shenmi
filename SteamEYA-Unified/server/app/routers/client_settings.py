"""按账号同步客户端个性化设置（主题、主题色、自定义背景与不透明度、通知偏好）。

背景文件本身单独上传：每个用户只在 /uploads/backgrounds 下保留一份最新文件，
设置 JSON 里记录 sha256 + 文件名，客户端按哈希判断是否需要重新下载。
"""
from __future__ import annotations

import hashlib
import json
import pathlib

from fastapi import APIRouter, Body, Depends, File, UploadFile

from .. import security
from ..config import UPLOAD_DIR
from ..db import execute, query_one
from ..deps import get_current_user

router = APIRouter(prefix="/api/client-settings", tags=["client-settings"])

BG_DIR = UPLOAD_DIR / "backgrounds"
BG_DIR.mkdir(parents=True, exist_ok=True)

_MAX_BG = 60 * 1024 * 1024
_BG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
           ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv"}


def _read_json(user_id: int) -> tuple[dict, int]:
    row = query_one("SELECT value, updated_at FROM user_client_settings WHERE user_id = ?", (user_id,))
    if not row:
        return {}, 0
    try:
        data = json.loads(row["value"] or "{}")
        if not isinstance(data, dict):
            data = {}
    except Exception:
        data = {}
    return data, int(row["updated_at"] or 0)


def _write_json(user_id: int, data: dict) -> int:
    now = security.now_ms()
    execute(
        "INSERT INTO user_client_settings (user_id, value, updated_at) VALUES (?,?,?) "
        "ON CONFLICT(user_id) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at",
        (user_id, json.dumps(data, ensure_ascii=False), now),
    )
    return now


def _background_info(user_id: int) -> dict | None:
    """按当前用户找已上传的背景文件（文件名带随机后缀，同一用户只保留一份）。"""
    for path in BG_DIR.glob(f"user-{user_id}-*"):
        return {
            "name": path.name,
            "url": f"/uploads/backgrounds/{path.name}",
            "size": path.stat().st_size if path.exists() else 0,
        }
    return None


@router.get("")
def get_settings(user: dict = Depends(get_current_user)):
    data, updated_at = _read_json(user["id"])
    bg = _background_info(user["id"])
    if bg:
        stored = data.get("background") or {}
        if stored.get("name") == bg["name"] and stored.get("sha256"):
            bg["sha256"] = stored["sha256"]
        else:
            try:
                bg["sha256"] = hashlib.sha256((BG_DIR / bg["name"]).read_bytes()).hexdigest()
            except Exception:
                bg.pop("sha256", None)
        data["background"] = bg
    else:
        data.pop("background", None)
    return {"ok": True, "settings": data or None, "updatedAt": updated_at}


@router.put("")
def put_settings(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    incoming = payload.get("settings")
    if not isinstance(incoming, dict):
        return {"ok": False, "error": "缺少 settings"}
    # 背景只能走上传接口，避免客户端用任意 URL 覆盖。
    incoming.pop("background", None)
    old, _ = _read_json(user["id"])
    bg = old.get("background")
    if bg:
        incoming["background"] = bg
    now = _write_json(user["id"], incoming)
    return {"ok": True, "updatedAt": now}


@router.post("/background")
async def upload_background(file: UploadFile = File(...), user: dict = Depends(get_current_user)):
    ext = pathlib.Path(file.filename or "").suffix.lower()
    if ext not in _BG_EXT:
        return {"ok": False, "error": "只支持图片或视频格式"}
    data = await file.read()
    if not data:
        return {"ok": False, "error": "文件为空"}
    if len(data) > _MAX_BG:
        return {"ok": False, "error": "背景文件不能超过 60MB"}

    for old in BG_DIR.glob(f"user-{user['id']}-*"):
        try:
            old.unlink(missing_ok=True)
        except Exception:
            pass

    name = f"user-{user['id']}-{security.gen_token(8)}{ext}"
    (BG_DIR / name).write_bytes(data)
    info = {
        "name": name,
        "url": f"/uploads/backgrounds/{name}",
        "size": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
    }
    stored, _ = _read_json(user["id"])
    stored["background"] = info
    _write_json(user["id"], stored)
    return {"ok": True, "background": info}


@router.delete("/background")
def delete_background(user: dict = Depends(get_current_user)):
    for old in BG_DIR.glob(f"user-{user['id']}-*"):
        try:
            old.unlink(missing_ok=True)
        except Exception:
            pass
    stored, _ = _read_json(user["id"])
    stored.pop("background", None)
    _write_json(user["id"], stored)
    return {"ok": True}
