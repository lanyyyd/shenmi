"""云存档模块：游戏存档（zip）的上传 / 下载 / 备注 / 共享。

支持游戏：植物大战僵尸(pvz) / 我的世界(minecraft)。
- 只接受单个 .zip 文件（服务端强校验扩展名，客户端另有拖拽限制）；
- 存档按创建时间命名（Asia/Shanghai，如 2026年10月11日14:32），可自定义；
- 共享后所有用户可在该游戏的「共享存档」中看到上传者与时间，备注跟随上传者自己的备注。
"""
from __future__ import annotations

import pathlib
import uuid
from datetime import datetime, timedelta, timezone
from typing import Optional

from fastapi import APIRouter, Body, Depends, File, Form, Header, Request, UploadFile
from fastapi.responses import FileResponse

from .. import security
from ..config import DATA_DIR
from ..db import execute, query_all, query_one
from ..deps import get_current_user, resolve_user

router = APIRouter(prefix="/api/save", tags=["save"])

SAVE_DIR = DATA_DIR / "saves"
SAVE_DIR.mkdir(parents=True, exist_ok=True)

GAMES = {"pvz": "植物大战僵尸", "minecraft": "我的世界"}
_TZ = timezone(timedelta(hours=8))


def _default_name(ms: int) -> str:
    dt = datetime.fromtimestamp(ms / 1000, _TZ)
    return f"{dt.year}年{dt.month}月{dt.day}日{dt.hour:02d}:{dt.minute:02d}"


def _item(row, owner_name: str = "") -> dict:
    return {
        "id": row["id"], "game": row["game"], "name": row["name"], "remark": row["remark"] or "",
        "size": row["file_size"], "fileName": row["file_name"], "shared": bool(row["shared"]),
        "sharedAt": row["shared_at"], "createdAt": row["created_at"],
        "ownerId": row["user_id"], "ownerName": owner_name,
    }


@router.get("/games")
def games():
    return {"ok": True, "games": [{"key": k, "name": v} for k, v in GAMES.items()]}


@router.post("/upload")
async def upload(game: str = Form(...), file: UploadFile = File(...),
                 name: str = Form(""), remark: str = Form(""), source: str = Form("auto"),
                 request: Request = None, authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    if game not in GAMES:
        return {"ok": False, "msg": "未知游戏"}
    fname = file.filename or ""
    if pathlib.Path(fname).suffix.lower() != ".zip":
        return {"ok": False, "msg": "仅支持单个 .zip 压缩包"}
    data = await file.read()
    if not data:
        return {"ok": False, "msg": "文件为空"}
    now = security.now_ms()
    item_name = (name or "").strip()[:60] or _default_name(now)
    safe_id = uuid.uuid4().hex
    rel = f"{user['id']}/{game}/{safe_id}.zip"
    abs_path = SAVE_DIR / rel
    abs_path.parent.mkdir(parents=True, exist_ok=True)
    abs_path.write_bytes(data)
    sid = execute(
        "INSERT INTO cloud_saves (user_id, game, name, remark, file_path, file_name, file_size, "
        "shared, source, created_at) VALUES (?,?,?,?,?,?,?,0,?,?)",
        (user["id"], game, item_name, remark[:200], str(abs_path), fname[:200], len(data),
         "drag" if source == "drag" else "auto", now),
    )
    row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
    return {"ok": True, "save": _item(row, user["nickname"] or user["username"])}


@router.get("/list")
def list_saves(game: str = "pvz", request: Request = None,
               authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    rows = query_all("SELECT * FROM cloud_saves WHERE user_id=? AND game=? ORDER BY created_at DESC",
                     (user["id"], game))
    return {"ok": True, "saves": [_item(r) for r in rows]}


@router.get("/shared")
def list_shared(game: str = "pvz", request: Request = None,
                authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    rows = query_all(
        "SELECT s.*, COALESCE(NULLIF(u.nickname,''), u.username) AS owner_name "
        "FROM cloud_saves s JOIN users u ON u.id = s.user_id "
        "WHERE s.game=? AND s.shared=1 ORDER BY s.shared_at DESC, s.created_at DESC", (game,))
    return {"ok": True, "saves": [_item(r, r["owner_name"]) for r in rows]}


@router.get("/download")
def download(id: int, request: Request = None, authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (id,))
    if not row:
        return {"ok": False, "msg": "存档不存在"}
    if row["user_id"] != user["id"] and not row["shared"] and not user.get("is_admin"):
        return {"ok": False, "msg": "无权下载该存档"}
    p = pathlib.Path(row["file_path"])
    if not p.exists():
        return {"ok": False, "msg": "存档文件已丢失"}
    return FileResponse(str(p), media_type="application/zip", filename=f"{row['name']}.zip")


@router.post("/remark")
def set_remark(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    sid = int(payload.get("id") or 0)
    row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
    if not row or row["user_id"] != user["id"]:
        return {"ok": False, "msg": "存档不存在或无权操作"}
    execute("UPDATE cloud_saves SET remark = ? WHERE id = ?", (str(payload.get("remark") or "")[:200], sid))
    return {"ok": True}


@router.post("/share")
def share(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    ids = [int(i) for i in (payload.get("ids") or [])]
    shared = bool(payload.get("shared", True))
    if not ids:
        return {"ok": False, "msg": "未选择存档"}
    now = security.now_ms()
    changed = 0
    for sid in ids:
        row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
        if not row or row["user_id"] != user["id"]:
            continue
        execute("UPDATE cloud_saves SET shared = ?, shared_at = ? WHERE id = ?",
                (1 if shared else 0, now if shared else None, sid))
        changed += 1
    return {"ok": True, "changed": changed}


@router.post("/delete")
def delete(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    ids = [int(i) for i in (payload.get("ids") or [])]
    removed = 0
    for sid in ids:
        row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
        if not row or row["user_id"] != user["id"]:
            continue
        try:
            pathlib.Path(row["file_path"]).unlink(missing_ok=True)
        except Exception:
            pass
        execute("DELETE FROM cloud_saves WHERE id = ?", (sid,))
        removed += 1
    return {"ok": True, "removed": removed}