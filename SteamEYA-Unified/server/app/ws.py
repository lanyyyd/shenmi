"""WebSocket 实时聊天端点（/ws），对应原 server.js 的 wss 处理。

鉴权：cookie `token` / query `?token=` 会话，或 query `?jwt=`（客户端统一账号）。
"""
from __future__ import annotations

import asyncio
import json
import uuid

from fastapi import APIRouter, WebSocket, WebSocketDisconnect

from . import chat_logic as CL
from . import lan
from . import push
from . import security
from .db import execute, query_one
from .hub import hub

router = APIRouter()


def _resolve(ws: WebSocket):
    token = ws.cookies.get("token") or ws.query_params.get("token")
    device_id = ws.query_params.get("deviceId") or ""
    if token:
        sess = query_one("SELECT * FROM sessions WHERE token = ?", (token,))
        if sess:
            u = query_one("SELECT * FROM users WHERE id = ?", (sess["user_id"],))
            if u and u["is_active"]:
                return dict(u), sess["device_id"]
    jwt = ws.query_params.get("jwt")
    if jwt:
        payload = security.decode_access_token(jwt)
        if payload:
            u = query_one("SELECT * FROM users WHERE id = ?", (int(payload["sub"]),))
            if u and u["is_active"]:
                return dict(u), (device_id or "client")
    return None, None


@router.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await ws.accept()
    user, device_id = _resolve(ws)
    if not user:
        await ws.close(code=4001, reason="unauthorized")
        return

    conn_id = str(uuid.uuid4())
    await hub.add(conn_id, ws, user, device_id or "web")
    await ws.send_text(json.dumps({
        "type": "ready", "userId": user["id"], "username": user["username"],
        "nickname": user["nickname"], "avatar": user["avatar"] or "",
        "displayName": CL.display_name(user), "connId": conn_id,
    }, ensure_ascii=False))
    await hub.broadcast_online()

    try:
        while True:
            raw = await ws.receive_text()
            try:
                m = json.loads(raw)
            except Exception:
                continue

            if m.get("type") == "ping":
                execute("UPDATE online SET last_seen = ? WHERE conn_id = ?", (security.now_ms(), conn_id))
                await ws.send_text(json.dumps({"type": "pong"}))
                continue

            if m.get("type") == "recall":
                rid = int(m.get("id") or 0)
                row = CL.get_msg(rid) if rid > 0 else None
                if not row or row["from_id"] != user["id"] or row["recalled"]:
                    continue
                if security.now_ms() - int(row["created_at"]) > 120000:
                    await ws.send_text(json.dumps({"type": "recall_fail", "id": rid, "reason": "只能撤回 2 分钟内的消息"}))
                    continue
                execute("UPDATE messages SET recalled = 1, recalled_at = ? WHERE id = ?", (security.now_ms(), rid))
                rc = {"type": "recall", "id": rid, "room": row["room"], "toId": row["to_id"], "fromId": row["from_id"]}
                if row["room"] == "group":
                    await hub.broadcast_all(rc)
                else:
                    await hub.send_user(row["from_id"], rc)
                    if row["to_id"]:
                        await hub.send_user(row["to_id"], rc)
                continue

            if m.get("type") != "chat":
                continue

            me = query_one("SELECT * FROM users WHERE id = ?", (user["id"],)) or user
            msg_type = m.get("msgType") if m.get("msgType") in ("text", "image", "file", "video") else "text"
            content = str(m.get("content") or "")[:4000]
            file = m.get("file") if isinstance(m.get("file"), dict) else None

            if m.get("msgType") == "sticker":
                s = query_one("SELECT * FROM stickers WHERE id = ?", (int(m.get("stickerId") or 0),))
                if not s or not s["active"]:
                    continue
                msg_type = "sticker"
                content = ""
                file = {"url": s["file_path"], "name": s["file_name"], "size": s["file_size"]}
                execute("UPDATE stickers SET send_count = send_count + 1 WHERE id = ?", (s["id"],))

            if msg_type == "text" and not content.strip():
                continue
            if msg_type != "text" and not file:
                continue

            to_id = None
            if m.get("to"):
                t = query_one("SELECT * FROM users WHERE id = ?", (int(m["to"]),))
                if not t:
                    continue
                to_id = t["id"]

            mention_str = CL.norm_mentions(m.get("mentions")) if to_id is None else ""
            payload = CL.insert_chat(me["id"], msg_type, content, file, to_id, mention_str)
            await CL.broadcast_chat(payload)
            # 局域网互传：把本机新消息（群聊）异步转发给同网段已发现实例，失败静默、不阻塞聊天。
            lan.forward_to_peers(payload)
            # 命中管理后台推送配置时，异步尽力推送（PushPlus），失败静默、不阻塞聊天。
            try:
                asyncio.create_task(asyncio.to_thread(push.maybe_push, payload))
            except Exception:
                pass
    except WebSocketDisconnect:
        pass
    except Exception:
        pass
    finally:
        await hub.remove(conn_id)
        await hub.broadcast_online()