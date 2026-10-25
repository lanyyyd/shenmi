"""「今天吃点啥」：店铺 / 菜品 / 全局共享转盘 / 评价（同评 + 点赞）/ 打卡总结。

设计要点：
- 转盘「全局共享」：任何人抽一次，写入一条 owner_id 为空的 eat_tasks，
  于是所有人打开看到的都是同一个结果、同一个任务（GET /task 返回它）。
- 某个用户可以点「我要吃别的」：给自己置 override，从此不跟随全局任务；
  他再抽只写自己的个人任务（owner_id=自己），不影响别人看到的结果。
- 评价支持「同评」：parent_id 指向原评价，可只打分 / 只写评语 / 只传图，也可以什么都不填。
- 点赞和朋友圈一样：唯一约束保证一人一次，再次点击即取消（toggle）。
- 店铺评分不落库：由该店所有菜品的所有评价（含同评）实时聚合出平均分。
"""
from __future__ import annotations

import datetime
import json
import logging
import pathlib
from typing import Optional

from fastapi import APIRouter, Body, Depends, File, Header, HTTPException, Request, UploadFile

from .. import security
from ..config import UPLOAD_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, resolve_user, user_public

router = APIRouter(prefix="/api/eat", tags=["eat"])

log = logging.getLogger("gateway.eat")

# 评价配图 / 菜品图落盘目录（本模块自带，避免改动全局 config）
EAT_DIR = UPLOAD_DIR / "eat"
EAT_DIR.mkdir(parents=True, exist_ok=True)

_IMG_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"}
_MAX_IMG = 8 * 1024 * 1024


# ---------------------------------------------------------------- 小工具
def _now() -> int:
    return security.now_ms()


def _opt_user(request: Request, authorization: Optional[str]) -> Optional[dict]:
    return resolve_user(request, authorization)


def _to_int(v, default=None):
    try:
        return int(v)
    except (TypeError, ValueError):
        return default


def _store_rating(store_id: int) -> tuple[Optional[float], int]:
    row = query_one(
        "SELECT AVG(r.rating) AS a, COUNT(r.rating) AS c FROM eat_reviews r "
        "JOIN eat_dishes d ON d.id = r.dish_id "
        "WHERE d.store_id = ? AND r.rating IS NOT NULL",
        (store_id,),
    )
    avg = row["a"] if row else None
    return (round(avg, 1) if avg is not None else None, int(row["c"] or 0) if row else 0)


def _recommended(store_id: int, limit: int = 3) -> list[dict]:
    """推荐菜：按「吃过次数」（被评价的次数，含同评）降序取前 N。"""
    rows = query_all(
        "SELECT d.id, d.name, d.price, d.image, COUNT(r.id) AS cnt FROM eat_dishes d "
        "LEFT JOIN eat_reviews r ON r.dish_id = d.id "
        "WHERE d.store_id = ? GROUP BY d.id ORDER BY cnt DESC, d.id ASC LIMIT ?",
        (store_id, limit),
    )
    return [{"id": r["id"], "name": r["name"], "price": r["price"], "image": r["image"],
             "eatCount": int(r["cnt"] or 0)} for r in rows]


def _store_out(s) -> dict:
    avg, cnt = _store_rating(s["id"])
    return {"id": s["id"], "name": s["name"], "image": s["image"], "location": s["location"],
            "rating": avg, "ratingCount": cnt, "ownerId": s["owner_id"],
            "createdAt": s["created_at"], "recommend": _recommended(s["id"], 3),
            "imageStatus": _img_status(s), "imageError": _img_error(s)}


def _dish_out(d, store=None) -> dict:
    cnt = query_one("SELECT COUNT(*) c FROM eat_reviews WHERE dish_id = ?", (d["id"],))["c"]
    o = {"id": d["id"], "storeId": d["store_id"], "name": d["name"], "image": d["image"],
         "price": d["price"], "eatCount": int(cnt or 0), "ownerId": d["owner_id"],
         "createdAt": d["created_at"], "imageStatus": _img_status(d), "imageError": _img_error(d)}
    if store is not None:
        o["storeName"] = store["name"]
        o["location"] = store["location"]
    return o


def _img_status(row) -> str:
    """AI 配图状态：老数据/用户图都按空处理，前端只在 queued/running 时显示占位。"""
    try:
        return str(row["image_status"] or "")
    except (IndexError, KeyError, TypeError):
        return ""


def _img_error(row) -> str:
    try:
        return str(row["image_error"] or "")
    except (IndexError, KeyError, TypeError):
        return ""


