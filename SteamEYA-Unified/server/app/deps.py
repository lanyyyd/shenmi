"""鉴权依赖：客户端用 Bearer JWT，聊天 Web 前端用 cookie/x-token 会话。

两种凭据都解析为统一的用户字典，业务接口无需关心来源。
"""
from __future__ import annotations

from typing import Optional

from fastapi import Header, HTTPException, Request, status

from . import security
from .db import execute, query_one


def user_public(u) -> dict:
    """统一的对外用户结构。"""
    if u is None:
        return {}
    d = dict(u)
    # 等级 / 经验（由经验按阈值推导；读不到时按 0 / Lv0 兜底，绝不影响登录）
    try:
        from . import levelcore
        exp = levelcore.exp_of(d["id"])
        level = levelcore.level_of(exp)
    except Exception:
        exp, level = 0, 0
    return {
        "id": d["id"],
        "username": d["username"],
        "nickname": d.get("nickname") or d["username"],
        "avatar": d.get("avatar") or "",
        "email": d.get("email") or "",
        "isAdmin": bool(d.get("is_admin")),
        "exp": exp,
        "level": level,
    }


def _load_user(user_id: int):
    return query_one("SELECT * FROM users WHERE id = ?", (user_id,))


def resolve_user(request: Request, authorization: Optional[str]) -> Optional[dict]:
    """按优先级解析：Authorization: Bearer <JWT> → cookie token → x-token 会话。"""
    # 1) JWT
    if authorization and authorization.lower().startswith("bearer "):
        payload = security.decode_access_token(authorization[7:].strip())
        if payload:
            u = _load_user(int(payload["sub"]))
            if u and u["is_active"]:
                return dict(u)
    # 2) 会话 token（cookie / header）
    token = request.cookies.get("token") or request.headers.get("x-token")
    if token:
        sess = query_one("SELECT * FROM sessions WHERE token = ?", (token,))
        if sess:
            u = _load_user(sess["user_id"])
            if u and u["is_active"]:
                execute("UPDATE sessions SET last_seen = ? WHERE token = ?",
                        (security.now_ms(), token))
                return dict(u)
    return None


async def get_current_user(
    request: Request,
    authorization: Optional[str] = Header(default=None),
) -> dict:
    u = resolve_user(request, authorization)
    if not u:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="未登录或凭据已失效")
    return u


def require_admin(request: Request, authorization: Optional[str] = Header(default=None)) -> dict:
    u = resolve_user(request, authorization)
    if not u:
        raise HTTPException(status_code=401, detail="未登录")
    if not u.get("is_admin"):
        raise HTTPException(status_code=403, detail="仅管理员可操作")
    return u