"""统一账号中心：客户端走 JWT 的注册/登录/我。

Web 聊天前端对应的 /api/register|login 会话式接口在 routers/chat.py。
两套接口共用同一张 users 表与同一套密码哈希。
"""
from __future__ import annotations

import os
import re

from fastapi import APIRouter, Body, Depends, HTTPException, Request

from .. import security
from ..db import execute, query_one
from ..deps import get_current_user, user_public
# 邀请码校验与网页版注册共用同一套逻辑（同一张 invite_codes 表与后台开关）。
from .chat import _consume_invite, _get_invite_config

router = APIRouter(prefix="/api/auth", tags=["auth"])

_USERNAME_RE = re.compile(r"^[\u4e00-\u9fa5\w-]{2,20}$")


def _client_ip(request: Request) -> str:
    xf = (request.headers.get("x-forwarded-for") or "").split(",")[0].strip()
    return xf or (request.client.host if request.client else "")


def _create_user(username: str, password: str, nickname: str, email: str, ip: str) -> int:
    uid = execute(
        "INSERT INTO users (username, password_hash, nickname, email, created_at, reg_ip) "
        "VALUES (?, ?, ?, ?, ?, ?)",
        (username, security.hash_password(password), nickname or username, email, security.now_ms(), ip),
    )
    return uid


def _login_result(u) -> dict:
    token = security.create_access_token(u["id"], u["username"], bool(u["is_admin"]))
    return {"access_token": token, "token_type": "bearer", "user": user_public(u)}


@router.post("/register")
def register(request: Request, payload: dict = Body(...)):
    username = str(payload.get("username", "")).strip()
    password = str(payload.get("password", ""))
    nickname = str(payload.get("nickname", "")).strip()[:20]
    email = str(payload.get("email", "")).strip()[:120]
    if not _USERNAME_RE.match(username):
        raise HTTPException(400, "用户名需 2-20 位（中文/字母/数字/下划线）")
    if len(password) < 6:
        raise HTTPException(400, "密码至少 6 位")
    if query_one("SELECT id FROM users WHERE username = ?", (username,)):
        raise HTTPException(409, "用户名已被占用")
    # 与网页版注册保持一致：后台开启「注册必须填写邀请码」时校验并消耗邀请码。
    if _get_invite_config().get("required"):
        inv = _consume_invite(str(payload.get("inviteCode", "")))
        if inv.get("error"):
            raise HTTPException(400, inv["error"])
    uid = _create_user(username, password, nickname, email, _client_ip(request))
    u = query_one("SELECT * FROM users WHERE id = ?", (uid,))
    return _login_result(u)


@router.post("/login")
def login(payload: dict = Body(...)):
    username = str(payload.get("username", "")).strip()
    password = str(payload.get("password", ""))
    u = query_one("SELECT * FROM users WHERE username = ?", (username,))
    if not u or not security.verify_password(password, u["password_hash"]):
        raise HTTPException(401, "用户名或密码错误")
    if not u["is_active"]:
        raise HTTPException(403, "账号已被禁用")
    # 旧哈希登录成功后自动升级为 scrypt
    if security.needs_rehash(u["password_hash"]):
        execute("UPDATE users SET password_hash = ? WHERE id = ?", (security.hash_password(password), u["id"]))
    execute("UPDATE users SET last_login_at = ? WHERE id = ?", (security.now_ms(), u["id"]))
    return _login_result(u)


@router.get("/me")
def me(user: dict = Depends(get_current_user)):
    return {"user": user_public(user)}


@router.post("/local")
def local_login(payload: dict = Body(...)):
    """离线/局域网模式：仅凭昵称登录（无需密码）。

    仅在设置了 EYA_ALLOW_LOCAL_AUTH=1 的本地网关中开放。
    昵称即用户名，保证联网后离线消息能按「离线昵称=在线用户名」正确归属。
    """
    if os.getenv("EYA_ALLOW_LOCAL_AUTH") != "1":
        raise HTTPException(403, "当前不允许离线登录")
    username = str(payload.get("nickname") or payload.get("username") or "").strip()
    if not _USERNAME_RE.match(username):
        raise HTTPException(400, "昵称需 2-20 位（中文/字母/数字/下划线）")
    u = query_one("SELECT * FROM users WHERE username = ?", (username,))
    if not u:
        uid = _create_user(username, security.gen_token(16), username, "", "local")
        u = query_one("SELECT * FROM users WHERE id = ?", (uid,))
    return _login_result(u)