def _try_enqueue(kind: str, target_id: int) -> None:
    """新建无图对象后入 AI 配图队列；任何异常都不影响创建本身。"""
    try:
        from .. import aigen
        aigen.enqueue(kind, target_id)
    except Exception as exc:
        log.warning("AI 配图入队失败 %s#%s：%s", kind, target_id, exc)


def _cancel_jobs(kind: str, target_id: int, reason: str) -> None:
    execute_update(
        "UPDATE ai_image_jobs SET status = 'canceled', error = ?, updated_at = ?, finished_at = ? "
        "WHERE kind = ? AND target_id = ? AND status IN ('queued','running')",
        (reason[:200], _now(), _now(), kind, target_id))


def _mark_user_image(kind: str, target_id: int) -> None:
    """用户自己补了图：标记来源并取消该对象还没跑完的 AI 任务。"""
    table = "eat_stores" if kind == "store" else "eat_dishes"
    execute_update(
        f"UPDATE {table} SET image_status = '', image_error = '', image_source = 'user' WHERE id = ?",
        (target_id,))
    _cancel_jobs(kind, target_id, "用户已上传图片")


def _task_out(t) -> Optional[dict]:
    if not t:
        return None
    dish = query_one("SELECT * FROM eat_dishes WHERE id = ?", (t["dish_id"],))
    store = query_one("SELECT * FROM eat_stores WHERE id = ?", (t["store_id"],))
    avg, _ = _store_rating(t["store_id"]) if store else (None, 0)
    return {"id": t["id"], "ownerId": t["owner_id"], "global": t["owner_id"] is None,
            "dishId": t["dish_id"], "storeId": t["store_id"],
            "dishName": dish["name"] if dish else "", "dishImage": dish["image"] if dish else "",
            "price": dish["price"] if dish else 0,
            "storeName": store["name"] if store else "",
            "location": store["location"] if store else "",
            "rating": avg, "createdAt": t["created_at"]}


def _day_start_ms() -> int:
    """当天 00:00（服务器本地时区）的毫秒时间戳：任务只在当天有效。"""
    now = datetime.datetime.now()
    return int(now.replace(hour=0, minute=0, second=0, microsecond=0).timestamp() * 1000)


def _global_task():
    return query_one("SELECT * FROM eat_tasks WHERE owner_id IS NULL AND created_at >= ? "
                     "ORDER BY id DESC LIMIT 1", (_day_start_ms(),))


def _personal_task(uid: int):
    return query_one("SELECT * FROM eat_tasks WHERE owner_id = ? AND created_at >= ? "
                     "ORDER BY id DESC LIMIT 1", (uid, _day_start_ms()))


def _override(uid: int) -> bool:
    row = query_one("SELECT override FROM eat_optout WHERE user_id = ?", (uid,))
    return bool(row and row["override"])


def _set_override(uid: int, val: bool) -> None:
    execute(
        "INSERT INTO eat_optout (user_id, override, updated_at) VALUES (?, ?, ?) "
        "ON CONFLICT(user_id) DO UPDATE SET override = excluded.override, updated_at = excluded.updated_at",
        (uid, 1 if val else 0, _now()),
    )


def _may_edit(row, user: dict) -> bool:
    return bool(user.get("is_admin")) or (row is not None and row["owner_id"] == user["id"])


def _clean_images(raw) -> list[str]:
    """规整前端传来的图片数组：最多 9 张、去重、只收 /uploads/ 地址。"""
    out = []
    if isinstance(raw, list):
        for x in raw[:9]:
            s = str(x or "").strip()[:400]
            if s.startswith("/uploads/") and s not in out:
                out.append(s)
    return out


def _review_images(r) -> list[str]:
    """取一条评价的图片列表：优先 images（JSON 数组），回退单张 image，都没有则空。"""
    raw = ""
    try:
        raw = r["images"] or ""
    except (IndexError, KeyError, TypeError):
        raw = ""
    imgs = []
    if raw:
        try:
            parsed = json.loads(raw)
        except (ValueError, TypeError):
            parsed = None
        if isinstance(parsed, list):
            for x in parsed[:9]:
                s = str(x or "").strip()[:400]
                if s:
                    imgs.append(s)
    if not imgs:
        try:
            single = str(r["image"] or "").strip()
        except (IndexError, KeyError, TypeError):
            single = ""
        if single:
            imgs = [single[:400]]
    return imgs


# ---------------------------------------------------------------- 店铺 / 菜品
@router.get("/stores")
def list_stores():
    rows = query_all("SELECT * FROM eat_stores ORDER BY id DESC")
    return {"ok": True, "stores": [_store_out(s) for s in rows]}


