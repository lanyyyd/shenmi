"""聊天室模块：面向现有 Web 前端（WebView2 内嵌）的接口。

保持与原 server.js 一致的返回结构（{ok:true,...} 或 {ok:false,error}）与 cookie 会话，
使前端 app.js / index.html 无需改动即可对接统一网关。
"""
from __future__ import annotations

import base64
import json
import os
import pathlib
import quopri
import re
import uuid
from typing import Optional

from fastapi import APIRouter, Body, Depends, File, Form, Header, Request, Response, UploadFile

from .. import chat_logic as CL
from .. import security
from ..config import (AVATAR_DIR, MAX_AVATAR, MAX_STICKER, MAX_UPLOAD, STICKER_DIR, UPLOAD_DIR)
from ..db import execute, query_all, query_one
from ..deps import resolve_user, user_public
from ..hub import hub

router = APIRouter(prefix="/api", tags=["chat"])

_USERNAME_RE = re.compile(r"^[\u4e00-\u9fa5\w-]{2,20}$")
_IMG_EXT = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".avif"}
_VID_EXT = {".mp4", ".webm", ".ogv", ".ogg", ".m4v", ".mov"}
_STICKER_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}


def _now() -> int:
    return security.now_ms()


# MIME encoded-word（RFC2047）：=?charset?B/Q?text?=
_MIME_WORD_RE = re.compile(r"=\?([^?]+)\?([BbQq])\?([^?]*)\?=")


def _decode_upload_name(name: str) -> str:
    """还原被 MIME(RFC2047) 或 latin-1 误编码的上传文件名。

    .NET HttpClient 的 MultipartFormDataContent 上传非 ASCII（中文）文件名时，会把它编码成
    RFC2047 encoded-word（形如 =?utf-8?B?...?=）。服务端若原样使用，中文名会变成一长串乱码，
    且 Path.suffix 取不到扩展名，导致图片/视频被误判为普通文件（无法预览、下载名也不对）。
    """
    if not name:
        return name

    def _word(m: "re.Match[str]") -> str:
        charset, enc, text = m.group(1), m.group(2).upper(), m.group(3)
        try:
            if enc == "B":
                raw = base64.b64decode(text + "=" * (-len(text) % 4))
            else:
                raw = quopri.decodestring(text.replace("_", " ").encode("ascii"))
            return raw.decode(charset, "replace")
        except Exception:
            return m.group(0)

    name = _MIME_WORD_RE.sub(_word, name)
    try:
        return name.encode("latin-1").decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return name


def _sniff_media(data: bytes) -> tuple[str, str]:
    """按文件头判断媒体类型，返回 (kind, ext)；识别不出返回 ('', '')。"""
    if data.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image", ".png"
    if data.startswith(b"\xff\xd8\xff"):
        return "image", ".jpg"
    if data.startswith((b"GIF87a", b"GIF89a")):
        return "image", ".gif"
    if data.startswith(b"BM"):
        return "image", ".bmp"
    if data.startswith(b"RIFF") and data[8:12] == b"WEBP":
        return "image", ".webp"
    if data[4:8] == b"ftyp":
        return "video", ".mp4"
    if data.startswith(b"\x1a\x45\xdf\xa3"):
        return "video", ".webm"
    return "", ""


def _err(msg: str, code: int = 400):
    from fastapi.responses import JSONResponse
    return JSONResponse({"ok": False, "error": msg}, status_code=code)


def _client_ip(request: Request) -> str:
    xf = (request.headers.get("x-forwarded-for") or "").split(",")[0].strip()
    return xf or (request.client.host if request.client else "")


def _issue_session(response: Response, user_id: int, device_id: str, request: Request) -> str:
    token = security.gen_token(32)
    did = (device_id or "")[:64] or str(uuid.uuid4())
    t = _now()
    execute("INSERT INTO sessions (token, user_id, device_id, ua, ip, created_at, last_seen) VALUES (?,?,?,?,?,?,?)",
            (token, user_id, did, (request.headers.get("user-agent") or "")[:200], _client_ip(request), t, t))
    response.set_cookie("token", token, max_age=30 * 86400, httponly=True, samesite="lax", path="/")
    return token


def _me(user: Optional[dict], request: Request, authorization: Optional[str] = None):
    if user:
        return user
    return resolve_user(request, authorization)


# ---------------- 邀请码配置 ----------------
def _get_invite_config() -> dict:
    row = query_one("SELECT value FROM settings WHERE key = 'invite_config'")
    try:
        return {"required": True, **json.loads(row["value"])} if row else {"required": True}
    except Exception:
        return {"required": True}


