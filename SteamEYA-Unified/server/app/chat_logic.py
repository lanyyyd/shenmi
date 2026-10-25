"""聊天核心逻辑：消息整形、插入、@提及、未读计算、房间号。"""
from __future__ import annotations

import uuid
from typing import Any, Optional

from . import config, levelcore, security
from .db import execute, query_all, query_one
from .hub import hub


def pm_room(a: int, b: int) -> str:
    x, y = min(a, b), max(a, b)
    return f"pm:{x}:{y}"


def display_name(u) -> str:
    if not u:
        return ""
    return (u["nickname"] or u["username"]) if isinstance(u, dict) else (u["nickname"] or u["username"])


def parse_mentions(s: str) -> list:
    out: list = []
    for x in (s or "").split(","):
        if not x:
            continue
        if x == "all":
            out.append("all")
        else:
            n = int(x) if x.isdigit() else 0
            if n > 0:
                out.append(n)
    return out


def norm_mentions(arr) -> str:
    all_flag = False
    ids: list[int] = []
    for v in (arr or [])[:200]:
        if v in ("all", 0, "0"):
            all_flag = True
            continue
        try:
            n = int(v)
        except Exception:
            continue
        if n > 0 and query_one("SELECT id FROM users WHERE id = ?", (n,)):
            ids.append(n)
    if all_flag:
        return ",all,"
    return ("," + ",".join(str(i) for i in ids) + ",") if ids else ""


def shape_msg(r, viewer) -> dict:
    recalled = bool(r["recalled"])
    is_admin = bool(viewer and viewer.get("is_admin"))
    is_sender = bool(viewer and r["from_id"] == viewer["id"])
    hide = recalled and not is_admin and not is_sender
    from_exp = 0
    try:
        from_exp = int(r["from_exp"] or 0)
    except Exception:
        from_exp = 0
    out = {
        "id": r["id"], "room": r["room"], "fromId": r["from_id"],
        "fromName": r["from_name"], "fromAvatar": r["from_avatar"] or "",
        "fromLevel": levelcore.level_of(from_exp),
        "toId": r["to_id"], "type": "text" if hide else r["type"],
        "content": "" if hide else r["content"],
        "mentions": [] if hide else parse_mentions(r["mentions"]),
        "filePath": None if hide else r["file_path"],
        "fileName": None if hide else r["file_name"],
        "fileSize": None if hide else r["file_size"],
        "recalled": recalled, "createdAt": r["created_at"],
    }
    # 红包消息：内容即红包 id，前端据此渲染红包卡片
    if r["type"] == "redpacket":
        try:
            out["redpacketId"] = int(r["content"] or 0)
        except Exception:
            out["redpacketId"] = 0
    return out


_MSG_SELECT = (
    "SELECT m.*, COALESCE(NULLIF(u.nickname,''), u.username, '已注销用户') AS from_name, "
    "COALESCE(u.avatar,'') AS from_avatar, COALESCE(e.exp, 0) AS from_exp FROM messages m "
    "LEFT JOIN users u ON u.id = m.from_id "
    "LEFT JOIN user_exp e ON e.user_id = m.from_id "
)


def get_msg(msg_id: int):
    return query_one(_MSG_SELECT + "WHERE m.id = ?", (msg_id,))


def insert_chat(from_id: int, msg_type: str, content: str, file: Optional[dict],
                to_id: Optional[int], mentions: str, created_at: Optional[int] = None,
                install_id: str = "", client_msg_id: str = "") -> dict:
    """插入一条聊天消息并返回广播 payload。"""
    me = query_one("SELECT * FROM users WHERE id = ?", (from_id,))
    room = "group" if not to_id else pm_room(from_id, to_id)
    ts = created_at or security.now_ms()
    # 局域网/离线模式：标记为本机待同步消息（联网后由 /api/sync/* 幂等回传）
    if not install_id and config.LAN_MODE:
        install_id = config.INSTALL_ID or "local"
        client_msg_id = client_msg_id or uuid.uuid4().hex
    synced = 1 if not install_id else 0
    mid = execute(
        "INSERT INTO messages (room, from_id, to_id, type, content, file_path, file_name, file_size, "
        "mentions, created_at, install_id, client_msg_id, synced) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (room, from_id, to_id, msg_type, content,
         (file or {}).get("url"), (file or {}).get("name"), (file or {}).get("size"),
         mentions, ts, install_id, client_msg_id, synced),
    )
    return {
        "type": "chat", "id": mid, "room": room, "fromId": from_id,
        "fromName": display_name(me), "fromAvatar": (me["avatar"] if me else "") or "",
        "fromLevel": levelcore.level_of(levelcore.exp_of(from_id)),
        "toId": to_id, "msgType": msg_type, "content": content,
        "mentions": parse_mentions(mentions),
        "filePath": (file or {}).get("url"), "fileName": (file or {}).get("name"),
        "fileSize": (file or {}).get("size"), "createdAt": ts,
    }


async def broadcast_chat(payload: dict) -> None:
    if payload["room"] == "group":
        await hub.broadcast_all(payload)
    else:
        await hub.send_user(payload["fromId"], payload)
        if payload.get("toId"):
            await hub.send_user(payload["toId"], payload)


def unread_for(user_id: int) -> dict:
    rows = query_all(
        "SELECT m.room AS room, COUNT(*) AS c FROM messages m "
        "LEFT JOIN read_state r ON r.user_id = ? AND r.room = m.room "
        "WHERE m.from_id != ? AND m.id > COALESCE(r.last_read_id, 0) GROUP BY m.room",
        (user_id, user_id),
    )
    out: dict[str, int] = {}
    for r in rows:
        if not r["c"]:
            continue
        if r["room"] == "group":
            out["group"] = r["c"]
        elif r["room"].startswith("pm:"):
            out[r["room"]] = r["c"]
    return out


def mention_for(user_id: int) -> bool:
    row = query_one(
        "SELECT COUNT(*) AS c FROM messages m "
        "LEFT JOIN read_state r ON r.user_id = ? AND r.room = m.room "
        "WHERE m.room = 'group' AND m.from_id != ? AND m.id > COALESCE(r.last_read_id, 0) "
        "AND (m.mentions LIKE ? OR m.mentions LIKE '%,all,%')",
        (user_id, user_id, f"%,{user_id},%"),
    )
    return bool(row and row["c"] > 0)