@router.get("/stores/{store_id}")
def get_store(store_id: int):
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    if not s:
        raise HTTPException(404, "店铺不存在")
    return {"ok": True, "store": _store_out(s)}


@router.get("/stores/{store_id}/dishes")
def list_store_dishes(store_id: int):
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    if not s:
        raise HTTPException(404, "店铺不存在")
    rows = query_all("SELECT * FROM eat_dishes WHERE store_id = ? ORDER BY id ASC", (store_id,))
    return {"ok": True, "store": _store_out(s), "dishes": [_dish_out(d, s) for d in rows]}


@router.get("/dishes")
def list_dishes():
    rows = query_all(
        "SELECT d.*, s.name AS sname, s.location AS sloc FROM eat_dishes d "
        "LEFT JOIN eat_stores s ON s.id = d.store_id ORDER BY d.store_id ASC, d.id ASC")
    out = []
    for d in rows:
        o = _dish_out(d)
        o["storeName"] = d["sname"] or ""
        o["location"] = d["sloc"] or ""
        out.append(o)
    return {"ok": True, "dishes": out}


@router.post("/stores")
def add_store(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    name = str(payload.get("name", "")).strip()[:60]
    if not name:
        raise HTTPException(400, "请填写店铺名")
    sid = execute(
        "INSERT INTO eat_stores (name, image, location, owner_id, created_at) VALUES (?,?,?,?,?)",
        (name, str(payload.get("image", ""))[:400], str(payload.get("location", "")).strip()[:60],
         user["id"], _now()),
    )
    _try_enqueue("store", sid)
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (sid,))
    return {"ok": True, "store": _store_out(s)}


@router.put("/stores/{store_id}")
def update_store(store_id: int, payload: dict = Body(...), user: dict = Depends(get_current_user)):
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    if not s:
        raise HTTPException(404, "店铺不存在")
    if not _may_edit(s, user):
        raise HTTPException(403, "无权修改该店铺")
    sets, vals = [], []
    if "name" in payload:
        sets.append("name = ?"); vals.append(str(payload["name"]).strip()[:60])
    if "image" in payload:
        sets.append("image = ?"); vals.append(str(payload["image"])[:400])
    if "location" in payload:
        sets.append("location = ?"); vals.append(str(payload["location"]).strip()[:60])
    if sets:
        execute_update(f"UPDATE eat_stores SET {', '.join(sets)} WHERE id = ?", (*vals, store_id))
    if "image" in payload and str(payload.get("image") or "").strip():
        _mark_user_image("store", store_id)
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    return {"ok": True, "store": _store_out(s)}


# ---------------------------------------------------------------- AI 重新生图
def _queue_regen(kind: str, target_id: int, payload: dict) -> dict:
    from .. import aigen
    if not aigen.is_ready():
        raise HTTPException(400, "AI 生图未启用或未配置，请先在后台开启")
    extra = str((payload or {}).get("extraPrompt") or "").strip()[:300]
    jid = aigen.enqueue(kind, target_id, force=True, extra_prompt=extra, regen=True)
    if not jid:
        raise HTTPException(500, "创建生图任务失败")
    return {"ok": True, "jobId": jid, "status": "queued"}


@router.post("/stores/{store_id}/regen-image")
def regen_store_image(store_id: int, payload: dict = Body(default={}),
                      user: dict = Depends(get_current_user)):
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    if not s:
        raise HTTPException(404, "店铺不存在")
    if not _may_edit(s, user):
        raise HTTPException(403, "无权修改该店铺")
    return _queue_regen("store", store_id, payload)


@router.post("/dishes/{dish_id}/regen-image")
def regen_dish_image(dish_id: int, payload: dict = Body(default={}),
                     user: dict = Depends(get_current_user)):
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    if not d:
        raise HTTPException(404, "菜品不存在")
    if not _may_edit(d, user):
        raise HTTPException(403, "无权修改该菜品")
    return _queue_regen("dish", dish_id, payload)


@router.get("/image-status")
def image_status(kind: str = "", id: int = 0, user: dict = Depends(get_current_user)):
    table = "eat_stores" if kind == "store" else ("eat_dishes" if kind == "dish" else "")
    if not table:
        raise HTTPException(400, "参数错误")
    row = query_one(f"SELECT image, image_status, image_error FROM {table} WHERE id = ?", (id,))
    if not row:
        raise HTTPException(404, "对象不存在")
    return {"ok": True, "image": row["image"] or "", "status": row["image_status"] or "",
            "error": row["image_error"] or ""}


