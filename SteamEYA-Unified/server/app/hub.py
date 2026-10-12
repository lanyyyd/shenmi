"""聊天室实时层：连接注册表 + 广播（对应原 server.js 的 clients 与 broadcast*）。"""
from __future__ import annotations

import asyncio
import json
import logging
from typing import Any, Optional

from . import security
from .db import execute, query_all, query_one

log = logging.getLogger("hub")


def display_name(u) -> str:
    if not u:
        return ""
    return (u["nickname"] or u["username"]) if isinstance(u, dict) else (u["nickname"] or u["username"])


class Hub:
    def __init__(self) -> None:
        # conn_id -> {ws, user_id, username, nickname, device_id}
        self.clients: dict[str, dict[str, Any]] = {}
        self._lock = asyncio.Lock()

    async def add(self, conn_id: str, ws, user: dict, device_id: str) -> None:
        async with self._lock:
            self.clients[conn_id] = {
                "ws": ws, "user_id": user["id"], "username": user["username"],
                "nickname": user["nickname"] or user["username"], "device_id": device_id,
            }
        execute(
            "INSERT INTO online (conn_id, user_id, device_id, ip, ua, connected_at, last_seen) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (conn_id, user["id"], device_id, "", "", security.now_ms(), security.now_ms()),
        )

    async def remove(self, conn_id: str) -> None:
        async with self._lock:
            self.clients.pop(conn_id, None)
        execute("DELETE FROM online WHERE conn_id = ?", (conn_id,))

    async def kick_user(self, user_id: int) -> None:
        targets = [cid for cid, c in self.clients.items() if c["user_id"] == user_id]
        for cid in targets:
            c = self.clients.get(cid)
            if c:
                try:
                    await c["ws"].close(code=4003, reason="session ended")
                except Exception:
                    pass
            await self.remove(cid)
        execute("DELETE FROM online WHERE user_id = ?", (user_id,))
        await self.broadcast_online()

    # ---------------- 发送 ----------------
    async def _send(self, ws, obj: dict) -> None:
        try:
            await ws.send_text(json.dumps(obj, ensure_ascii=False))
        except Exception:
            pass

    async def send_user(self, user_id: int, obj: dict) -> None:
        for c in list(self.clients.values()):
            if c["user_id"] == user_id:
                await self._send(c["ws"], obj)

    async def broadcast_all(self, obj: dict, except_conn: Optional[str] = None) -> None:
        for cid, c in list(self.clients.items()):
            if cid != except_conn:
                await self._send(c["ws"], obj)

    # ---------------- 快照 ----------------
    def online_snapshot(self) -> dict:
        rows = query_all("SELECT * FROM online")
        users: dict[int, dict] = {}
        for r in rows:
            u = query_one("SELECT * FROM users WHERE id = ?", (r["user_id"],))
            if not u:
                continue
            if r["user_id"] not in users:
                users[r["user_id"]] = {
                    "id": u["id"], "username": u["username"], "nickname": u["nickname"],
                    "avatar": u["avatar"] or "", "displayName": display_name(u), "devices": [],
                }
            users[r["user_id"]]["devices"].append({
                "deviceId": r["device_id"], "ip": r["ip"], "ua": r["ua"], "at": r["connected_at"],
            })
        lst = list(users.values())
        return {"users": lst, "onlineUsers": len(lst), "onlineDevices": len(rows)}

    async def broadcast_online(self) -> None:
        await self.broadcast_all({"type": "online", **self.online_snapshot()})

    async def broadcast_read(self, user: dict, room: str, last_read_id: int) -> None:
        payload = {
            "type": "read", "room": room, "userId": user["id"],
            "username": user["nickname"] or user["username"], "lastReadId": last_read_id,
        }
        if room == "group":
            await self.broadcast_all(payload)
            return
        m = room.split(":")
        if len(m) == 3:
            await self.send_user(int(m[1]), payload)
            await self.send_user(int(m[2]), payload)


hub = Hub()