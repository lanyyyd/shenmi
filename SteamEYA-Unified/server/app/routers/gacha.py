"""抽卡接口。

用户侧（get_current_user）：
  GET  /api/gacha/info            卡池列表（含奖池一览、我的保底进度、余额）
  POST /api/gacha/draw            抽卡 {poolId, times:1|10}
  GET  /api/gacha/logs            我的抽卡记录（分页）
管理侧（require_admin）：
  GET  /api/admin/gacha-pools            卡池列表
  POST /api/admin/gacha-pools            新增卡池 {name, cost1, cost10}
  POST /api/admin/gacha-pools/update     修改卡池 {id, name, cost1, cost10, active}
  GET  /api/admin/gacha-prizes           奖品列表（可选 poolId）
  POST /api/admin/gacha-prizes           新增奖品（multipart，实物可传图片）
  POST /api/admin/gacha-prizes/update    修改奖品
  DELETE /api/admin/gacha-prizes/{id}    下架奖品（软删除，保留记录）
  GET  /api/admin/gacha-config           概率 / 保底配置
  POST /api/admin/gacha-config
  GET  /api/admin/gacha-logs             全部抽卡记录
"""
from __future__ import annotations

import pathlib

from fastapi import APIRouter, Body, Depends, File, Form, HTTPException, UploadFile

from .. import gachacore as GC
from .. import security
from ..config import GACHA_PRIZE_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api", tags=["gacha"])

_IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
_MAX_IMG = 2 * 1024 * 1024
_KINDS = ("points", "exp", "physical")
_RARITIES = ("normal", "premium", "rare", "limited")


def _balance(user_id: int) -> int:
    row = query_one("SELECT balance FROM point_accounts WHERE user_id = ?", (user_id,))
    return int(row["balance"]) if row else 0


def _public_rates() -> dict:
    r = GC.load_rates()
    return {"enabled": bool(r["enabled"]), "fiveStar": r["fiveStar"],
            "fourStar": r["fourStar"], "limitedRate": r["limitedRate"]}


# ---------------------------------------------------------------- 用户侧
@router.get("/gacha/info")
def gacha_info(user: dict = Depends(get_current_user)):
    rates = GC.load_rates()
    pools = []
    for p in GC.list_pools(active_only=True):
        pools.append({
            **GC.pool_out(p),
            "prizes": GC.pool_prizes(p["id"]),
            "state": GC.state_out(GC.get_state(user["id"], p["id"]), rates),
        })
    return {"ok": True, "enabled": bool(rates["enabled"]), "rates": _public_rates(),
            "balance": _balance(user["id"]), "pools": pools}


