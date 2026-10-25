"""积分系统：每日签到随机积分 + 积分商城（兑换）+ 用户间转账 + 兑换订单兑现。

随机积分区间、商城商品均由后台配置。

用户侧（get_current_user）：
  POST   /api/points/signin     当天首次签到，发放随机积分（幂等）
  GET    /api/points/me         余额 + 今天是否已签到
  GET    /api/points/logs       积分明细（分页）
  GET    /api/points/items      商城商品（含当前余额）
  POST   /api/points/redeem     兑换商品
  POST   /api/points/transfer   给其他用户转账
  GET    /api/points/orders     我的订单（含管理员兑现的文字/图片/视频）
管理侧（require_admin）：
  GET    /api/admin/signin-config      签到随机积分区间
  POST   /api/admin/signin-config
  GET    /api/admin/point-items        商品列表（含下架）
  POST   /api/admin/point-items        新增（multipart，可传图片）
  POST   /api/admin/point-items/update 修改
  DELETE /api/admin/point-items/{id}   下架
  GET    /api/admin/orders             全部兑换订单（谁、何时、兑换了什么、状态）
  POST   /api/admin/orders/fulfill     兑现某订单（文字 + 图片/视频随订单下发）
  DELETE /api/admin/orders/files/{id}  删除某订单的某张兑现图片/视频
  POST   /api/admin/points/adjust      修改某用户积分（记 admin 明细）
"""
from __future__ import annotations

import json
import pathlib
import random
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, UploadFile

from .. import levelcore as LV
from .. import security
from ..config import ORDER_FILE_DIR, POINT_ITEM_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["points"])

_ITEM_IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
_MAX_ITEM_IMG = 2 * 1024 * 1024
_ORDER_IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
_ORDER_VIDEO_EXT = {".mp4", ".webm", ".mov", ".m4v", ".mkv", ".avi"}
_MAX_ORDER_FILE = 50 * 1024 * 1024
_DEFAULT_SIGNIN = {"min": 1, "max": 10}

# 积分明细 / 订单状态的中文名（前端可直接用 title/statusText 展示）
_LOG_TYPES = {"signin": "每日签到", "redeem": "兑换商品", "transfer_in": "收到转账",
              "transfer_out": "转出积分", "admin": "系统调整",
              "gacha": "抽卡", "redpacket_send": "发红包",
              "redpacket_grab": "抢红包", "redpacket_refund": "红包退回"}
_ORDER_STATUS = {"redeemed": "待兑现", "issued": "已兑现"}


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


def _bj_today():
    """北京时间的今天（签到按 UTC+8 计算日期）。"""
    return datetime.now(timezone(timedelta(hours=8))).date()


def _signin_days(user_id: int, days: int = 7) -> list[dict]:
    """最近 N 天（含今天）的签到情况，供前端画步骤条。

    status：signed 已签到 / missed 断签 / pending 今天还没签。
    """
    today = _bj_today()
    wanted = [today - timedelta(days=i) for i in range(days - 1, -1, -1)]
    start = wanted[0].strftime("%Y-%m-%d")
    rows = query_all("SELECT day, points FROM daily_signin WHERE user_id = ? AND day >= ?", (user_id, start))
    got = {r["day"]: int(r["points"] or 0) for r in rows}
    today_str = today.strftime("%Y-%m-%d")
    weekdays = "一二三四五六日"
    out = []
    for d in wanted:
        ds = d.strftime("%Y-%m-%d")
        if ds in got:
            status = "signed"
        else:
            status = "pending" if ds == today_str else "missed"
        out.append({
            "day": ds, "label": f"{d.month}/{d.day}", "weekday": weekdays[d.weekday()],
            "points": got.get(ds, 0), "status": status, "today": ds == today_str,
        })
    return out


def _signin_streak(user_id: int) -> int:
    """连续签到天数：从今天往前数（今天还没签则从昨天开始数）。"""
    today = _bj_today()
    got = {r["day"] for r in query_all("SELECT day FROM daily_signin WHERE user_id = ?", (user_id,))}
    start = 0 if today.strftime("%Y-%m-%d") in got else 1
    streak = 0
    for i in range(start, 400):
        if (today - timedelta(days=i)).strftime("%Y-%m-%d") in got:
            streak += 1
        else:
            break
    return streak