@router.delete("/stores/{store_id}")
def delete_store(store_id: int, user: dict = Depends(get_current_user)):
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    if not s:
        raise HTTPException(404, "店铺不存在")
    if not _may_edit(s, user):
        raise HTTPException(403, "无权删除该店铺")
    execute_update("DELETE FROM eat_review_likes WHERE review_id IN "
                   "(SELECT id FROM eat_reviews WHERE store_id = ?)", (store_id,))
    execute_update("DELETE FROM eat_review_guests WHERE review_id IN "
                   "(SELECT id FROM eat_reviews WHERE store_id = ?)", (store_id,))
    execute_update("DELETE FROM eat_reviews WHERE store_id = ?", (store_id,))
    execute_update("DELETE FROM eat_dishes WHERE store_id = ?", (store_id,))
    execute_update("DELETE FROM eat_stores WHERE id = ?", (store_id,))
    _cancel_jobs("store", store_id, "店铺已删除")
    execute_update("UPDATE ai_image_jobs SET status = 'canceled', error = '店铺已删除', updated_at = ?, "
                   "finished_at = ? WHERE kind = 'dish' AND status IN ('queued','running') "
                   "AND target_id NOT IN (SELECT id FROM eat_dishes)", (_now(), _now()))
    return {"ok": True}


@router.post("/dishes")
def add_dish(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    store_id = _to_int(payload.get("storeId"))
    name = str(payload.get("name", "")).strip()[:60]
    if not store_id or not query_one("SELECT id FROM eat_stores WHERE id = ?", (store_id,)):
        raise HTTPException(400, "请选择所属店铺")
    if not name:
        raise HTTPException(400, "请填写菜品名")
    did = execute(
        "INSERT INTO eat_dishes (store_id, name, image, price, owner_id, created_at) VALUES (?,?,?,?,?,?)",
        (store_id, name, str(payload.get("image", ""))[:400], float(payload.get("price") or 0),
         user["id"], _now()),
    )
    _try_enqueue("dish", did)
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (did,))
    return {"ok": True, "dish": _dish_out(d)}


@router.put("/dishes/{dish_id}")
def update_dish(dish_id: int, payload: dict = Body(...), user: dict = Depends(get_current_user)):
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    if not d:
        raise HTTPException(404, "菜品不存在")
    if not _may_edit(d, user):
        raise HTTPException(403, "无权修改该菜品")
    sets, vals = [], []
    if "name" in payload:
        sets.append("name = ?"); vals.append(str(payload["name"]).strip()[:60])
    if "image" in payload:
        sets.append("image = ?"); vals.append(str(payload["image"])[:400])
    if "price" in payload:
        sets.append("price = ?"); vals.append(float(payload.get("price") or 0))
    if "storeId" in payload:
        sid = _to_int(payload.get("storeId"))
        if sid and query_one("SELECT id FROM eat_stores WHERE id = ?", (sid,)):
            sets.append("store_id = ?"); vals.append(sid)
    if sets:
        execute_update(f"UPDATE eat_dishes SET {', '.join(sets)} WHERE id = ?", (*vals, dish_id))
    if "image" in payload and str(payload.get("image") or "").strip():
        _mark_user_image("dish", dish_id)
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    return {"ok": True, "dish": _dish_out(d)}


@router.delete("/dishes/{dish_id}")
def delete_dish(dish_id: int, user: dict = Depends(get_current_user)):
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    if not d:
        raise HTTPException(404, "菜品不存在")
    if not _may_edit(d, user):
        raise HTTPException(403, "无权删除该菜品")
    execute_update("DELETE FROM eat_review_likes WHERE review_id IN "
                   "(SELECT id FROM eat_reviews WHERE dish_id = ?)", (dish_id,))
    execute_update("DELETE FROM eat_review_guests WHERE review_id IN "
                   "(SELECT id FROM eat_reviews WHERE dish_id = ?)", (dish_id,))
    execute_update("DELETE FROM eat_reviews WHERE dish_id = ?", (dish_id,))
    execute_update("DELETE FROM eat_dishes WHERE id = ?", (dish_id,))
    _cancel_jobs("dish", dish_id, "菜品已删除")
    return {"ok": True}


@router.post("/upload")
async def upload_image(file: UploadFile = File(...), user: dict = Depends(get_current_user)):
    """评价配图 / 菜品图上传，落盘到 /uploads/eat/。"""
    data = await file.read()
    if len(data) > _MAX_IMG:
        raise HTTPException(413, "图片超过 8MB")
    ext = pathlib.Path(file.filename or "").suffix.lower()
    if ext not in _IMG_EXT:
        ext = ".jpg"
    name = f"{_now()}-{security.gen_token(6)}{ext}"
    (EAT_DIR / name).write_bytes(data)
    return {"ok": True, "url": f"/uploads/eat/{name}"}


