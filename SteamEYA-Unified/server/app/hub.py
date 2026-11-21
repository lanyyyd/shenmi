"""聊天室实时层：连接注册表 + 广播（对应原 server.js 的 clients 与 broadcast*）。"""
from __future__ import annotations

import asyncio
import json
import logging
from typing import Any, Optional

from . import presence
from . import security
from .db import execute, query_all, query_one

log = logging.getLogger("hub")

# 在线连接超时：客户端每 25 秒发一次心跳，90 秒没有任何心跳就认为已掉线。
# 网关被强杀时 online 表会留下旧行，靠这个 TTL + 启动清理避免「幽灵在线」。
ONLINE_TTL_MS = 90_000


def online_rows() -> list[dict]:
    """返回仍有效的在线连接；顺带清掉超过 TTL 的残留行。"""
    cutoff = security.now_ms() - ONLINE_TTL_MS
    try:
        execute("DELETE FROM online WHERE last_seen < ?", (cutoff,))
    except Exception:
        pass
    return query_all("SELECT * FROM online WHERE last_seen >= ?", (cutoff,))


def online_user_ids() -> set[int]:
    return {r["user_id"] for r in online_rows()}



def display_name(u) -> str:
    if not u:
        return ""
    return (u["nickname"] or u["username"]) if isinstance(u, dict) else (u["nickname"] or u["username"])


class Hub:
    def __init__(self) -> None:
        # conn_id -> {ws, user_id, username, nickname, device_id}
        self.clients: dict[str, dict[str, Any]] = {}
        self._lock = asyncio.Lock()
        # 服务端后台线程（presence）需要借主事件循环广播
        self.loop: asyncio.AbstractEventLoop | None = None

    def set_loop(self, loop: asyncio.AbstractEventLoop) -> None:
        self.loop = loop

    def broadcast_online_threadsafe(self) -> None:
        """供同步线程调用：把最新在线快照推给所有连接。"""
        loop = self.loop
        if loop is None or loop.is_closed():
            return
        try:
            asyncio.run_coroutine_threadsafe(self.broadcast_online(), loop)
        except Exception as e:
            log.debug("在线快照广播失败：%s", e)

    async def add(self, conn_id: str, ws, user: dict, device_id: str, ip: str = "") -> None:
        async with self._lock:
            self.clients[conn_id] = {
                "ws": ws, "user_id": user["id"], "username": user["username"],
                "nickname": user["nickname"] or user["username"], "device_id": device_id,
            }
        execute(
            "INSERT INTO online (conn_id, user_id, device_id, ip, ua, connected_at, last_seen) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            (conn_id, user["id"], device_id, ip, "", security.now_ms(), security.now_ms()),
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
        rows = online_rows()
        users: dict[int, dict] = {}
        for r in rows:
            u = query_one("SELECT * FROM users WHERE id = ?", (r["user_id"],))
            if not u:
                continue
            if r["user_id"] not in users:
                users[r["user_id"]] = {
                    "id": u["id"], "username": u["username"], "nickname": u["nickname"],
                    "avatar": u["avatar"] or "", "displayName": display_name(u), "devices": [],
                    # 在玩状态由 presence 计算（云游戏探测 / Steam Web API），没有时为空串
                    "activity": presence.activity_of(r["user_id"]),
                }
            users[r["user_id"]]["devices"].append({
                "deviceId": r["device_id"], "ip": r["ip"], "ua": r["ua"], "at": r["connected_at"],
            })

        # 正在云游戏 / Steam 但聊天 WebSocket 已断开（App 切到游戏页、被系统挂起）：
        # 心跳还在就仍然算在线，并显示「在玩 xx」，避免一上号就从列表里消失。
        for uid, text in presence.activity_snapshot().items():
            if uid in users:
                continue
            u = query_one("SELECT * FROM users WHERE id = ?", (uid,))
            if not u or not u["is_active"]:
                continue
            users[uid] = {
                "id": u["id"], "username": u["username"], "nickname": u["nickname"],
                "avatar": u["avatar"] or "", "displayName": display_name(u),
                "devices": [], "activity": text,
            }

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