def _grant_signin_exp(user_id: int, day: str, streak: int) -> None:
    """签到经验：默认 +10；连续签到达到配置天数时按倍率加成。失败静默，不影响签到。"""
    try:
        spec = (LV.load_config()["sources"].get("signin") or {})
        need = int(spec.get("streakDays") or 0)
        mult = float(spec.get("streakMultiplier") or 1.0)
        if need <= 0 or streak < need:
            mult = 1.0
        LV.grant(user_id, "signin", ref=day, remark="每日签到", multiplier=mult)
    except Exception:
        pass


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


def _order_reply(rid: int) -> dict:
    """某订单的兑现内容：文字 + 图片/视频列表（用户端「我的订单」直接展示）。"""
    row = query_one("SELECT * FROM point_order_replies WHERE redemption_id = ?", (rid,))
    files = query_all("SELECT * FROM point_order_files WHERE redemption_id = ? ORDER BY id", (rid,))
    return {
        "text": (row["content"] if row else "") or "",
        "updatedAt": (row["updated_at"] if row else 0) or 0,
        "files": [{
            "id": f["id"], "kind": f["kind"], "url": f["file_path"] or "",
            "name": f["file_name"] or "", "size": f["file_size"] or 0,
            "createdAt": f["created_at"],
        } for f in files],
    }


def _order_out(r) -> dict:
    status = r["status"] or "redeemed"
    return {
        "id": r["id"], "itemId": r["item_id"], "itemName": r["item_name"] or "",
        "price": r["price"] or 0, "status": status,
        "statusText": _ORDER_STATUS.get(status, "待兑现"),
        "createdAt": r["created_at"], "reply": _order_reply(r["id"]),
    }


def _order_with_user(r) -> dict:
    """订单 + 下单用户（用户被删除时给出「已注销用户」兜底）。"""
    d = _order_out(r)
    username = ""
    nickname = ""
    if "username" in r.keys():
        username = r["username"] or ""
        nickname = r["nickname"] or ""
    d["userId"] = r["user_id"]
    d["username"] = username or "（已注销用户）"
    d["nickname"] = nickname
    return d


def _user_label(row) -> str:
    if not row:
        return "（已注销用户）"
    return (row["nickname"] or row["username"] or "") + f" (@{row['username']})"


async def _save_order_files(rid: int, files, now: int) -> int:
    """把兑现时上传的图片/视频落到 uploads/orders 并入库，返回成功保存数量。"""
    saved = 0
    for f in files or []:
        if f is None or not f.filename:
            continue
        ext = pathlib.Path(f.filename).suffix.lower()[:12]
        ctype = (f.content_type or "").lower()
        if ext in _ORDER_IMG_EXT or ctype.startswith("image/"):
            kind = "image"
        elif ext in _ORDER_VIDEO_EXT or ctype.startswith("video/"):
            kind = "video"
        else:
            raise HTTPException(400, f"只支持图片或视频：{f.filename}")
        data = await f.read()
        if not data:
            continue
        if len(data) > _MAX_ORDER_FILE:
            raise HTTPException(413, f"文件超过 50MB：{f.filename}")
        name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
        (ORDER_FILE_DIR / name).write_bytes(data)
        execute("INSERT INTO point_order_files (redemption_id, kind, file_path, file_name, file_size, created_at) "
                "VALUES (?,?,?,?,?,?)",
                (rid, kind, f"/uploads/orders/{name}", f.filename, len(data), now))
        saved += 1
    return saved


# ---------------------------------------------------------------- 用户侧
@router.post("/points/signin")
def signin(user: dict = Depends(get_current_user)):
    """每天首次签到发放随机积分；当天重复调用只返回“已签到”。

    返回值带上最近 7 天签到情况（days）与连续天数（streak），供签到弹窗的步骤条直接渲染。
    """
    day = _bj_day()

    def _result(points: int, already: bool, streak: int) -> dict:
        return {
            "ok": True, "already": already, "points": points,
            "balance": _balance(user["id"]),
            "streak": streak,
            "days": _signin_days(user["id"], 7),
        }

    if query_one("SELECT user_id FROM daily_signin WHERE user_id = ? AND day = ?", (user["id"], day)):
        return _result(0, True, _signin_streak(user["id"]))
    cfg = _signin_config()
    points = random.randint(cfg["min"], cfg["max"])
    try:
        execute("INSERT INTO daily_signin (user_id, day, points, created_at) VALUES (?,?,?,?)",
                (user["id"], day, points, security.now_ms()))
    except Exception:
        # 并发重复签到：PK 冲突，按“已签到”处理
        return _result(0, True, _signin_streak(user["id"]))
    _add_points(user["id"], points, "signin", day, "每日签到")
    streak = _signin_streak(user["id"])
    _grant_signin_exp(user["id"], day, streak)
    return _result(points, False, streak)