# ---------------------------------------------------------------- 转盘 / 任务
def _set_task(uid: int, dish_row) -> dict:
    """把指定菜品设为任务：未「吃别的」→ 写全局共享任务；已「吃别的」→ 只写自己的个人任务。"""
    ov = _override(uid)
    if ov:
        # 「我要吃别的」后：只写自己一个人的任务，不动全局结果
        execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (uid,))
        tid = execute("INSERT INTO eat_tasks (owner_id, dish_id, store_id, created_at) VALUES (?,?,?,?)",
                      (uid, dish_row["id"], dish_row["store_id"], _now()))
    else:
        # 全局共享：清掉旧的全局任务，写入新的一条，所有人看到同一结果
        execute_update("DELETE FROM eat_tasks WHERE owner_id IS NULL")
        tid = execute("INSERT INTO eat_tasks (owner_id, dish_id, store_id, created_at) VALUES (?,?,?,?)",
                      (None, dish_row["id"], dish_row["store_id"], _now()))
    return {"ok": True, "global": not ov, "task": _task_out(query_one("SELECT * FROM eat_tasks WHERE id = ?", (tid,)))}


def _create_task(uid: int, store_id: Optional[int]) -> dict:
    if store_id:
        row = query_one("SELECT * FROM eat_dishes WHERE store_id = ? ORDER BY RANDOM() LIMIT 1", (store_id,))
    else:
        row = query_one("SELECT * FROM eat_dishes ORDER BY RANDOM() LIMIT 1")
    if not row:
        raise HTTPException(400, "还没有菜品，先去添加吧")
    return _set_task(uid, row)


@router.get("/task")
def get_task(user: dict = Depends(get_current_user)):
    uid = user["id"]
    personal = _personal_task(uid)
    if personal:
        t = personal
    elif _override(uid):
        t = None
    else:
        t = _global_task()
    return {"ok": True, "overridden": _override(uid), "task": _task_out(t)}


@router.post("/spin")
def spin(user: dict = Depends(get_current_user)):
    """抽奖：从全部店铺的全部菜品中随机抽一个（全局共享）。"""
    return _create_task(user["id"], None)


