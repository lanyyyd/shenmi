"""Steam 账号分发模块：用户取号 + 管理员账号/用户/日志/统计/设置。

由原 Steam 服务端（6967）的接口移植，统一到 /api/steam/* 前缀，鉴权改为统一账号。
"""
from __future__ import annotations

from datetime import datetime

from fastapi import APIRouter, Body, Depends, HTTPException, Request

from .. import security
from ..db import execute, query_all, query_one
from ..deps import get_current_user, require_admin, user_public

router = APIRouter(prefix="/api/steam", tags=["steam"])


def _log(user_id, username, action, detail="", ip=""):
    execute("INSERT INTO logs (user_id, username, action, detail, ip_address, created_at) VALUES (?,?,?,?,?,?)",
            (user_id, username, action, detail, ip, datetime.now().isoformat()))


def _get_setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str):
    execute("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            (key, value, security.now_ms()))


# ---------------- 用户：取号 ----------------
@router.get("/account/get")
def get_account(user: dict = Depends(get_current_user)):
    username = user["username"]
    account = query_one(
        "SELECT * FROM accounts WHERE assigned_to = ? AND status != 'expired' ORDER BY id LIMIT 1", (username,))
    already_locked = account is not None
    if not account:
        # 仅“可用”账号可被领取：使用中（in_use）与已过期（expired）的账号不会再被其他用户取到
        account = query_one(
            "SELECT * FROM accounts WHERE (assigned_to IS NULL OR assigned_to = '') "
            "AND status = 'available' ORDER BY id LIMIT 1")
    if not account:
        _log(user["id"], username, "获取账号", "无空闲账号")
        raise HTTPException(404, "没有空闲账号")
    now = datetime.now().isoformat()
    if already_locked:
        execute("UPDATE accounts SET updated_at = ? WHERE id = ?", (now, account["id"]))
    else:
        execute("UPDATE accounts SET assigned_to = ?, status = 'in_use', updated_at = ? WHERE id = ?",
                (username, now, account["id"]))
    _log(user["id"], username, "获取账号",
         f"获取账号: {account['account_name']}" + ("" if already_locked else "（首次获取，已锁定）"))
    return {"id": account["id"], "account_name": account["account_name"],
            "eya_token": account["eya_token"], "steam_id": account["steam_id"]}


# ---------------- 管理员 ----------------
@router.post("/admin/accounts/import")
def import_accounts(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    accounts = payload.get("accounts") or []
    imported = 0
    for acc in accounts:
        name = str(acc.get("account_name", "")).strip()
        token = str(acc.get("eya_token", "")).strip()
        sid = str(acc.get("steam_id", "")).strip()
        assigned = str(acc.get("assigned_user", "")).strip()
        if not name or not token:
            continue
        if query_one("SELECT id FROM accounts WHERE account_name = ?", (name,)):
            continue
        execute("INSERT INTO accounts (account_name, eya_token, steam_id, status, assigned_to) VALUES (?,?,?,?,?)",
                (name, token, sid, "in_use" if assigned else "available", assigned or None))
        imported += 1
    _log(admin["id"], admin["username"], "导入账号", f"批量导入 {imported} 条账号")
    return {"imported": imported, "message": f"成功导入 {imported} 条账号"}


@router.post("/admin/accounts")
def create_account(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """新增单个账号（后台「添加账号」）。

    - 账号名必填且不可重复（重复返回 409，便于前端直接提示）；
    - EYA 令牌必填；
    - 指定了分配用户时校验用户存在，并按「使用中」入库；未指定则入库为「可用」，任何用户可取。
    """
    name = str(payload.get("account_name", "")).strip()
    token = str(payload.get("eya_token", "")).strip()
    sid = str(payload.get("steam_id", "")).strip()
    assigned = str(payload.get("assigned_user", "")).strip()
    if not name:
        raise HTTPException(400, "账号名不能为空")
    if not token:
        raise HTTPException(400, "EYA 令牌不能为空")
    if query_one("SELECT id FROM accounts WHERE account_name = ?", (name,)):
        raise HTTPException(409, "账号名已存在")
    if assigned and not query_one("SELECT id FROM users WHERE username = ?", (assigned,)):
        raise HTTPException(400, "指定的用户不存在")
    aid = execute(
        "INSERT INTO accounts (account_name, eya_token, steam_id, status, assigned_to) VALUES (?,?,?,?,?)",
        (name, token, sid, "in_use" if assigned else "available", assigned or None))
    _log(admin["id"], admin["username"], "添加账号", f"添加账号: {name}" + (f"（指定给 {assigned}）" if assigned else ""))
    return dict(query_one("SELECT * FROM accounts WHERE id = ?", (aid,)))


@router.get("/admin/accounts")
def list_accounts(admin: dict = Depends(require_admin)):
    return [dict(r) for r in query_all("SELECT * FROM accounts ORDER BY created_at DESC")]


@router.put("/admin/accounts/{account_id}")
def update_account(account_id: int, data: dict = Body(...), admin: dict = Depends(require_admin)):
    account = query_one("SELECT * FROM accounts WHERE id = ?", (account_id,))
    if not account:
        raise HTTPException(404, "账号不存在")
    fields: dict = {}
    if data.get("account_name") is not None:
        name = str(data["account_name"]).strip()
        if not name:
            raise HTTPException(400, "账号名不能为空")
        if query_one("SELECT id FROM accounts WHERE account_name = ? AND id != ?", (name, account_id)):
            raise HTTPException(400, "账号名已存在")
        fields["account_name"] = name
    if data.get("eya_token") is not None:
        token = str(data["eya_token"]).strip()
        if not token:
            raise HTTPException(400, "EYA Token 不能为空")
        fields["eya_token"] = token
    if data.get("steam_id") is not None:
        fields["steam_id"] = str(data["steam_id"]).strip()
    if data.get("assigned_to") is not None:
        assigned = str(data["assigned_to"]).strip()
        if assigned:
            if not query_one("SELECT id FROM users WHERE username = ?", (assigned,)):
                raise HTTPException(400, "指定的用户不存在")
            fields["assigned_to"] = assigned
        else:
            fields["assigned_to"] = None
        if data.get("status") is None:
            fields["status"] = "in_use" if assigned else "available"
    if data.get("status") is not None:
        st = str(data["status"]).strip()
        if st not in ("available", "in_use", "expired"):
            raise HTTPException(400, "账号状态不合法")
        fields["status"] = st
    if not fields:
        raise HTTPException(400, "没有需要更新的字段")
    fields["updated_at"] = datetime.now().isoformat()
    clause = ", ".join(f"{k} = ?" for k in fields)
    execute(f"UPDATE accounts SET {clause} WHERE id = ?", (*fields.values(), account_id))
    updated = query_one("SELECT * FROM accounts WHERE id = ?", (account_id,))
    _log(admin["id"], admin["username"], "编辑账号", f"编辑账号: {updated['account_name']}")
    return dict(updated)


@router.delete("/admin/accounts/{account_id}")
def delete_account(account_id: int, admin: dict = Depends(require_admin)):
    account = query_one("SELECT * FROM accounts WHERE id = ?", (account_id,))
    if not account:
        raise HTTPException(404, "账号不存在")
    execute("DELETE FROM accounts WHERE id = ?", (account_id,))
    _log(admin["id"], admin["username"], "删除账号", f"删除账号: {account['account_name']}")
    return {"message": "删除成功"}


@router.get("/admin/users")
def list_users(admin: dict = Depends(require_admin)):
    rows = query_all(
        "SELECT u.id, u.username, u.nickname, u.created_at, u.is_active, "
        "COUNT(l.id) AS login_count FROM users u "
        "LEFT JOIN logs l ON u.id = l.user_id AND l.action = '登录' GROUP BY u.id ORDER BY u.created_at DESC")
    return [dict(r) for r in rows]


@router.put("/admin/users/{user_id}/toggle")
def toggle_user(user_id: int, admin: dict = Depends(require_admin)):
    u = query_one("SELECT * FROM users WHERE id = ?", (user_id,))
    if not u:
        raise HTTPException(404, "用户不存在")
    if u["username"] == admin["username"]:
        raise HTTPException(400, "不能禁用管理员自己")
    new_state = 0 if u["is_active"] else 1
    execute("UPDATE users SET is_active = ? WHERE id = ?", (new_state, user_id))
    _log(admin["id"], admin["username"], "启用用户" if new_state else "禁用用户",
         f"{'启用' if new_state else '禁用'}用户: {u['username']}")
    return {"is_active": bool(new_state)}


@router.get("/admin/logs")
def list_logs(admin: dict = Depends(require_admin)):
    return [dict(r) for r in query_all("SELECT * FROM logs ORDER BY created_at DESC LIMIT 100")]


@router.get("/admin/stats")
def get_stats(admin: dict = Depends(require_admin)):
    total = query_one("SELECT COUNT(*) AS c FROM accounts")["c"]
    available = query_one("SELECT COUNT(*) AS c FROM accounts WHERE status='available'")["c"]
    in_use = query_one("SELECT COUNT(*) AS c FROM accounts WHERE status='in_use'")["c"]
    users = query_one("SELECT COUNT(*) AS c FROM users")["c"]
    return {"total_accounts": total, "available_accounts": available,
            "in_use_accounts": in_use, "total_users": users}


@router.get("/admin/settings")
def read_settings(admin: dict = Depends(require_admin)):
    api_key = _get_setting("steam_api_key")
    masked = ""
    if api_key:
        masked = (api_key[:4] + "****" + api_key[-4:]) if len(api_key) > 8 else "****"
    return {"steam_api_key_set": bool(api_key), "steam_api_key_masked": masked}


@router.put("/admin/settings")
def update_settings(data: dict = Body(...), admin: dict = Depends(require_admin)):
    if data.get("steam_api_key") is None:
        raise HTTPException(400, "没有需要更新的设置")
    _set_setting("steam_api_key", str(data["steam_api_key"]).strip())
    _log(admin["id"], admin["username"], "修改设置", "更新 Steam Web API Key")
    return {"message": "保存成功"}