"""「小火人」AI 助手接口。

- GET  /api/ai/config        公开：是否启用 / 名字 / AI 账号 id（前端据此把小火人置顶）
- GET  /api/admin/ai-config  后台读取 AI 配置（API Key / 模型 / URL / 人设 / 主动发言开关…）
- POST /api/admin/ai-config  后台保存 AI 配置

私聊回复与群聊 @ 回复由 ws.py 在消息落库后触发（见 aicore.maybe_reply），不经 HTTP。
"""
from __future__ import annotations

from fastapi import APIRouter, Body, Depends

from .. import aicore
from ..deps import require_admin

router = APIRouter(prefix="/api", tags=["ai"])


@router.get("/ai/config")
def public_config():
    cfg = aicore.get_config()
    u = aicore.ai_user()
    return {
        "ok": True,
        "enabled": bool(cfg.get("enabled")),
        "name": aicore.display_name(),
        "userId": int(u["id"]) if u else 0,
        "avatar": (u["avatar"] if u else "") or "",
        "activeSpeak": bool(cfg.get("active_speak")),
    }


@router.get("/admin/ai-config")
def admin_get_config(user: dict = Depends(require_admin)):
    return {"ok": True, "config": aicore.get_config()}


@router.post("/admin/ai-config")
def admin_save_config(payload: dict = Body(...), user: dict = Depends(require_admin)):
    cfg = aicore.save_config(payload or {})
    return {"ok": True, "config": cfg}