@router.post("/pick")
def pick(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    """「就吃这家了！」：从指定店铺里随机挑一个菜。"""
    return _create_task(user["id"], _to_int(payload.get("storeId")))


@router.post("/choose")
def choose(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    """「就吃这个了！」：把餐品详情页里选中的那个具体菜品设为任务。"""
    dish_id = _to_int(payload.get("dishId"))
    if not dish_id:
        raise HTTPException(400, "缺少菜品")
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    if not d:
        raise HTTPException(404, "菜品不存在")
    return _set_task(user["id"], d)


@router.post("/task/other")
def task_other(user: dict = Depends(get_current_user)):
    """「我要吃别的」：给自己置 override，从此不跟随全局任务。"""
    _set_override(user["id"], True)
    execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (user["id"],))
    return {"ok": True, "overridden": True}


# ---------------------------------------------------------------- 参与者列表
@router.get("/users")
def eat_users(request: Request, authorization: Optional[str] = Header(default=None)):
    """评价时可多选的「一起吃」小伙伴（登录后可见，只返回昵称等公开字段）。"""
    u = _opt_user(request, authorization)
    if not u:
        raise HTTPException(401, "请先登录")
    rows = query_all("SELECT * FROM users WHERE is_active = 1 ORDER BY id ASC")
    return {"ok": True, "users": [user_public(r) for r in rows]}


# ---------------------------------------------------------------- 评价（同评 + 点赞）
def _review_out(r, uid: Optional[int]) -> dict:
    author = query_one("SELECT * FROM users WHERE id = ?", (r["user_id"],))
    dish = query_one("SELECT * FROM eat_dishes WHERE id = ?", (r["dish_id"],)) if r["dish_id"] else None
    store = query_one("SELECT * FROM eat_stores WHERE id = ?", (r["store_id"],)) if r["store_id"] else None
    like_count = query_one("SELECT COUNT(*) c FROM eat_review_likes WHERE review_id = ?", (r["id"],))["c"]
    liked = bool(uid and query_one("SELECT 1 FROM eat_review_likes WHERE review_id = ? AND user_id = ?",
                                   (r["id"], uid)))
    same_count = query_one("SELECT COUNT(*) c FROM eat_reviews WHERE parent_id = ?", (r["id"],))["c"]
    guests = []
    for g in query_all("SELECT user_id FROM eat_review_guests WHERE review_id = ? ORDER BY user_id ASC", (r["id"],)):
        gu = query_one("SELECT * FROM users WHERE id = ?", (g["user_id"],))
        if gu:
            guests.append(user_public(gu))
    imgs = _review_images(r)
    return {"id": r["id"], "user": user_public(author) if author else {},
            "storeId": r["store_id"], "storeName": store["name"] if store else "",
            "dishId": r["dish_id"], "dishName": dish["name"] if dish else "",
            "price": dish["price"] if dish else 0, "dishImage": dish["image"] if dish else "",
            "parentId": r["parent_id"], "rating": r["rating"], "comment": r["comment"] or "",
            "image": imgs[0] if imgs else "", "images": imgs, "createdAt": r["created_at"],
            "guests": guests,
            "likeCount": int(like_count or 0), "liked": liked, "sameCount": int(same_count or 0)}


@router.get("/reviews")
def list_reviews(request: Request, limit: int = 50, offset: int = 0,
                 authorization: Optional[str] = Header(default=None)):
    u = _opt_user(request, authorization)
    uid = u["id"] if u else None
    limit = max(1, min(limit, 100))
    offset = max(0, offset)
    rows = query_all("SELECT * FROM eat_reviews WHERE parent_id IS NULL ORDER BY id DESC LIMIT ? OFFSET ?",
                     (limit, offset))
    total = query_one("SELECT COUNT(*) c FROM eat_reviews WHERE parent_id IS NULL")["c"]
    out = []
    for r in rows:
        o = _review_out(r, uid)
        kids = query_all("SELECT * FROM eat_reviews WHERE parent_id = ? ORDER BY id ASC", (r["id"],))
        o["sames"] = [_review_out(k, uid) for k in kids]
        out.append(o)
    return {"ok": True, "reviews": out, "total": int(total or 0),
            "hasMore": offset + len(rows) < int(total or 0)}


@router.post("/reviews")
def add_review(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    """新建评价 / 同评。rating / comment / image 全可空（同评可以什么都不填）。"""
    uid = user["id"]
    parent_id = _to_int(payload.get("parentId"))
    parent = None
    if parent_id:
        parent = query_one("SELECT * FROM eat_reviews WHERE id = ?", (parent_id,))
        if not parent:
            raise HTTPException(404, "原评价不存在")
        # 禁止套娃：跟评只能挂在一级评价下，若父级本身是跟评则挂到它的主评
        if parent["parent_id"]:
            root = query_one("SELECT * FROM eat_reviews WHERE id = ?", (parent["parent_id"],))
            if root:
                parent = root
                parent_id = root["id"]
    store_id = _to_int(payload.get("storeId")) or (parent["store_id"] if parent else None)
    dish_id = _to_int(payload.get("dishId")) or (parent["dish_id"] if parent else None)
    if not dish_id:
        raise HTTPException(400, "请选择要评价的菜品")
    # 只给了菜品没给店铺时，从菜品反查店铺，保证评价能统计到店铺评分/总结
    if not store_id:
        drow = query_one("SELECT store_id FROM eat_dishes WHERE id = ?", (dish_id,))
        store_id = drow["store_id"] if drow else None
    rating = _to_int(payload.get("rating"))
    if rating is not None:
        rating = max(1, min(5, rating))
    comment = str(payload.get("comment", ""))[:500]
    # 多图：优先 images 数组，兼容旧的单张 image 字段；image 列存第一张
    images = _clean_images(payload.get("images")) if "images" in payload else _clean_images([payload.get("image")])
    image = images[0] if images else ""
    rid = execute(
        "INSERT INTO eat_reviews (user_id, store_id, dish_id, parent_id, rating, comment, image, images, created_at) "
        "VALUES (?,?,?,?,?,?,?,?,?)",
        (uid, store_id, dish_id, parent_id, rating, comment, image, json.dumps(images), _now()),
    )
    # 「一起吃」：勾选的小伙伴也计入这条评价的统计（不能选自己，自动去重）
    # 只有主评需要成员；同评不提供该选项，传入也忽略
    guests = []
    raw_guests = payload.get("guestIds") or []
    if parent is None and isinstance(raw_guests, list):
        for g in raw_guests[:30]:
            gid = _to_int(g)
            if not gid or gid == uid or gid in guests:
                continue
            if not query_one("SELECT id FROM users WHERE id = ? AND is_active = 1", (gid,)):
                continue
            execute("INSERT OR IGNORE INTO eat_review_guests (review_id, user_id, created_at) VALUES (?,?,?)",
                    (rid, gid, _now()))
            guests.append(gid)

    # 当前任务：个人任务优先，否则（未「吃别的」）跟随全局任务
    cur = _personal_task(uid)
    if cur is None and not _override(uid):
        cur = _global_task()
    matched = bool(cur and _to_int(cur["dish_id"]) == dish_id)
    if parent is None:
        # 打完卡：清掉自己的任务；若跟随的是全局任务，给自己打 override，避免重复提示
        execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (uid,))
        if _global_task():
            _set_override(uid, True)
    elif matched:
        # 跟评的菜品与当前任务一致 → 同样算完成任务
        execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (uid,))
        if cur["owner_id"] is None:
            _set_override(uid, True)
    r = query_one("SELECT * FROM eat_reviews WHERE id = ?", (rid,))
    return {"ok": True, "review": _review_out(r, uid),
            "taskCompleted": bool(matched or parent is None), "guests": guests}


