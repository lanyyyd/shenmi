"""离线同步模块：把离线（局域网）期间产生的消息幂等同步到服务器。

要点：
1. 消息一律使用**真实发送时间 created_at** 落库，不用联网时刻；
2. 以 (install_id, client_msg_id) 唯一约束做**幂等去重**，重复提交不产生重复消息；
3. 离线昵称=在线用户名：可用 from_username 指定消息归属账号。
"""
from __future__ import annotations

from typing import Optional

from fastapi import APIRouter, Body, Depends, Header, Request

from .. import chat_logic as CL
from .. import security
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, resolve_user

router = APIRouter(prefix="/api/sync", tags=["sync"])


@router.post("/push")
def push(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    install_id = str(payload.get("install_id") or "").strip()
    messages = payload.get("messages") or []
    if not install_id:
        return {"ok": False, "msg": "缺少 install_id"}
    accepted, skipped = 0, 0
    for m in messages:
        cid = str(m.get("client_msg_id") or "").strip()
        if not cid:
            skipped += 1
            continue
        # 幂等：同一台机器的同一条消息只入库一次
        if query_one("SELECT id FROM messages WHERE install_id=? AND client_msg_id=?", (install_id, cid)):
            skipped += 1
            continue
        # 归属账号：优先 from_username（离线昵称映射），否则用当前登录账号
        sender = user
        frm = str(m.get("from_username") or "").strip()
        if frm and frm != user["username"]:
            row = query_one("SELECT * FROM users WHERE username = ?", (frm,))
            if row:
                sender = dict(row)
        # 目标
        to_id = None
        room = str(m.get("room") or "group")
        if room != "group":
            tname = m.get("to_username") or ""
            trow = query_one("SELECT * FROM users WHERE username = ?", (str(tname),)) if tname else None
            to_id = trow["id"] if trow else None
        msg_type = m.get("type") if m.get("type") in ("text", "image", "file", "video", "sticker") else "text"
        file = m.get("file") if isinstance(m.get("file"), dict) else None
        content = str(m.get("content") or "")[:4000]
        mentions = CL.norm_mentions(m.get("mentions")) if to_id is None else ""
        # 关键：使用离线真实时间
        try:
            created_at = int(m.get("created_at") or 0) or security.now_ms()
        except Exception:
            created_at = security.now_ms()
        execute(
            "INSERT INTO messages (room, from_id, to_id, type, content, file_path, file_name, file_size, "
            "mentions, created_at, install_id, client_msg_id, synced) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,1)",
            (CL.pm_room(sender["id"], to_id) if to_id else "group",
             sender["id"], to_id, msg_type, content,
             (file or {}).get("url"), (file or {}).get("name"), (file or {}).get("size"),
             mentions, created_at, install_id, cid),
        )
        accepted += 1
    return {"ok": True, "accepted": accepted, "skipped": skipped}


@router.get("/pull")
def pull(request: Request, since: int = 0, limit: int = 200,
         authorization: Optional[str] = Header(default=None)):
    u = resolve_user(request, authorization)
    if not u:
        return {"ok": False, "msg": "未登录"}
    limit = min(limit, 500)
    rows = query_all(
        "SELECT m.*, COALESCE(NULLIF(w.nickname,''), w.username) AS from_name, COALESCE(w.avatar,'') AS from_avatar "
        "FROM messages m JOIN users w ON w.id = m.from_id "
        "WHERE m.id > ? AND (m.room='group' OR m.from_id=? OR m.to_id=?) "
        "ORDER BY m.id ASC LIMIT ?",
        (since, u["id"], u["id"], limit),
    )
    lst = [CL.shape_msg(r, u) for r in rows]
    return {"ok": True, "messages": lst, "maxId": (rows[-1]["id"] if rows else since)}


@router.get("/pending")
def pending(install_id: str = ""):
    """本机待同步消息（在本地网关读取，供客户端拿到后推送到远端）。"""
    if not install_id:
        return {"ok": False, "msg": "缺少 install_id"}
    rows = query_all(
        "SELECT m.*, COALESCE(NULLIF(w.nickname,''), w.username) AS from_name, COALESCE(w.avatar,'') AS from_avatar "
        "FROM messages m JOIN users w ON w.id = m.from_id "
        "WHERE m.install_id = ? AND m.synced = 0 ORDER BY m.created_at ASC",
        (install_id,),
    )
    out = []
    for r in rows:
        to_username = ""
        if r["to_id"]:
            tu = query_one("SELECT username FROM users WHERE id = ?", (r["to_id"],))
            to_username = tu["username"] if tu else ""
        out.append({
            "client_msg_id": r["client_msg_id"],
            "room": r["room"],
            "to_username": to_username,
            "from_username": r["from_name"],
            "type": r["type"],
            "content": r["content"],
            "mentions": CL.parse_mentions(r["mentions"]),
            "file": ({"url": r["file_path"], "name": r["file_name"], "size": r["file_size"]}
                     if r["file_path"] else None),
            "created_at": r["created_at"],
        })
    return {"ok": True, "messages": out, "count": len(out)}


@router.post("/mark_synced")
def mark_synced(payload: dict = Body(...)):
    """标记本机消息已同步（在本地网关调用），保证第二个用户不再重复上传。"""
    install_id = str(payload.get("install_id") or "")
    ids = payload.get("client_msg_ids") or []
    if not install_id or not ids:
        return {"ok": False, "msg": "缺少参数"}
    marked = 0
    for cid in ids:
        marked += execute_update("UPDATE messages SET synced = 1 WHERE install_id = ? AND client_msg_id = ?",
                                 (install_id, str(cid)))
    return {"ok": True, "marked": marked}