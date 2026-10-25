"""等级 / 经验接口。

用户侧（get_current_user）：
  GET  /api/level/me        我的等级 + 经验进度 + 规则（阈值 / 经验怎么来）
  GET  /api/level/logs      经验明细（分页）
  GET  /api/level/board     等级排行 Top N
管理侧（require_admin）：
  GET  /api/admin/level-config     当前等级配置
  POST /api/admin/level-config     保存等级配置（阈值 / 来源 / 上限 / 总开关）
  GET  /api/admin/level-users      用户列表（含经验与等级）
  POST /api/admin/level-exp        手工调整某用户经验（可正可负）
  POST /api/admin/level-init       老用户折算初始化（preview=1 只预览不写库，幂等）
"""
from __future__ import annotations

from fastapi import APIRouter, Body, Depends, HTTPException

from .. import levelcore as LC
from ..db import query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["level"])


# ---------------------------------------------------------------- 用户侧
@router.get("/level/me")
def my_level(user: dict = Depends(get_current_user)):
    cfg = LC.load_config()
    info = LC.level_info(LC.exp_of(user["id"]), cfg["thresholds"])
    return {"ok": True, "enabled": cfg["enabled"], "thresholds": cfg["thresholds"],
            "sources": LC.public_sources(cfg), **info}


@router.get("/level/logs")
def level_logs(limit: int = 30, offset: int = 0, user: dict = Depends(get_current_user)):
    limit = min(max(limit, 1), 100)
    offset = max(offset, 0)
    rows = query_all("SELECT * FROM exp_logs WHERE user_id = ? ORDER BY id DESC LIMIT ? OFFSET ?",
                     (user["id"], limit, offset))
    total = query_one("SELECT COUNT(*) AS c FROM exp_logs WHERE user_id = ?", (user["id"],))["c"]
    return {"ok": True, "total": total, "logs": [{
        "id": r["id"], "delta": r["delta"], "expAfter": r["exp_after"],
        "source": r["source"],
        "title": LC.SOURCE_NAMES.get(r["source"], "经验变动"),
        "ref": r["ref"] or "", "remark": r["remark"] or "",
        "createdAt": r["created_at"],
    } for r in rows]}


@router.get("/level/board")
def level_board(limit: int = 20, user: dict = Depends(get_current_user)):
    limit = min(max(limit, 1), 100)
    cfg = LC.load_config()
    rows = query_all(
        "SELECT u.id, u.username, u.nickname, u.avatar, COALESCE(e.exp, 0) AS exp "
        "FROM users u LEFT JOIN user_exp e ON e.user_id = u.id "
        "WHERE u.is_active = 1 AND u.is_admin = 0 "
        "ORDER BY exp DESC, u.id ASC LIMIT ?", (limit,))
    board = []
    for i, r in enumerate(rows):
        board.append({
            "rank": i + 1, "userId": r["id"], "username": r["username"],
            "nickname": r["nickname"] or r["username"], "avatar": r["avatar"] or "",
            "exp": int(r["exp"] or 0), "level": LC.level_of(r["exp"], cfg["thresholds"]),
            "isMe": r["id"] == user["id"],
        })
    return {"ok": True, "thresholds": cfg["thresholds"], "board": board}


# ---------------------------------------------------------------- 管理侧
@router.get("/admin/level-config")
def admin_get_config(admin: dict = Depends(require_admin)):
    return {"ok": True, "config": LC.load_config(), "sources": LC.public_sources(),
            "initAt": LC.init_at()}


@router.post("/admin/level-config")
def admin_set_config(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    cfg = LC.save_config(payload or {})
    return {"ok": True, "config": cfg, "sources": LC.public_sources(cfg)}


def _exp_or_404(payload: dict) -> int:
    uid = int(payload.get("userId") or 0)
    if not uid:
        name = str(payload.get("username") or "").strip()
        row = query_one("SELECT id FROM users WHERE username = ?", (name,)) if name else None
        if not row:
            raise HTTPException(404, "用户不存在")
        uid = row["id"]
    if not query_one("SELECT id FROM users WHERE id = ?", (uid,)):
        raise HTTPException(404, "用户不存在")
    return uid


@router.get("/admin/level-users")
def admin_level_users(keyword: str = "", limit: int = 200, offset: int = 0,
                      admin: dict = Depends(require_admin)):
    cfg = LC.load_config()
    limit = min(max(limit, 1), 500)
    offset = max(offset, 0)
    kw = (keyword or "").strip()
    where, params = "", []
    if kw:
        where = "WHERE (u.username LIKE ? OR u.nickname LIKE ?)"
        params = [f"%{kw}%", f"%{kw}%"]
    base = ("FROM users u LEFT JOIN user_exp e ON e.user_id = u.id " + where)
    rows = query_all(
        "SELECT u.id, u.username, u.nickname, u.avatar, u.is_admin, u.is_active, u.created_at, "
        "COALESCE(e.exp, 0) AS exp, e.updated_at AS exp_updated " + base +
        " ORDER BY exp DESC, u.id ASC LIMIT ? OFFSET ?", (*params, limit, offset))
    total = query_one("SELECT COUNT(*) AS c " + base, tuple(params))["c"]
    users = [{
        "id": r["id"], "username": r["username"], "nickname": r["nickname"] or "",
        "avatar": r["avatar"] or "", "isAdmin": bool(r["is_admin"]),
        "active": bool(r["is_active"]), "createdAt": r["created_at"],
        "exp": int(r["exp"] or 0), "level": LC.level_of(r["exp"], cfg["thresholds"]),
        "expUpdated": r["exp_updated"] or 0,
    } for r in rows]
    return {"ok": True, "total": total, "thresholds": cfg["thresholds"], "users": users}


@router.post("/admin/level-exp")
def admin_set_exp(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    uid = _exp_or_404(payload)
    try:
        delta = int(payload.get("delta"))
    except Exception:
        raise HTTPException(400, "请填写经验变动数量")
    if delta == 0:
        raise HTTPException(400, "经验变动不能为 0")
    if abs(delta) > 100_000_000:
        raise HTTPException(400, "单次调整幅度过大")
    remark = str(payload.get("remark") or "").strip()[:100]
    info = LC.adjust(uid, delta, "admin", admin.get("username") or "admin", remark or "管理员调整")
    return {"ok": True, "userId": uid, **info}


@router.post("/admin/level-init")
def admin_level_init(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """老用户折算初始化。preview=1 只返回折算明细，不写库；否则落地（幂等）。"""
    if payload.get("preview"):
        rows = LC.init_preview(only_uninitialized=True)
        return {"ok": True, "count": len(rows), "users": rows, "initAt": LC.init_at()}
    res = LC.init_apply()
    return {"ok": True, **res, "initAt": LC.init_at()}