def _invite_available() -> int:
    return query_one(
        "SELECT COUNT(*) AS c FROM invite_codes WHERE active=1 AND (max_uses=0 OR used_count < max_uses)")["c"]


def _consume_invite(raw: str) -> dict:
    code = str(raw or "").strip().upper()
    if not code:
        return {"error": "请填写邀请码"}
    row = query_one("SELECT * FROM invite_codes WHERE code = ?", (code,))
    if not row:
        return {"error": "邀请码无效"}
    if not row["active"]:
        return {"error": "该邀请码已被停用"}
    if row["max_uses"] > 0 and row["used_count"] >= row["max_uses"]:
        return {"error": "该邀请码使用次数已达上限"}
    cur = execute("UPDATE invite_codes SET used_count = used_count + 1 "
                  "WHERE id=? AND active=1 AND (max_uses=0 OR used_count < max_uses)", (row["id"],))
    if not cur:
        return {"error": "该邀请码已失效"}
    return {"ok": True, "id": row["id"]}


@router.get("/invite-info")
def invite_info():
    return {"ok": True, "required": bool(_get_invite_config().get("required")), "available": _invite_available()}


# ---------------- 注册 / 登录 ----------------
@router.post("/register")
def register(request: Request, response: Response, payload: dict = Body(...)):
    username = str(payload.get("username", "")).strip()
    password = str(payload.get("password", ""))
    nickname = str(payload.get("nickname", "")).strip()[:20] or username
    email = str(payload.get("email", "")).strip()[:120]
    device_id = str(payload.get("deviceId", ""))
    invite = str(payload.get("inviteCode", ""))
    if not _USERNAME_RE.match(username):
        return _err("用户名需 2-20 位（中文/字母/数字/下划线）")
    if len(password) < 6:
        return _err("密码至少 6 位")
    if query_one("SELECT id FROM users WHERE username = ?", (username,)):
        return _err("用户名已被占用", 409)
    inv = None
    if _get_invite_config().get("required"):
        inv = _consume_invite(invite)
        if inv.get("error"):
            return _err(inv["error"])
    uid = execute(
        "INSERT INTO users (username, password_hash, nickname, email, created_at, reg_ip) VALUES (?,?,?,?,?,?)",
        (username, security.hash_password(password), nickname, email, _now(), _client_ip(request)))
    _issue_session(response, uid, device_id, request)
    u = query_one("SELECT * FROM users WHERE id = ?", (uid,))
    return {"ok": True, "user": user_public(u)}


@router.post("/login")
def login(request: Request, response: Response, payload: dict = Body(...)):
    username = str(payload.get("username", "")).strip()
    password = str(payload.get("password", ""))
    u = query_one("SELECT * FROM users WHERE username = ?", (username,))
    if not u or not security.verify_password(password, u["password_hash"]):
        return _err("用户名或密码错误", 401)
    if not u["is_active"]:
        return _err("账号已被封禁", 403)
    if security.needs_rehash(u["password_hash"]):
        execute("UPDATE users SET password_hash = ? WHERE id = ?", (security.hash_password(password), u["id"]))
    execute("UPDATE users SET last_login_at = ? WHERE id = ?", (_now(), u["id"]))
    _issue_session(response, u["id"], str(payload.get("deviceId", "")), request)
    return {"ok": True, "user": user_public(u)}


@router.post("/logout")
def logout(request: Request, response: Response):
    token = request.cookies.get("token")
    if token:
        execute("DELETE FROM sessions WHERE token = ?", (token,))
    response.delete_cookie("token", path="/")
    return {"ok": True}


