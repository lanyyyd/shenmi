"""积分系统：每日签到随机积分 + 积分商城（兑换）+ 用户间转账。

随机积分区间、商城商品均由后台配置。

用户侧（get_current_user）：
  POST   /api/points/signin     当天首次签到，发放随机积分（幂等）
  GET    /api/points/me         余额 + 今天是否已签到
  GET    /api/points/logs       积分明细（分页）
  GET    /api/points/items      商城商品（含当前余额）
  POST   /api/points/redeem     兑换商品
  POST   /api/points/transfer   给其他用户转账
管理侧（require_admin）：
  GET    /api/admin/signin-config      签到随机积分区间
  POST   /api/admin/signin-config
  GET    /api/admin/point-items        商品列表（含下架）
  POST   /api/admin/point-items        新增（multipart，可传图片）
  POST   /api/admin/point-items/update 修改
  DELETE /api/admin/point-items/{id}   下架
"""
from __future__ import annotations

import json
import pathlib
import random
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, UploadFile

from .. import security
from ..config import POINT_ITEM_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["points"])

_ITEM_IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
_MAX_ITEM_IMG = 2 * 1024 * 1024
_DEFAULT_SIGNIN = {"min": 1, "max": 10}


# ---------------------------------------------------------------- 小工具
def _get_setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str) -> None:
    execute("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            (key, value, security.now_ms()))


def _signin_config() -> dict:
    raw = _get_setting("signin_config", "")
    if not raw:
        return dict(_DEFAULT_SIGNIN)
    try:
        cfg = json.loads(raw)
        lo, hi = int(cfg.get("min", 1)), int(cfg.get("max", 10))
        if lo < 0 or hi < lo:
            return dict(_DEFAULT_SIGNIN)
        return {"min": lo, "max": hi}
    except Exception:
        return dict(_DEFAULT_SIGNIN)


def _bj_day() -> str:
    """北京时间（UTC+8）的日期，用于「每天首次签到」判定。"""
    return datetime.now(timezone(timedelta(hours=8))).strftime("%Y-%m-%d")


def _balance(user_id: int) -> int:
    row = query_one("SELECT balance FROM point_accounts WHERE user_id = ?", (user_id,))
    return int(row["balance"]) if row else 0


def _add_points(user_id: int, delta: int, type_: str, ref: str = "", remark: str = "") -> int:
    """给账户加/减积分并记一条明细，返回变动后余额。"""
    now = security.now_ms()
    if query_one("SELECT user_id FROM point_accounts WHERE user_id = ?", (user_id,)):
        execute_update("UPDATE point_accounts SET balance = balance + ?, updated_at = ? WHERE user_id = ?",
                       (delta, now, user_id))
    else:
        execute("INSERT INTO point_accounts (user_id, balance, updated_at) VALUES (?, ?, ?)",
                (user_id, delta, now))
    balance = _balance(user_id)
    execute("INSERT INTO point_logs (user_id, delta, balance_after, type, ref, remark, created_at) "
            "VALUES (?,?,?,?,?,?,?)",
            (user_id, delta, balance, type_, ref or "", remark or "", now))
    return balance


def _item_out(r, with_admin: bool = False) -> dict:
    data = {
        "id": r["id"],
        "name": r["name"] or "",
        "description": r["description"] or "",
        "image": r["image_path"] or "",
        "price": r["price"] or 0,
        "stock": r["stock"] if r["stock"] is not None else -1,
        "createdAt": r["created_at"],
    }
    if with_admin:
        data["active"] = bool(r["active"])
    return data


async def _save_item_image(file: UploadFile | None) -> str:
    """保存商品图片，返回 /uploads/points/xxx 相对路径；未传/不合法返回空串。"""
    if file is None or not file.filename:
        return ""
    ext = pathlib.Path(file.filename).suffix.lower()
    if ext not in _ITEM_IMG_EXT:
        raise HTTPException(400, "商品图片仅支持 png/jpg/gif/webp/bmp")
    data = await file.read()
    if len(data) > _MAX_ITEM_IMG:
        raise HTTPException(413, "商品图片需 2MB 以内")
    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    (POINT_ITEM_DIR / name).write_bytes(data)
    return f"/uploads/points/{name}"


# ---------------------------------------------------------------- 用户侧
@router.post("/points/signin")
def signin(user: dict = Depends(get_current_user)):
    """每天首次签到发放随机积分；当天重复调用只返回“已签到”。"""
    day = _bj_day()
    if query_one("SELECT user_id FROM daily_signin WHERE user_id = ? AND day = ?", (user["id"], day)):
        return {"ok": True, "already": True, "points": 0, "balance": _balance(user["id"])}
    cfg = _signin_config()
    points = random.randint(cfg["min"], cfg["max"])
    try:
        execute("INSERT INTO daily_signin (user_id, day, points, created_at) VALUES (?,?,?,?)",
                (user["id"], day, points, security.now_ms()))
    except Exception:
        # 并发重复签到：PK 冲突，按“已签到”处理
        return {"ok": True, "already": True, "points": 0, "balance": _balance(user["id"])}
    balance = _add_points(user["id"], points, "signin", day, "每日签到")
    return {"ok": True, "already": False, "points": points, "balance": balance}


@router.get("/points/me")
def me(user: dict = Depends(get_current_user)):
    day = _bj_day()
    signed = query_one("SELECT points FROM daily_signin WHERE user_id = ? AND day = ?", (user["id"], day))
    return {"ok": True, "balance": _balance(user["id"]), "signedToday": bool(signed), "day": day}


@router.get("/points/logs")
def logs(limit: int = 30, offset: int = 0, user: dict = Depends(get_current_user)):
    limit = min(max(limit, 1), 100)
    offset = max(offset, 0)
    rows = query_all("SELECT * FROM point_logs WHERE user_id = ? ORDER BY id DESC LIMIT ? OFFSET ?",
                     (user["id"], limit, offset))
    total = query_one("SELECT COUNT(*) AS c FROM point_logs WHERE user_id = ?", (user["id"],))["c"]
    return {"ok": True, "total": total, "logs": [{
        "id": r["id"], "delta": r["delta"], "balanceAfter": r["balance_after"],
        "type": r["type"], "ref": r["ref"] or "", "remark": r["remark"] or "",
        "createdAt": r["created_at"],
    } for r in rows]}


@router.get("/points/items")
def items(user: dict = Depends(get_current_user)):
    rows = query_all("SELECT * FROM point_items WHERE active = 1 ORDER BY id DESC")
    return {"ok": True, "balance": _balance(user["id"]), "items": [_item_out(r) for r in rows]}


@router.post("/points/redeem")
def redeem(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    iid = int(payload.get("itemId") or 0)
    item = query_one("SELECT * FROM point_items WHERE id = ? AND active = 1", (iid,))
    if not item:
        raise HTTPException(404, "商品不存在或已下架")
    if item["stock"] == 0:
        raise HTTPException(400, "库存不足")
    if _balance(user["id"]) < item["price"]:
        raise HTTPException(400, "积分不足")
    balance = _add_points(user["id"], -int(item["price"]), "redeem", item["name"], "兑换商品")
    if item["stock"] > 0:
        execute_update("UPDATE point_items SET stock = stock - 1 WHERE id = ? AND stock > 0", (iid,))
    rid = execute("INSERT INTO point_redemptions (user_id, item_id, item_name, price, status, created_at) "
                  "VALUES (?,?,?,?, 'redeemed', ?)",
                  (user["id"], iid, item["name"], item["price"], security.now_ms()))
    return {"ok": True, "balance": balance, "redemptionId": rid}


@router.post("/points/transfer")
def transfer(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    amount = int(payload.get("amount") or 0)
    to = str(payload.get("to") or "").strip()
    remark = str(payload.get("remark") or "")[:200]
    if amount <= 0:
        raise HTTPException(400, "转账积分需大于 0")
    if not to:
        raise HTTPException(400, "请填写对方用户名或昵称")
    target = query_one("SELECT * FROM users WHERE username = ?", (to,)) \
        or query_one("SELECT * FROM users WHERE nickname = ?", (to,))
    if not target:
        raise HTTPException(404, "对方用户不存在")
    if target["id"] == user["id"]:
        raise HTTPException(400, "不能给自己转账")
    if target["is_active"] is not None and not target["is_active"]:
        raise HTTPException(400, "对方账号不可用")
    if _balance(user["id"]) < amount:
        raise HTTPException(400, "积分不足")
    balance = _add_points(user["id"], -amount, "transfer_out", target["username"], remark or "转账")
    _add_points(target["id"], amount, "transfer_in", user["username"], remark or "转账")
    return {"ok": True, "balance": balance}


# ---------------------------------------------------------------- 管理侧
@router.get("/admin/signin-config")
def admin_get_signin(admin: dict = Depends(require_admin)):
    return {"ok": True, **_signin_config()}


@router.post("/admin/signin-config")
def admin_set_signin(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    lo = int(payload.get("min") or 0)
    hi = int(payload.get("max") or 0)
    if lo < 0 or hi < 0 or lo > hi:
        raise HTTPException(400, "随机积分区间不合法")
    _set_setting("signin_config", json.dumps({"min": lo, "max": hi}))
    return {"ok": True, **_signin_config()}


@router.get("/admin/point-items")
def admin_items(admin: dict = Depends(require_admin)):
    rows = query_all("SELECT * FROM point_items ORDER BY id DESC")
    return {"ok": True, "items": [_item_out(r, with_admin=True) for r in rows]}


@router.post("/admin/point-items")
async def admin_item_create(
    name: str = Form(...),
    description: str = Form(""),
    price: int = Form(0),
    stock: int = Form(-1),
    file: UploadFile | None = File(default=None),
    admin: dict = Depends(require_admin),
):
    if not name.strip():
        raise HTTPException(400, "请填写商品名")
    image = await _save_item_image(file)
    iid = execute("INSERT INTO point_items (name, description, image_path, price, stock, active, created_at) "
                  "VALUES (?,?,?,?,?,1,?)",
                  (name.strip(), description or "", image, max(price, 0), stock, security.now_ms()))
    return {"ok": True, "id": iid}


@router.post("/admin/point-items/update")
async def admin_item_update(
    id: int = Form(...),
    name: str = Form(...),
    description: str = Form(""),
    price: int = Form(0),
    stock: int = Form(-1),
    active: str = Form("1"),
    file: UploadFile | None = File(default=None),
    admin: dict = Depends(require_admin),
):
    item = query_one("SELECT * FROM point_items WHERE id = ?", (id,))
    if not item:
        raise HTTPException(404, "商品不存在")
    image = item["image_path"] or ""
    new_image = await _save_item_image(file)
    if new_image:
        image = new_image
    execute_update("UPDATE point_items SET name=?, description=?, image_path=?, price=?, stock=?, active=? WHERE id=?",
                   (name.strip(), description or "", image, max(price, 0), stock,
                    1 if str(active) in ("1", "true", "True") else 0, id))
    return {"ok": True}


@router.delete("/admin/point-items/{iid}")
def admin_item_delete(iid: int, admin: dict = Depends(require_admin)):
    if not query_one("SELECT id FROM point_items WHERE id = ?", (iid,)):
        raise HTTPException(404, "商品不存在")
    # 下架而非物理删除，保留兑换记录可追溯
    execute_update("UPDATE point_items SET active = 0 WHERE id = ?", (iid,))
    return {"ok": True}