@router.post("/gacha/draw")
def gacha_draw(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    pool_id = int(payload.get("poolId") or 0)
    times = 10 if int(payload.get("times") or 1) == 10 else 1
    if not pool_id:
        raise HTTPException(400, "缺少卡池")
    res = GC.draw(user["id"], pool_id, times)
    if res.get("error"):
        raise HTTPException(400, res["error"])
    return {"ok": True, **res}


@router.get("/gacha/logs")
def gacha_logs(poolId: int = 0, rarity: str = "", limit: int = 50, offset: int = 0,
               user: dict = Depends(get_current_user)):
    limit = min(max(limit, 1), 100)
    offset = max(offset, 0)
    where = ["user_id = ?"]
    params: list = [user["id"]]
    if poolId:
        where.append("pool_id = ?")
        params.append(int(poolId))
    if rarity in _RARITIES:
        where.append("rarity = ?")
        params.append(rarity)
    sql_where = " WHERE " + " AND ".join(where)
    rows = query_all("SELECT * FROM gacha_logs" + sql_where + " ORDER BY id DESC LIMIT ? OFFSET ?",
                     (*params, limit, offset))
    total = query_one("SELECT COUNT(*) AS c FROM gacha_logs" + sql_where, tuple(params))["c"]
    pools = {p["id"]: p["name"] for p in GC.list_pools()}
    out = []
    for r in rows:
        out.append({
            "id": r["id"], "poolId": r["pool_id"], "poolName": pools.get(r["pool_id"], ""),
            "kind": r["kind"], "kindName": GC.KIND_NAMES.get(r["kind"], r["kind"]),
            "rarity": r["rarity"], "rarityName": GC.RARITY_NAMES.get(r["rarity"], r["rarity"]),
            "name": r["name"] or "", "value": int(r["value"] or 0), "image": r["image_path"] or "",
            "isLimited": bool(r["is_limited"]), "pullNo": int(r["pull_no"] or 0),
            "createdAt": r["created_at"],
        })
    return {"ok": True, "total": total, "logs": out}


# ---------------------------------------------------------------- 管理侧：概览
@router.get("/admin/gacha")
def admin_overview(admin: dict = Depends(require_admin)):
    return {"ok": True, "config": GC.load_rates(), "pools": [GC.pool_out(p) for p in GC.list_pools()]}


# ---------------------------------------------------------------- 卡池
@router.get("/admin/gacha-pools")
def admin_pools(admin: dict = Depends(require_admin)):
    pools = []
    for p in GC.list_pools():
        d = GC.pool_out(p)
        d["prizeCount"] = query_one("SELECT COUNT(*) AS c FROM gacha_prizes WHERE pool_id=? AND active=1",
                                    (p["id"],))["c"]
        pools.append(d)
    return {"ok": True, "pools": pools}


@router.post("/admin/gacha-pools")
def admin_pool_create(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    name = str(payload.get("name") or "").strip()[:30]
    if not name:
        raise HTTPException(400, "请填写卡池名称")
    c1 = max(int(payload.get("cost1") or 0), 0)
    c10 = max(int(payload.get("cost10") or 0), 0)
    sort = int(payload.get("sort") or (query_one("SELECT COALESCE(MAX(sort),0)+1 AS s FROM gacha_pools")["s"]))
    pid = execute("INSERT INTO gacha_pools (name, cost1, cost10, sort, active, created_at) VALUES (?,?,?,?,1,?)",
                  (name, c1, c10, sort, security.now_ms()))
    return {"ok": True, "id": pid}


@router.post("/admin/gacha-pools/update")
def admin_pool_update(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    pid = int(payload.get("id") or 0)
    p = GC.get_pool(pid)
    if not p:
        raise HTTPException(404, "卡池不存在")
    name = str(payload.get("name") if payload.get("name") is not None else p["name"]).strip()[:30] or p["name"]
    c1 = max(int(payload.get("cost1") if payload.get("cost1") is not None else p["cost1"]), 0)
    c10 = max(int(payload.get("cost10") if payload.get("cost10") is not None else p["cost10"]), 0)
    active = 1 if (payload.get("active") if payload.get("active") is not None else p["active"]) else 0
    sort = int(payload.get("sort") if payload.get("sort") is not None else p["sort"])
    execute_update("UPDATE gacha_pools SET name=?, cost1=?, cost10=?, active=?, sort=? WHERE id=?",
                   (name, c1, c10, active, sort, pid))
    return {"ok": True, "pool": GC.pool_out(GC.get_pool(pid))}


# ---------------------------------------------------------------- 奖品
async def _save_prize_image(file: UploadFile | None) -> str:
    if file is None or not file.filename:
        return ""
    ext = pathlib.Path(file.filename).suffix.lower()
    if ext not in _IMG_EXT:
        raise HTTPException(400, "奖品图片仅支持 png/jpg/gif/webp/bmp")
    data = await file.read()
    if len(data) > _MAX_IMG:
        raise HTTPException(413, "奖品图片需 2MB 以内")
    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    (GACHA_PRIZE_DIR / name).write_bytes(data)
    return f"/uploads/gacha/{name}"


def _validate_prize(kind: str, rarity: str, name: str, value: int):
    if kind not in _KINDS:
        raise HTTPException(400, "奖品类型不合法")
    if rarity not in _RARITIES:
        raise HTTPException(400, "稀有度不合法")
    if kind in ("points", "exp") and value <= 0:
        raise HTTPException(400, "请填写大于 0 的数量")
    if kind == "physical" and not name.strip():
        raise HTTPException(400, "实物奖品需填写奖品名")


@router.get("/admin/gacha-prizes")
def admin_prizes(poolId: int = 0, admin: dict = Depends(require_admin)):
    pools = [GC.pool_out(p) for p in GC.list_pools()]
    if not poolId and pools:
        poolId = pools[0]["id"]
    prizes = GC.pool_prizes(int(poolId), include_inactive=True) if poolId else []
    return {"ok": True, "pools": pools, "poolId": int(poolId), "prizes": prizes}


@router.post("/admin/gacha-prizes")
async def admin_prize_create(
    poolId: int = Form(...),
    kind: str = Form(...),
    rarity: str = Form(...),
    name: str = Form(""),
    description: str = Form(""),
    value: int = Form(0),
    stock: int = Form(-1),
    weight: int = Form(1),
    file: UploadFile | None = File(default=None),
    admin: dict = Depends(require_admin),
):
    if not GC.get_pool(poolId):
        raise HTTPException(404, "卡池不存在")
    name = name.strip()[:60]
    _validate_prize(kind, rarity, name, int(value))
    image = await _save_prize_image(file) if kind == "physical" else ""
    pid = execute(
        "INSERT INTO gacha_prizes (pool_id, kind, rarity, name, description, image_path, value, stock, weight, active, created_at) "
        "VALUES (?,?,?,?,?,?,?,?,?,1,?)",
        (poolId, kind, rarity, name, description.strip()[:500], image,
         max(int(value), 0), int(stock), max(int(weight), 1), security.now_ms()))
    return {"ok": True, "id": pid}


@router.post("/admin/gacha-prizes/update")
async def admin_prize_update(
    id: int = Form(...),
    kind: str = Form(...),
    rarity: str = Form(...),
    name: str = Form(""),
    description: str = Form(""),
    value: int = Form(0),
    stock: int = Form(-1),
    weight: int = Form(1),
    active: str = Form("1"),
    file: UploadFile | None = File(default=None),
    admin: dict = Depends(require_admin),
):
    row = query_one("SELECT * FROM gacha_prizes WHERE id = ?", (id,))
    if not row:
        raise HTTPException(404, "奖品不存在")
    name = name.strip()[:60]
    _validate_prize(kind, rarity, name, int(value))
    image = row["image_path"] or ""
    new_image = await _save_prize_image(file)
    if new_image:
        image = new_image
    if kind != "physical":
        image = ""
    execute_update(
        "UPDATE gacha_prizes SET kind=?, rarity=?, name=?, description=?, image_path=?, value=?, stock=?, weight=?, active=? WHERE id=?",
        (kind, rarity, name, description.strip()[:500], image, max(int(value), 0), int(stock),
         max(int(weight), 1), 1 if str(active) in ("1", "true", "True") else 0, id))
    return {"ok": True}


@router.delete("/admin/gacha-prizes/{pid}")
def admin_prize_delete(pid: int, admin: dict = Depends(require_admin)):
    if not query_one("SELECT id FROM gacha_prizes WHERE id = ?", (pid,)):
        raise HTTPException(404, "奖品不存在")
    # 下架而非物理删除，保留抽卡记录可追溯
    execute_update("UPDATE gacha_prizes SET active = 0 WHERE id = ?", (pid,))
    return {"ok": True}


# ---------------------------------------------------------------- 概率配置
@router.get("/admin/gacha-config")
def admin_get_config(admin: dict = Depends(require_admin)):
    return {"ok": True, "config": GC.load_rates()}


@router.post("/admin/gacha-config")
def admin_set_config(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    return {"ok": True, "config": GC.save_rates(payload or {})}


# ---------------------------------------------------------------- 抽卡记录
@router.get("/admin/gacha-logs")
def admin_logs(poolId: int = 0, keyword: str = "", rarity: str = "", kind: str = "",
               limit: int = 100, offset: int = 0, admin: dict = Depends(require_admin)):
    limit = min(max(limit, 1), 300)
    offset = max(offset, 0)
    where, params = [], []
    if poolId:
        where.append("l.pool_id = ?")
        params.append(int(poolId))
    if rarity in _RARITIES:
        where.append("l.rarity = ?")
        params.append(rarity)
    if kind in _KINDS:
        where.append("l.kind = ?")
        params.append(kind)
    kw = (keyword or "").strip()
    if kw:
        where.append("(u.username LIKE ? OR u.nickname LIKE ? OR l.name LIKE ?)")
        params += [f"%{kw}%"] * 3
    sql_where = (" WHERE " + " AND ".join(where)) if where else ""
    base = "FROM gacha_logs l LEFT JOIN users u ON u.id = l.user_id LEFT JOIN gacha_pools p ON p.id = l.pool_id" + sql_where
    rows = query_all(
        "SELECT l.*, u.username AS username, u.nickname AS nickname, p.name AS pool_name " + base +
        " ORDER BY l.id DESC LIMIT ? OFFSET ?", (*params, limit, offset))
    total = query_one("SELECT COUNT(*) AS c " + base, tuple(params))["c"]
    out = []
    for r in rows:
        out.append({
            "id": r["id"], "userId": r["user_id"],
            "username": r["username"] or "（已注销用户）", "nickname": r["nickname"] or "",
            "poolId": r["pool_id"], "poolName": r["pool_name"] or "",
            "kind": r["kind"], "kindName": GC.KIND_NAMES.get(r["kind"], r["kind"]),
            "rarity": r["rarity"], "rarityName": GC.RARITY_NAMES.get(r["rarity"], r["rarity"]),
            "name": r["name"] or "", "value": int(r["value"] or 0), "image": r["image_path"] or "",
            "isLimited": bool(r["is_limited"]), "pullNo": int(r["pull_no"] or 0),
            "guaranteedBefore": bool(r["guaranteed_before"]), "createdAt": r["created_at"],
        })
    rare = query_one("SELECT COUNT(*) AS c FROM gacha_logs WHERE rarity IN ('rare','limited')")["c"]
    limited = query_one("SELECT COUNT(*) AS c FROM gacha_logs WHERE rarity = 'limited'")["c"]
    return {"ok": True, "total": total, "rareTotal": rare, "limitedTotal": limited, "logs": out}