@router.get("/me")
def me(request: Request, authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    return {"ok": True, "user": user_public(u) if u else None}


@router.get("/autologin")
def autologin(request: Request, jwt: str = "", redirect: str = "/"):
    """用统一 JWT 换取聊天会话 cookie 并跳转前端。

    供客户端把 WebView2 直接指向 `.../api/autologin?jwt=<token>`，
    从而复用现有 Web 聊天界面而无需在浏览器侧再登录一次。
    """
    from fastapi.responses import RedirectResponse

    payload = security.decode_access_token(jwt) if jwt else None
    if not payload:
        return _err("无效的令牌", 401)
    u = query_one("SELECT * FROM users WHERE id = ?", (int(payload["sub"]),))
    if not u or not u["is_active"]:
        return _err("用户不存在或已禁用", 401)
    response = RedirectResponse(url=redirect or "/")
    _issue_session(response, u["id"], "webview", request)
    return response


# ---------------- 个人中心 ----------------
@router.post("/profile")
def profile(request: Request, payload: dict = Body(...), authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    if payload.get("nickname") is not None:
        nick = str(payload["nickname"]).strip()[:20]
        if not nick:
            return _err("昵称不能为空")
        execute("UPDATE users SET nickname = ? WHERE id = ?", (nick, u["id"]))
        import asyncio
        try:
            asyncio.create_task(hub.broadcast_online())
        except Exception:
            pass
        return {"ok": True, "nickname": nick}
    if payload.get("oldPassword") is not None and payload.get("newPassword") is not None:
        if not security.verify_password(str(payload["oldPassword"]), u["password_hash"]):
            return _err("原密码错误")
        if len(str(payload["newPassword"])) < 6:
            return _err("新密码至少 6 位")
        execute("UPDATE users SET password_hash = ? WHERE id = ?", (security.hash_password(str(payload["newPassword"])), u["id"]))
        execute("DELETE FROM sessions WHERE user_id = ?", (u["id"],))
        return {"ok": True, "msg": "密码已修改，请重新登录"}
    return _err("缺少参数")


@router.post("/profile/avatar")
async def profile_avatar(request: Request, file: UploadFile = File(...),
                         authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    ext = pathlib.Path(file.filename or "").suffix.lower()
    if not (file.content_type or "").startswith("image/") or ext not in _IMG_EXT:
        return _err("头像仅支持 png/jpg/gif/webp/bmp，且不超过 2MB")
    data = await file.read()
    if len(data) > MAX_AVATAR:
        return _err("头像需 2MB 以内")
    name = f"{_now()}-{security.gen_token(6)}{ext}"
    (AVATAR_DIR / name).write_bytes(data)
    url = f"/uploads/avatars/{name}"
    prev = u.get("avatar") or ""
    execute("UPDATE users SET avatar = ? WHERE id = ?", (url, u["id"]))
    if prev.startswith("/uploads/avatars/"):
        try:
            (UPLOAD_DIR / prev[len("/uploads/"):]).unlink()
        except Exception:
            pass
    return {"ok": True, "avatar": url}


# ---------------- 在线 / 用户 / 历史 ----------------
@router.get("/online")
def online():
    return {"ok": True, **hub.online_snapshot()}


@router.get("/users")
def users(request: Request, authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    online_ids = {r["user_id"] for r in query_all("SELECT user_id FROM online")}
    # lastSeen = 最后登录 / 最后在线 / 最后发言 三者取最大（毫秒），供前端“上次登录时间”展示。
    # 之前未下发该字段，前端 fmtAgo 收到空值便恒显示“从未登录过”。
    rows = query_all(
        "SELECT u.id, u.username, u.nickname, u.avatar, u.last_login_at, "
        "(SELECT MAX(o.last_seen) FROM online o WHERE o.user_id = u.id) AS online_seen, "
        "(SELECT MAX(m.created_at) FROM messages m WHERE m.from_id = u.id) AS msg_seen "
        "FROM users u WHERE u.is_active=1 AND u.is_admin=0 ORDER BY u.id DESC")
    out = []
    for r in rows:
        last_seen = max(r["last_login_at"] or 0, r["online_seen"] or 0, r["msg_seen"] or 0)
        out.append({"id": r["id"], "username": r["username"], "nickname": r["nickname"],
                    "avatar": r["avatar"] or "", "displayName": CL.display_name(r),
                    "online": r["id"] in online_ids, "lastSeen": last_seen})
    return {"ok": True, "users": out}


@router.get("/history")
def history(request: Request, room: str = "group", limit: int = 80, before: int = 0,
            authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    limit = min(limit, 200)
    # LEFT JOIN：用户被后台删除后，其历史消息仍可正常显示（显示为“已注销用户”）
    base = ("SELECT m.*, COALESCE(NULLIF(w.nickname,''), w.username, '已注销用户') AS from_name, "
            "COALESCE(w.avatar,'') AS from_avatar "
            "FROM messages m LEFT JOIN users w ON w.id = m.from_id ")
    if room == "group":
        if before:
            rows = query_all(base + "WHERE m.room='group' AND m.id < ? ORDER BY m.id DESC LIMIT ?", (before, limit))
        else:
            rows = query_all(base + "WHERE m.room='group' ORDER BY m.id DESC LIMIT ?", (limit,))
    else:
        m = re.match(r"^pm:(\d+):(\d+)$", room)
        if not m:
            return _err("房间参数错误")
        a, b = int(m.group(1)), int(m.group(2))
        if u["id"] not in (a, b):
            return _err("无权查看该会话", 403)
        if before:
            rows = query_all(base + "WHERE m.room=? AND m.id < ? ORDER BY m.id DESC LIMIT ?", (room, before, limit))
        else:
            rows = query_all(base + "WHERE m.room=? ORDER BY m.id DESC LIMIT ?", (room, limit))
    lst = [CL.shape_msg(r, u) for r in reversed(rows)]
    return {"ok": True, "messages": lst, "hasMore": len(rows) == limit}


# ---------------- 已读 / 未读 ----------------
@router.get("/unread")
def unread(request: Request, authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    return {"ok": True, "unread": CL.unread_for(u["id"]), "mentionGroup": CL.mention_for(u["id"])}


@router.post("/read")
async def read(request: Request, payload: dict = Body(...), authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    room = str(payload.get("room") or "")
    if not room:
        return _err("缺少房间")
    max_row = query_one("SELECT COALESCE(MAX(id),0) AS m FROM messages WHERE room = ?", (room,))
    max_id = max_row["m"]
    execute("INSERT INTO read_state (user_id, room, last_read_id, updated_at) VALUES (?,?,?,?) "
            "ON CONFLICT(user_id, room) DO UPDATE SET last_read_id=excluded.last_read_id, updated_at=excluded.updated_at",
            (u["id"], room, max_id, _now()))
    await hub.broadcast_read(u, room, max_id)
    return {"ok": True, "lastReadId": max_id, "unread": CL.unread_for(u["id"]), "mentionGroup": CL.mention_for(u["id"])}


@router.get("/read-state")
def read_state(request: Request, room: str = "", authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    rows = query_all(
        "SELECT r.user_id, r.last_read_id, r.updated_at, COALESCE(NULLIF(w.nickname,''), w.username) AS username "
        "FROM read_state r JOIN users w ON w.id = r.user_id WHERE r.room = ?", (room,))
    return {"ok": True, "states": [{"userId": r["user_id"], "username": r["username"],
                                    "lastReadId": r["last_read_id"], "updatedAt": r["updated_at"]} for r in rows]}


# ---------------- 搜索 ----------------
@router.get("/search")
def search(request: Request, q: str = "", type: str = "", sender: str = "", room: str = "",
           frm: int = 0, to: int = 0, limit: int = 50, offset: int = 0,
           authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    limit = min(limit, 200)
    where, params = [], []
    if room == "group":
        where.append("m.room='group'")
    elif re.match(r"^pm:\d+:\d+$", room):
        where.append("m.room=?")
        params.append(room)
    else:
        where.append("(m.room='group' OR m.from_id=? OR m.to_id=?)")
        params += [u["id"], u["id"]]
    if type in ("text", "file"):
        where.append("m.type=?")
        params.append(type)
    elif type in ("image", "video", "media"):
        where.append("m.type IN ('image','video')")
    if q:
        if type == "file":
            where.append("m.file_name LIKE ?")
            params.append(f"%{q}%")
        elif type in ("image", "video", "media"):
            pass
        elif type == "text":
            where.append("m.content LIKE ?")
            params.append(f"%{q}%")
        else:
            where.append("(m.content LIKE ? OR m.file_name LIKE ?)")
            params += [f"%{q}%", f"%{q}%"]
    if sender:
        where.append("(w.nickname LIKE ? OR w.username LIKE ?)")
        params += [f"%{sender}%", f"%{sender}%"]
    if frm:
        where.append("m.created_at >= ?")
        params.append(frm)
    if to:
        where.append("m.created_at <= ?")
        params.append(to)
    sql = ("SELECT m.*, COALESCE(NULLIF(w.nickname,''), w.username, '已注销用户') AS from_name, "
           "COALESCE(w.avatar,'') AS from_avatar "
           "FROM messages m LEFT JOIN users w ON w.id = m.from_id WHERE " + " AND ".join(where) +
           " ORDER BY m.id DESC LIMIT ? OFFSET ?")
    rows = query_all(sql, (*params, limit, offset))
    return {"ok": True, "messages": [CL.shape_msg(r, u) for r in rows], "count": len(rows),
            "hasMore": len(rows) == limit, "nextOffset": offset + len(rows)}


# ---------------- 文件上传 ----------------
@router.post("/upload")
async def upload_file(request: Request, file: UploadFile = File(...),
                      authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    data = await file.read()
    if len(data) > MAX_UPLOAD:
        return _err("文件超过 50MB 限制", 413)
    raw_name = _decode_upload_name(file.filename or "")
    ext = pathlib.Path(raw_name).suffix.lower()
    mt = (file.content_type or "").lower()
    is_img = mt.startswith("image/") or ext in _IMG_EXT
    is_vid = (not is_img) and (mt.startswith("video/") or ext in _VID_EXT)
    if not is_img and not is_vid:
        # 文件名没有可用扩展名（被 MIME 编码截断、或本就无扩展名）时按文件头兜底，
        # 否则图片/视频会被误判成普通文件，既不能预览、下载名也不对。
        kind, sniff_ext = _sniff_media(data)
        if kind == "image":
            is_img, ext = True, (ext or sniff_ext)
        elif kind == "video":
            is_vid, ext = True, (ext or sniff_ext)
    sub = "images" if is_img else ("videos" if is_vid else "files")
    name = f"{_now()}-{security.gen_token(6)}{ext[:12]}"
    (UPLOAD_DIR / sub / name).write_bytes(data)
    return {"ok": True, "url": f"/uploads/{sub}/{name}", "name": raw_name or name,
            "size": len(data), "isImage": is_img, "isVideo": is_vid}


# ---------------- 表情包 ----------------
def _sticker_out(s) -> dict:
    return {"id": s["id"], "url": s["file_path"], "name": s["file_name"], "size": s["file_size"],
            "sendCount": s["send_count"], "uploaderId": s["uploader_id"], "createdAt": s["created_at"]}


@router.get("/stickers")
def list_stickers(request: Request, sort: str = "hot", limit: int = 120, offset: int = 0,
                  authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    order = "id DESC" if sort == "time" else "send_count DESC, id DESC"
    rows = query_all(f"SELECT * FROM stickers WHERE active=1 ORDER BY {order} LIMIT ? OFFSET ?",
                     (min(limit, 300), max(offset, 0)))
    total = query_one("SELECT COUNT(*) AS c FROM stickers WHERE active=1")["c"]
    return {"ok": True, "sort": sort, "stickers": [_sticker_out(r) for r in rows],
            "total": total, "isAdmin": bool(u.get("is_admin"))}


@router.post("/stickers")
async def upload_sticker(request: Request, file: UploadFile = File(...),
                         authorization: Optional[str] = Header(default=None)):
    import hashlib
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    ext = pathlib.Path(file.filename or "").suffix.lower()
    if not (file.content_type or "").startswith("image/") or ext not in _STICKER_EXT:
        return _err("仅支持 png/jpg/gif/webp/bmp，且不超过 2MB")
    data = await file.read()
    if len(data) > MAX_STICKER:
        return _err("表情包需 2MB 以内", 413)
    h = hashlib.sha256(data).hexdigest()
    exist = query_one("SELECT * FROM stickers WHERE hash = ?", (h,))
    if exist:
        if not exist["active"]:
            execute("UPDATE stickers SET active=1 WHERE id=?", (exist["id"],))
        return {"ok": True, "sticker": _sticker_out(query_one("SELECT * FROM stickers WHERE id=?", (exist["id"],))), "dedup": True}
    name = f"{_now()}-{h[:12]}{ext}"
    (STICKER_DIR / name).write_bytes(data)
    sid = execute("INSERT INTO stickers (file_path, file_name, file_size, hash, uploader_id, send_count, active, created_at) "
                  "VALUES (?,?,?,?,?,0,1,?)",
                  (f"/uploads/stickers/{name}", (file.filename or name)[:200], len(data), h, u["id"], _now()))
    return {"ok": True, "sticker": _sticker_out(query_one("SELECT * FROM stickers WHERE id=?", (sid,)))}


@router.delete("/stickers/{sticker_id}")
def delete_sticker(sticker_id: int, request: Request, authorization: Optional[str] = Header(default=None)):
    u = _me(None, request, authorization)
    if not u:
        return _err("请先登录", 401)
    if not u.get("is_admin"):
        return _err("需要管理员权限", 403)
    s = query_one("SELECT * FROM stickers WHERE id = ?", (sticker_id,))
    if not s:
        return _err("表情不存在", 404)
    execute("UPDATE stickers SET active=0 WHERE id=?", (sticker_id,))
    return {"ok": True}