@router.get("/points/signin-week")
def signin_week(user: dict = Depends(get_current_user)):
    """最近 7 天签到情况（步骤条用）：已签到 / 断签 / 今天未签 + 连续签到天数。"""
    return {
        "ok": True, "days": _signin_days(user["id"], 7), "streak": _signin_streak(user["id"]),
        "balance": _balance(user["id"]),
        "signedToday": bool(query_one("SELECT user_id FROM daily_signin WHERE user_id = ? AND day = ?",
                                      (user["id"], _bj_day()))),
    }


@router.get("/points/me")
def me(user: dict = Depends(get_current_user)):
    day = _bj_day()
    signed = query_one("SELECT points FROM daily_signin WHERE user_id = ? AND day = ?", (user["id"], day))
    return {"ok": True, "balance": _balance(user["id"]), "signedToday": bool(signed), "day": day,
            "streak": _signin_streak(user["id"]), "days": _signin_days(user["id"], 7)}


@router.get("/points/logs")
def logs(limit: int = 30, offset: int = 0, user: dict = Depends(get_current_user)):
    limit = min(max(limit, 1), 100)
    offset = max(offset, 0)
    rows = query_all("SELECT * FROM point_logs WHERE user_id = ? ORDER BY id DESC LIMIT ? OFFSET ?",
                     (user["id"], limit, offset))
    total = query_one("SELECT COUNT(*) AS c FROM point_logs WHERE user_id = ?", (user["id"],))["c"]
    return {"ok": True, "total": total, "logs": [{
        "id": r["id"], "delta": r["delta"], "balanceAfter": r["balance_after"],
        "type": r["type"], "title": _LOG_TYPES.get(r["type"], "积分变动"),
        "ref": r["ref"] or "", "remark": r["remark"] or "",
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
    try:
        LV.grant(user["id"], "redeem", ref=item["name"] or "", remark="兑换商品")
    except Exception:
        pass
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
    try:
        LV.grant(user["id"], "transfer", ref=target["username"] or "", remark="转账")
    except Exception:
        pass
    return {"ok": True, "balance": balance}


@router.get("/points/orders")
def my_orders(limit: int = 50, offset: int = 0, user: dict = Depends(get_current_user)):
    """我的订单：自己兑换过的商品 + 管理员兑现的文字/图片/视频（可随时回看）。"""
    limit = min(max(limit, 1), 100)
    offset = max(offset, 0)
    rows = query_all("SELECT * FROM point_redemptions WHERE user_id = ? ORDER BY id DESC LIMIT ? OFFSET ?",
                     (user["id"], limit, offset))
    total = query_one("SELECT COUNT(*) AS c FROM point_redemptions WHERE user_id = ?", (user["id"],))["c"]
    return {"ok": True, "total": total, "balance": _balance(user["id"]),
            "orders": [_order_out(r) for r in rows]}


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


# ---------------------------------------------------------------- 兑换订单（管理侧）
@router.get("/admin/orders")
def admin_orders(status: str = "", keyword: str = "", limit: int = 100, offset: int = 0,
                 admin: dict = Depends(require_admin)):
    """全部兑换订单：谁、什么时候、兑换了什么、是否已兑现（含兑现内容）。"""
    limit = min(max(limit, 1), 200)
    offset = max(offset, 0)
    where: list[str] = []
    params: list = []
    if status in ("redeemed", "issued"):
        where.append("r.status = ?")
        params.append(status)
    kw = (keyword or "").strip()
    if kw:
        where.append("(u.username LIKE ? OR u.nickname LIKE ? OR r.item_name LIKE ?)")
        params += [f"%{kw}%"] * 3
    sql_where = ("WHERE " + " AND ".join(where)) if where else ""
    base = "FROM point_redemptions r LEFT JOIN users u ON u.id = r.user_id " + sql_where
    rows = query_all("SELECT r.*, u.username AS username, u.nickname AS nickname " + base +
                     " ORDER BY r.id DESC LIMIT ? OFFSET ?", (*params, limit, offset))
    total = query_one("SELECT COUNT(*) AS c " + base, tuple(params))["c"]
    pending = query_one("SELECT COUNT(*) AS c FROM point_redemptions WHERE status <> 'issued'")["c"]
    return {"ok": True, "total": total, "pending": pending,
            "orders": [_order_with_user(r) for r in rows]}


@router.post("/admin/orders/fulfill")
async def admin_fulfill(redemptionId: int = Form(...), text: str = Form(""),
                        files: list[UploadFile] = File(default=[]),
                        admin: dict = Depends(require_admin)):
    """兑现订单：保存文字说明与图片/视频（可多次追加），并把订单标记为已兑现。"""
    row = query_one("SELECT r.*, u.username AS username, u.nickname AS nickname "
                    "FROM point_redemptions r LEFT JOIN users u ON u.id = r.user_id WHERE r.id = ?",
                    (redemptionId,))
    if not row:
        raise HTTPException(404, "订单不存在")
    now = security.now_ms()
    saved = await _save_order_files(redemptionId, files, now)
    content = (text or "").strip()[:4000]
    exists = query_one("SELECT redemption_id FROM point_order_replies WHERE redemption_id = ?", (redemptionId,))
    if exists:
        execute_update("UPDATE point_order_replies SET content = ?, admin_id = ?, updated_at = ? "
                       "WHERE redemption_id = ?", (content, admin["id"], now, redemptionId))
    else:
        execute("INSERT INTO point_order_replies (redemption_id, content, admin_id, created_at, updated_at) "
                "VALUES (?,?,?,?,?)", (redemptionId, content, admin["id"], now, now))
    execute_update("UPDATE point_redemptions SET status = 'issued' WHERE id = ?", (redemptionId,))
    fresh = query_one("SELECT r.*, u.username AS username, u.nickname AS nickname "
                      "FROM point_redemptions r LEFT JOIN users u ON u.id = r.user_id WHERE r.id = ?",
                      (redemptionId,))
    return {"ok": True, "savedFiles": saved, "order": _order_with_user(fresh)}


@router.delete("/admin/orders/files/{fid}")
def admin_order_file_delete(fid: int, admin: dict = Depends(require_admin)):
    row = query_one("SELECT * FROM point_order_files WHERE id = ?", (fid,))
    if not row:
        raise HTTPException(404, "文件不存在")
    try:
        (ORDER_FILE_DIR / pathlib.Path(row["file_path"] or "").name).unlink(missing_ok=True)
    except Exception:
        pass
    execute_update("DELETE FROM point_order_files WHERE id = ?", (fid,))
    return {"ok": True}


@router.post("/admin/points/adjust")
def admin_points_adjust(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """修改某用户积分（正数增加 / 负数扣减），并写入一条「系统调整」明细。"""
    uid = int(payload.get("userId") or 0)
    if not uid:
        name = str(payload.get("username") or "").strip()
        row = query_one("SELECT id FROM users WHERE username = ?", (name,)) if name else None
        if not row:
            raise HTTPException(404, "用户不存在")
        uid = row["id"]
    if not query_one("SELECT id FROM users WHERE id = ?", (uid,)):
        raise HTTPException(404, "用户不存在")
    try:
        delta = int(payload.get("delta"))
    except Exception:
        raise HTTPException(400, "请填写积分变动数量")
    if delta == 0:
        raise HTTPException(400, "积分变动不能为 0")
    if abs(delta) > 10_000_000:
        raise HTTPException(400, "单次调整幅度过大")
    if _balance(uid) + delta < 0:
        raise HTTPException(400, "调整后余额不能为负数")
    remark = str(payload.get("remark") or "").strip()[:100]
    balance = _add_points(uid, delta, "admin", admin.get("username") or "admin", remark or "管理员调整")
    return {"ok": True, "userId": uid, "delta": delta, "balance": balance}