@router.post("/reviews/{review_id}/like")
def toggle_like(review_id: int, user: dict = Depends(get_current_user)):
    """点赞 / 取消点赞（朋友圈式：只能点一次，再点即取消）。"""
    if not query_one("SELECT id FROM eat_reviews WHERE id = ?", (review_id,)):
        raise HTTPException(404, "评价不存在")
    uid = user["id"]
    if query_one("SELECT 1 FROM eat_review_likes WHERE review_id = ? AND user_id = ?", (review_id, uid)):
        execute_update("DELETE FROM eat_review_likes WHERE review_id = ? AND user_id = ?", (review_id, uid))
        liked = False
    else:
        execute("INSERT INTO eat_review_likes (review_id, user_id, created_at) VALUES (?,?,?)",
                (review_id, uid, _now()))
        liked = True
    count = query_one("SELECT COUNT(*) c FROM eat_review_likes WHERE review_id = ?", (review_id,))["c"]
    return {"ok": True, "liked": liked, "likeCount": int(count or 0)}


@router.put("/reviews/{review_id}")
def update_review(review_id: int, payload: dict = Body(...), user: dict = Depends(get_current_user)):
    """编辑评价 / 同评（仅作者本人）：评分、评语、图片。

    - 菜品与店铺不可改，避免消费记录串账；
    - 「一起吃」成员仅主评可调整，同评忽略 guestIds；
    - 编辑不会改变任务完成状态。
    """
    r = query_one("SELECT * FROM eat_reviews WHERE id = ?", (review_id,))
    if not r:
        raise HTTPException(404, "评价不存在")
    if int(r["user_id"]) != int(user["id"]):
        raise HTTPException(403, "只能编辑自己的评价")
    if "rating" in payload:
        rating = _to_int(payload.get("rating"))
        if rating is not None:
            rating = max(1, min(5, rating))
    else:
        rating = r["rating"]
    comment = str(payload.get("comment") or "")[:500] if "comment" in payload else (r["comment"] or "")
    # 多图：传了 images 就整体替换；未传则保持原值不变（不要清空）
    if "images" in payload:
        images = _clean_images(payload.get("images"))
        image = images[0] if images else ""
        images_json = json.dumps(images)
    else:
        images_json = r["images"] or ""
        image = str(payload.get("image") or "")[:400] if "image" in payload else (r["image"] or "")
    execute_update("UPDATE eat_reviews SET rating = ?, comment = ?, image = ?, images = ? WHERE id = ?",
                   (rating, comment, image, images_json, review_id))
    if r["parent_id"] is None and payload.get("guestIds") is not None:
        execute_update("DELETE FROM eat_review_guests WHERE review_id = ?", (review_id,))
        raw_guests = payload.get("guestIds") or []
        seen = []
        if isinstance(raw_guests, list):
            for g in raw_guests[:30]:
                gid = _to_int(g)
                if not gid or gid == user["id"] or gid in seen:
                    continue
                if not query_one("SELECT id FROM users WHERE id = ? AND is_active = 1", (gid,)):
                    continue
                execute("INSERT OR IGNORE INTO eat_review_guests (review_id, user_id, created_at) VALUES (?,?,?)",
                        (review_id, gid, _now()))
                seen.append(gid)
    r = query_one("SELECT * FROM eat_reviews WHERE id = ?", (review_id,))
    return {"ok": True, "review": _review_out(r, user["id"])}


