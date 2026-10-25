"""管理后台接口（聊天室 Web 管理页 web/admin.html 使用，前缀 /api/admin）。

仅管理员可访问；返回结构与原 Node 版 server.js 的 /api/admin/* 保持一致
（{ok:true,...} 或 {ok:false,error}），前端无需改动即可对接统一网关。
"""
from __future__ import annotations

import json
import re
from datetime import datetime

from fastapi import APIRouter, Body, Depends, HTTPException

from .. import security
from ..db import execute, execute_update, query_all, query_one
from ..deps import require_admin

router = APIRouter(prefix="/api/admin", tags=["admin"])

_PM_RE = re.compile(r"^pm:(\d+):(\d+)$")


# ---------------------------------------------------------------- 设置存取
def _get_setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str):
    execute("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            (key, value, security.now_ms()))


def _get_json_setting(key: str, default):
    raw = _get_setting(key, "")
    if not raw:
        return default
    try:
        return json.loads(raw)
    except Exception:
        return default


# ---------------------------------------------------------------- 用户管理
@router.get("/users")
def list_users(admin: dict = Depends(require_admin)):
    online_rows = query_all("SELECT user_id, device_id, ip, last_seen FROM online")
    devices: dict[int, set] = {}
    last_seen: dict[int, int] = {}
    for r in online_rows:
        devices.setdefault(r["user_id"], set()).add(r["device_id"])
        last_seen[r["user_id"]] = max(last_seen.get(r["user_id"], 0), r["last_seen"] or 0)

    sess_rows = query_all("SELECT user_id, ip, last_seen FROM sessions")
    login_ips: dict[int, set] = {}
    for r in sess_rows:
        if r["ip"]:
            login_ips.setdefault(r["user_id"], set()).add(r["ip"])
        last_seen[r["user_id"]] = max(last_seen.get(r["user_id"], 0), r["last_seen"] or 0)

    rows = query_all("SELECT * FROM users ORDER BY created_at ASC")
    # 积分余额（没有账户记录的用户按 0 处理）
    points_map = {r["user_id"]: (r["balance"] or 0)
                  for r in query_all("SELECT user_id, balance FROM point_accounts")}
    # 经验（没有记录的用户按 0 处理）；等级由阈值推导
    from .. import levelcore as _LV
    exp_map = {r["user_id"]: int(r["exp"] or 0)
               for r in query_all("SELECT user_id, exp FROM user_exp")}
    thresholds = _LV.load_config()["thresholds"]
    users = []
    for u in rows:
        exp = exp_map.get(u["id"], 0)
        users.append({
            "id": u["id"],
            "username": u["username"],
            "nickname": u["nickname"] or "",
            "online": u["id"] in devices,
            "deviceCount": len(devices.get(u["id"], ())),
            "isAdmin": bool(u["is_admin"]),
            "banned": not bool(u["is_active"]),
            "createdAt": u["created_at"],
            "regIp": u["reg_ip"] or "",
            "loginIps": sorted(login_ips.get(u["id"], ())),
            "lastSeen": max(last_seen.get(u["id"], 0),
                            (u["last_login_at"] * 1000
                             if 0 < (u["last_login_at"] or 0) < 1_000_000_000_000
                             else (u["last_login_at"] or 0))),
            "points": points_map.get(u["id"], 0),
            "exp": exp,
            "level": _LV.level_of(exp, thresholds),
        })
    total = len(users)
    msg_total = query_one("SELECT COUNT(*) AS c FROM messages")["c"]
    return {"ok": True, "users": users, "total": total, "msgTotal": msg_total}


def _target_user(user_id: int):
    u = query_one("SELECT * FROM users WHERE id = ?", (user_id,))
    if not u:
        raise HTTPException(404, "用户不存在")
    return u


def _audit(admin: dict, action: str, detail: str = ""):
    """管理操作留痕（写 logs 表，与 Steam 后台的操作日志同一张表）。"""
    execute("INSERT INTO logs (user_id, username, action, detail, ip_address, created_at) VALUES (?,?,?,?,?,?)",
            (admin["id"], admin["username"], action, detail, "", datetime.now().isoformat()))


@router.post("/ban")
def ban_user(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    user_id = int(payload.get("userId") or 0)
    banned = bool(payload.get("banned"))
    u = _target_user(user_id)
    if u["is_admin"]:
        raise HTTPException(400, "不能封禁管理员账号")
    execute("UPDATE users SET is_active = ? WHERE id = ?", (0 if banned else 1, user_id))
    if banned:
        # 封禁即清除其全部登录会话
        execute("DELETE FROM sessions WHERE user_id = ?", (user_id,))
        execute("DELETE FROM online WHERE user_id = ?", (user_id,))
    return {"ok": True, "banned": banned}


@router.post("/create-user")
def create_user(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    username = str(payload.get("username", "")).strip()
    password = str(payload.get("password", ""))
    email = str(payload.get("email", "")).strip()[:120]
    is_admin = 1 if payload.get("isAdmin") else 0
    if not re.match(r"^[\u4e00-\u9fa5\w-]{2,20}$", username):
        raise HTTPException(400, "用户名需 2-20 位（中文/字母/数字/下划线）")
    if len(password) < 6:
        raise HTTPException(400, "密码至少 6 位")
    if query_one("SELECT id FROM users WHERE username = ?", (username,)):
        raise HTTPException(409, "用户名已被占用")
    execute("INSERT INTO users (username, password_hash, nickname, email, is_admin, is_active, created_at, reg_ip) "
            "VALUES (?,?,?,?,?,1,?,'admin')",
            (username, security.hash_password(password), username, email, is_admin, security.now_ms()))
    return {"ok": True}


@router.post("/reset-password")
def reset_password(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    user_id = int(payload.get("userId") or 0)
    password = str(payload.get("password", ""))
    if len(password) < 6:
        raise HTTPException(400, "密码至少 6 位")
    _target_user(user_id)
    execute("UPDATE users SET password_hash = ? WHERE id = ?", (security.hash_password(password), user_id))
    execute("DELETE FROM sessions WHERE user_id = ?", (user_id,))
    return {"ok": True}


@router.post("/set-nickname")
def set_nickname(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    user_id = int(payload.get("userId") or 0)
    nickname = str(payload.get("nickname", "")).strip()[:20]
    if not nickname:
        raise HTTPException(400, "昵称不能为空")
    _target_user(user_id)
    execute("UPDATE users SET nickname = ? WHERE id = ?", (nickname, user_id))
    return {"ok": True, "nickname": nickname}


@router.post("/set-username")
def set_username(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """改用户名（聊天后台的用户管理即全系统账号管理）。

    注意：Steam 账号的分配是按用户名记录的，改名时一并同步，避免丢分配。
    """
    user_id = int(payload.get("userId") or 0)
    username = str(payload.get("username", "")).strip()
    u = _target_user(user_id)
    if not re.match(r"^[\u4e00-\u9fa5\w-]{2,20}$", username):
        raise HTTPException(400, "用户名需 2-20 位（中文/字母/数字/下划线）")
    old = u["username"]
    if username == old:
        return {"ok": True, "username": username}
    if query_one("SELECT id FROM users WHERE username = ?", (username,)):
        raise HTTPException(409, "用户名已被占用")
    execute("UPDATE users SET username = ? WHERE id = ?", (username, user_id))
    execute_update("UPDATE accounts SET assigned_to = ? WHERE assigned_to = ?", (username, old))
    # PushPlus 推送规则按用户名记录，改名时同步替换，避免规则失效
    cfg = _get_json_setting("push_config", {})
    if isinstance(cfg, dict):
        hit = False
        for key in ("watchUsers", "excludeUsers"):
            arr = cfg.get(key)
            if isinstance(arr, list) and old in arr:
                cfg[key] = [username if x == old else x for x in arr]
                hit = True
        if hit:
            _set_setting("push_config", json.dumps(cfg, ensure_ascii=False))
    _audit(admin, "修改用户名", f"{old} → {username}")
    return {"ok": True, "username": username}


@router.post("/delete-user")
def delete_user(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """删除账号（全系统账号管理）。

    - 会话/在线状态一并清除；
    - 其占用的 Steam 账号释放回“可用”池（已分配的置空、状态改 available）；
    - 聊天历史与云端 Cookie 记录保留：历史消息显示为“已注销用户”，
      云端账号可在云游戏后台重新分配给其他用户或删除。
    """
    user_id = int(payload.get("userId") or 0)
    u = _target_user(user_id)
    if u["is_admin"]:
        raise HTTPException(400, "不能删除管理员账号")
    if user_id == int(admin["id"]):
        raise HTTPException(400, "不能删除当前登录的账号")
    username = u["username"]
    execute("DELETE FROM sessions WHERE user_id = ?", (user_id,))
    execute("DELETE FROM online WHERE user_id = ?", (user_id,))
    released = execute_update(
        "UPDATE accounts SET assigned_to = NULL, status = 'available' WHERE assigned_to = ?", (username,))
    execute("DELETE FROM users WHERE id = ?", (user_id,))
    _audit(admin, "删除用户", f"删除用户: {username}（释放 Steam 账号 {released} 个）")
    return {"ok": True, "releasedAccounts": released}


# ---------------------------------------------------------------- 私聊记录
@router.get("/conversations")
def conversations(admin: dict = Depends(require_admin)):
    rows = query_all(
        "SELECT room, COUNT(*) AS c, MAX(created_at) AS last FROM messages "
        "WHERE room LIKE 'pm:%' GROUP BY room ORDER BY last DESC")
    out = []
    for r in rows:
        m = _PM_RE.match(r["room"])
        if not m:
            continue
        a, b = int(m.group(1)), int(m.group(2))
        ua = query_one("SELECT COALESCE(NULLIF(nickname,''), username) AS n FROM users WHERE id = ?", (a,))
        ub = query_one("SELECT COALESCE(NULLIF(nickname,''), username) AS n FROM users WHERE id = ?", (b,))
        out.append({
            "room": r["room"],
            "users": [ua["n"] if ua else str(a), ub["n"] if ub else str(b)],
            "count": r["c"],
        })
    return {"ok": True, "conversations": out}


@router.get("/room-messages")
def room_messages(room: str = "", admin: dict = Depends(require_admin)):
    if not room:
        raise HTTPException(400, "缺少房间参数")
    rows = query_all(
        "SELECT m.*, COALESCE(NULLIF(w.nickname,''), w.username, '已注销用户') AS from_name "
        "FROM messages m LEFT JOIN users w ON w.id = m.from_id WHERE m.room = ? "
        "ORDER BY m.id ASC LIMIT 300", (room,))
    messages = [{
        "id": r["id"],
        "createdAt": r["created_at"],
        "fromName": r["from_name"],
        "type": r["type"],
        "content": r["content"] or "",
        "filePath": r["file_path"] or "",
        "fileName": r["file_name"] or "",
    } for r in rows]
    return {"ok": True, "messages": messages}


# ---------------------------------------------------------------- PushPlus 推送配置
def _all_usernames() -> list[str]:
    return [r["username"] for r in query_all("SELECT username FROM users ORDER BY id ASC")]


@router.get("/push-config")
def get_push_config(admin: dict = Depends(require_admin)):
    config = _get_json_setting("push_config", {})
    return {"ok": True, "users": _all_usernames(), "config": config}


@router.post("/push-config")
def save_push_config(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    config = {
        "enabled": bool(payload.get("enabled")),
        "token": str(payload.get("token", "")).strip(),
        "topic": str(payload.get("topic", "")).strip(),
        "throttleSeconds": int(payload.get("throttleSeconds") or 0),
        "watchGroup": bool(payload.get("watchGroup")),
        "watchUsers": [str(x) for x in (payload.get("watchUsers") or [])],
        "excludeUsers": [str(x) for x in (payload.get("excludeUsers") or [])],
        "template": str(payload.get("template") or "markdown"),
    }
    _set_setting("push_config", json.dumps(config, ensure_ascii=False))
    return {"ok": True}


@router.post("/push-test")
def push_test(admin: dict = Depends(require_admin)):
    config = _get_json_setting("push_config", {})
    token = (config or {}).get("token")
    if not token:
        return {"ok": False, "error": "请先填写 PushPlus Token 并保存"}
    try:
        import requests
        body = {
            "token": token,
            "title": "SteamEYA 测试推送",
            "content": f"这是一条来自管理后台的测试消息（{security.now_ms()}）。",
            "template": (config or {}).get("template") or "markdown",
        }
        if (config or {}).get("topic"):
            body["topic"] = config["topic"]
        resp = requests.post("https://www.pushplus.plus/send", json=body, timeout=12).json()
        if resp.get("code") == 200:
            return {"ok": True, "msg": "测试推送已发送"}
        return {"ok": False, "error": resp.get("msg") or "推送失败"}
    except Exception as e:
        return {"ok": False, "error": f"推送失败：{e}"}


# ---------------------------------------------------------------- 公开面板预设内容
@router.get("/panel-preset")
def get_panel_preset(admin: dict = Depends(require_admin)):
    return {"ok": True, "items": _get_json_setting("panel_preset", [])}


@router.post("/panel-preset")
def save_panel_preset(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    items = []
    for it in payload.get("items") or []:
        name = str(it.get("name", "")).strip()[:40]
        content = str(it.get("content", "")).strip()[:300]
        if content:
            items.append({"name": name, "content": content})
    _set_setting("panel_preset", json.dumps(items, ensure_ascii=False))
    return {"ok": True, "items": items}


# ---------------------------------------------------------------- 注册邀请码
def _invite_payload() -> dict:
    cfg = _get_json_setting("invite_config", {"required": True})
    required = bool(cfg.get("required", True))
    rows = query_all("SELECT * FROM invite_codes ORDER BY id DESC")
    codes = [{
        "id": r["id"],
        "code": r["code"],
        "maxUses": r["max_uses"],
        "usedCount": r["used_count"],
        "note": r["note"] or "",
        "active": bool(r["active"]),
        "createdBy": query_one("SELECT username FROM users WHERE id = ?", (r["created_by"],))["username"]
        if r["created_by"] else "",
    } for r in rows]
    available = query_one(
        "SELECT COUNT(*) AS c FROM invite_codes WHERE active=1 AND (max_uses=0 OR used_count < max_uses)")["c"]
    return {"ok": True, "required": required, "available": available, "codes": codes}


@router.get("/invite-codes")
def list_invite_codes(admin: dict = Depends(require_admin)):
    return _invite_payload()


@router.post("/invite-config")
def set_invite_config(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    required = bool(payload.get("required"))
    _set_setting("invite_config", json.dumps({"required": required}))
    return _invite_payload()


@router.post("/invite-codes")
def create_invite_code(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    code = str(payload.get("code", "")).strip().upper()
    if not code:
        alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
        code = "".join(security.secrets.choice(alphabet) for _ in range(8))
    if query_one("SELECT id FROM invite_codes WHERE code = ?", (code,)):
        raise HTTPException(409, "邀请码已存在")
    max_uses = max(int(payload.get("maxUses") or 0), 0)
    note = str(payload.get("note", "")).strip()[:100]
    execute("INSERT INTO invite_codes (code, max_uses, used_count, note, active, created_by, created_at) "
            "VALUES (?,?,0,?,1,?,?)",
            (code, max_uses, note, admin["id"], security.now_ms()))
    result = _invite_payload()
    result["created"] = code
    return result


@router.post("/invite-codes/update")
def update_invite_code(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    code_id = int(payload.get("id") or 0)
    if not query_one("SELECT id FROM invite_codes WHERE id = ?", (code_id,)):
        raise HTTPException(404, "邀请码不存在")
    max_uses = max(int(payload.get("maxUses") or 0), 0)
    active = 1 if payload.get("active") else 0
    execute("UPDATE invite_codes SET max_uses = ?, active = ? WHERE id = ?", (max_uses, active, code_id))
    return _invite_payload()


@router.post("/invite-codes/delete")
def delete_invite_code(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    code_id = int(payload.get("id") or 0)
    execute_update("DELETE FROM invite_codes WHERE id = ?", (code_id,))
    return _invite_payload()