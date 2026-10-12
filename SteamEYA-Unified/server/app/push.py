"""PushPlus 消息推送（尽力而为，失败静默）。

配置存于 settings 表的 push_config（由管理后台 /api/admin/push-config 维护）。
命中监听条件的新消息会推送；节流秒数内的后续消息不再重复提醒。
"""
from __future__ import annotations

import json
import threading
import time

from .db import query_one

_lock = threading.Lock()
_last_push = 0.0


def _config() -> dict:
    row = query_one("SELECT value FROM settings WHERE key = 'push_config'")
    if not row:
        return {}
    try:
        return json.loads(row["value"])
    except Exception:
        return {}


def _summary(payload: dict) -> str:
    msg_type = payload.get("msgType") or payload.get("type")
    if msg_type == "image":
        return "[图片]"
    if msg_type == "video":
        return f"[视频] {payload.get('fileName') or ''}".strip()
    if msg_type == "file":
        return f"[文件] {payload.get('fileName') or ''}".strip()
    if msg_type == "sticker":
        return "[表情]"
    return str(payload.get("content") or "")


def maybe_push(payload: dict) -> None:
    """按管理后台配置推送一条新消息；任何异常都吞掉，不影响聊天主流程。"""
    try:
        cfg = _config()
        if not cfg.get("enabled") or not cfg.get("token"):
            return

        from_name = payload.get("fromName") or ""
        if from_name and from_name in (cfg.get("excludeUsers") or []):
            return

        room = payload.get("room")
        if room == "group":
            if not cfg.get("watchGroup"):
                return
            context = "群聊大厅"
        else:
            to_id = payload.get("toId")
            if not to_id:
                return
            u = query_one("SELECT username FROM users WHERE id = ?", (to_id,))
            if not u or u["username"] not in (cfg.get("watchUsers") or []):
                return
            context = f"私聊 → {from_name}"

        throttle = int(cfg.get("throttleSeconds") or 0)
        global _last_push
        now = time.time()
        with _lock:
            if throttle and now - _last_push < throttle:
                return
            _last_push = now

        try:
            import requests

            body = {
                "token": cfg["token"],
                "title": f"{context} · {from_name}",
                "content": _summary(payload),
                "template": cfg.get("template") or "markdown",
            }
            if cfg.get("topic"):
                body["topic"] = cfg["topic"]
            requests.post("https://www.pushplus.plus/send", json=body, timeout=8)
        except Exception:
            pass
    except Exception:
        pass