@router.delete("/reviews/{review_id}")
def delete_review(review_id: int, user: dict = Depends(get_current_user)):
    """删除评价（作者本人或管理员）。

    - 若该评价下面挂着「同评」：第一条同评自动升为主评，其余同评改挂到新主评下，顺序不变；
    - 该评价的点赞一并清除；
    - 评价本身就是消费记录，删除后自动不再计入店铺平均分、我的评价、月度/年度总结与图片墙。
    """
    r = query_one("SELECT * FROM eat_reviews WHERE id = ?", (review_id,))
    if not r:
        raise HTTPException(404, "评价不存在")
    if not (user.get("is_admin") or r["user_id"] == user["id"]):
        raise HTTPException(403, "只能删除自己发布的评价")

    kids = query_all("SELECT id FROM eat_reviews WHERE parent_id = ? ORDER BY id ASC", (review_id,))
    promoted_id = None
    if kids:
        promoted_id = kids[0]["id"]
        execute_update("UPDATE eat_reviews SET parent_id = NULL WHERE id = ?", (promoted_id,))
        for k in kids[1:]:
            execute_update("UPDATE eat_reviews SET parent_id = ? WHERE id = ?", (promoted_id, k["id"]))
    execute_update("DELETE FROM eat_review_likes WHERE review_id = ?", (review_id,))
    execute_update("DELETE FROM eat_review_guests WHERE review_id = ?", (review_id,))
    execute_update("DELETE FROM eat_reviews WHERE id = ?", (review_id,))
    return {"ok": True, "deleted": review_id, "promotedId": promoted_id}


# ---------------------------------------------------------------- 图片墙 / 总结
@router.get("/wall")
def wall(limit: int = 300):
    rows = query_all(
        "SELECT * FROM eat_reviews "
        "WHERE (image IS NOT NULL AND image <> '') "
        "OR (images IS NOT NULL AND images <> '' AND images <> '[]') "
        "ORDER BY id DESC LIMIT ?",
        (max(1, min(limit, 500)),))
    out = []
    for r in rows:
        imgs = _review_images(r)
        if not imgs:
            continue
        author = query_one("SELECT * FROM users WHERE id = ?", (r["user_id"],))
        dish = query_one("SELECT name FROM eat_dishes WHERE id = ?", (r["dish_id"],)) if r["dish_id"] else None
        store = query_one("SELECT name FROM eat_stores WHERE id = ?", (r["store_id"],)) if r["store_id"] else None
        # 一条评价的每一张图都进墙：同一评价的多张图会产生多条
        for url in imgs:
            out.append({"id": r["id"], "image": url, "comment": r["comment"] or "",
                        "dishName": dish["name"] if dish else "", "storeName": store["name"] if store else "",
                        "user": user_public(author) if author else {}, "createdAt": r["created_at"]})
    return {"ok": True, "images": out}


@router.get("/summary")
def summary(request: Request, period: str = "month", authorization: Optional[str] = Header(default=None)):
    u = _opt_user(request, authorization)
    if not u:
        raise HTTPException(401, "请先登录")
    uid = u["id"]
    now_dt = datetime.datetime.now()
    if period == "year":
        start_dt = now_dt.replace(month=1, day=1, hour=0, minute=0, second=0, microsecond=0)
    else:
        start_dt = now_dt.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
    since = int(start_dt.timestamp() * 1000)

    rows = query_all(
        "SELECT r.* FROM eat_reviews r WHERE r.created_at >= ? AND r.parent_id IS NULL "
        "AND (r.user_id = ? OR EXISTS (SELECT 1 FROM eat_review_guests g "
        "     WHERE g.review_id = r.id AND g.user_id = ?)) "
        "ORDER BY r.id DESC", (since, uid, uid))
    count = len(rows)
    spend = 0.0
    store_hits: dict[str, int] = {}
    dish_hits: dict[str, int] = {}
    images = []
    for r in rows:
        dish = query_one("SELECT * FROM eat_dishes WHERE id = ?", (r["dish_id"],)) if r["dish_id"] else None
        store = query_one("SELECT * FROM eat_stores WHERE id = ?", (r["store_id"],)) if r["store_id"] else None
        if dish:
            spend += float(dish["price"] or 0)
            dish_hits[dish["name"]] = dish_hits.get(dish["name"], 0) + 1
        if store:
            store_hits[store["name"]] = store_hits.get(store["name"], 0) + 1
        if r["image"]:
            images.append({"image": r["image"], "storeName": store["name"] if store else "",
                           "dishName": dish["name"] if dish else ""})
    top_store = max(store_hits.items(), key=lambda kv: kv[1])[0] if store_hits else ""
    top_dish = max(dish_hits.items(), key=lambda kv: kv[1])[0] if dish_hits else ""
    return {"ok": True, "period": period, "count": count, "spend": round(spend, 2),
            "topStore": top_store, "topDish": top_dish,
            "stores": [{"name": k, "count": v} for k, v in sorted(store_hits.items(), key=lambda kv: -kv[1])],
            "dishes": [{"name": k, "count": v} for k, v in sorted(dish_hits.items(), key=lambda kv: -kv[1])],
            "images": images}
