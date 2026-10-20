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
import pathlib
import urllib.parse
from typing import Optional

from fastapi import APIRouter, Body, Depends, File, Header, HTTPException, Request, UploadFile

from .. import security
from ..config import UPLOAD_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, resolve_user, user_public

router = APIRouter(prefix="/api/eat", tags=["eat"])

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


def _img(prompt: str) -> str:
    """示例图片：走在线生成接口，避免仓库里塞二进制资源。"""
    return ("https://trae-api-cn.mchost.guru/api/ide/v1/tool_text_to_image?prompt="
            + urllib.parse.quote(prompt) + "&image_size=landscape_4_3")


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
            "createdAt": s["created_at"], "recommend": _recommended(s["id"], 3)}


def _dish_out(d, store=None) -> dict:
    cnt = query_one("SELECT COUNT(*) c FROM eat_reviews WHERE dish_id = ?", (d["id"],))["c"]
    o = {"id": d["id"], "storeId": d["store_id"], "name": d["name"], "image": d["image"],
         "price": d["price"], "eatCount": int(cnt or 0), "ownerId": d["owner_id"],
         "createdAt": d["created_at"]}
    if store is not None:
        o["storeName"] = store["name"]
        o["location"] = store["location"]
    return o


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


def _global_task():
    return query_one("SELECT * FROM eat_tasks WHERE owner_id IS NULL ORDER BY id DESC LIMIT 1")


def _personal_task(uid: int):
    return query_one("SELECT * FROM eat_tasks WHERE owner_id = ? ORDER BY id DESC LIMIT 1", (uid,))


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


# ---------------------------------------------------------------- 种子数据（仅空库时写入）
_SEED = [
    {"name": "川味小馆", "location": "后街 1楼",
     "image": _img("spicy sichuan cuisine restaurant, kung pao chicken and mapo tofu on table"),
     "dishes": [("宫保鸡丁", 18, _img("kung pao chicken with peanuts and dried chili")),
                ("麻婆豆腐", 14, _img("mapo tofu in red spicy sauce")),
                ("回锅肉", 22, _img("twice cooked pork slices with green pepper")),
                ("水煮牛肉", 32, _img("sichuan boiled beef in chili oil"))]},
    {"name": "老张面馆", "location": "食堂 2楼",
     "image": _img("chinese noodle shop, beef noodle soup bowl close up"),
     "dishes": [("红烧牛肉面", 16, _img("braised beef noodle soup bowl")),
                ("番茄鸡蛋面", 13, _img("tomato egg noodle soup")),
                ("炸酱面", 15, _img("zhajiangmian noodles with soybean paste"))]},
    {"name": "深夜烧烤", "location": "夜市 3号",
     "image": _img("chinese bbq skewers grilling at night with smoke"),
     "dishes": [("羊肉串", 6, _img("grilled lamb skewers with cumin")),
                ("烤茄子", 12, _img("grilled eggplant with garlic and chili")),
                ("烤鸡翅", 8, _img("grilled chicken wings")),
                ("炒花甲", 25, _img("stir fried clams with chili"))]},
    {"name": "樱之屋日料", "location": "新餐厅 2楼",
     "image": _img("japanese restaurant, sushi platter and ramen bowl"),
     "dishes": [("三文鱼寿司", 28, _img("salmon sushi pieces on plate")),
                ("豚骨拉面", 26, _img("tonkotsu ramen bowl with soft boiled egg")),
                ("天妇罗", 22, _img("crispy tempura shrimp platter"))]},
    {"name": "甜品小站", "location": "一楼中庭",
     "image": _img("colorful dessert shop with milk tea and cakes"),
     "dishes": [("杨枝甘露", 16, _img("mango pomelo sago dessert in cup")),
                ("芋圆奶茶", 12, _img("taro ball milk tea cup")),
                ("抹茶千层", 20, _img("matcha mille crepe cake slice"))]},
    {"name": "沸腾火锅", "location": "老街 5楼",
     "image": _img("chinese hot pot with boiling spicy red soup"),
     "dishes": [("麻辣锅底", 38, _img("spicy hot pot soup base with chili")),
                ("肥牛卷", 28, _img("sliced beef rolls for hot pot")),
                ("虾滑", 26, _img("shrimp paste balls for hot pot")),
                ("毛肚", 30, _img("beef tripe slices for hot pot"))]},
]


def seed_eat_if_empty() -> None:
    if query_one("SELECT id FROM eat_stores LIMIT 1"):
        return
    for s in _SEED:
        sid = execute(
            "INSERT INTO eat_stores (name, image, location, owner_id, created_at) VALUES (?,?,?,?,?)",
            (s["name"], s["image"], s["location"], None, _now()),
        )
        for name, price, img in s["dishes"]:
            execute(
                "INSERT INTO eat_dishes (store_id, name, image, price, owner_id, created_at) VALUES (?,?,?,?,?,?)",
                (sid, name, img, price, None, _now()),
            )


# ---------------------------------------------------------------- 店铺 / 菜品
@router.get("/stores")
def list_stores():
    seed_eat_if_empty()
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
    seed_eat_if_empty()
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
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    return {"ok": True, "store": _store_out(s)}


@router.delete("/stores/{store_id}")
def delete_store(store_id: int, user: dict = Depends(get_current_user)):
    s = query_one("SELECT * FROM eat_stores WHERE id = ?", (store_id,))
    if not s:
        raise HTTPException(404, "店铺不存在")
    if not _may_edit(s, user):
        raise HTTPException(403, "无权删除该店铺")
    execute_update("DELETE FROM eat_reviews WHERE store_id = ?", (store_id,))
    execute_update("DELETE FROM eat_dishes WHERE store_id = ?", (store_id,))
    execute_update("DELETE FROM eat_stores WHERE id = ?", (store_id,))
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
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    return {"ok": True, "dish": _dish_out(d)}


@router.delete("/dishes/{dish_id}")
def delete_dish(dish_id: int, user: dict = Depends(get_current_user)):
    d = query_one("SELECT * FROM eat_dishes WHERE id = ?", (dish_id,))
    if not d:
        raise HTTPException(404, "菜品不存在")
    if not _may_edit(d, user):
        raise HTTPException(403, "无权删除该菜品")
    execute_update("DELETE FROM eat_reviews WHERE dish_id = ?", (dish_id,))
    execute_update("DELETE FROM eat_dishes WHERE id = ?", (dish_id,))
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
def _create_task(uid: int, store_id: Optional[int]) -> dict:
    ov = _override(uid)
    if store_id:
        row = query_one("SELECT * FROM eat_dishes WHERE store_id = ? ORDER BY RANDOM() LIMIT 1", (store_id,))
    else:
        row = query_one("SELECT * FROM eat_dishes ORDER BY RANDOM() LIMIT 1")
    if not row:
        raise HTTPException(400, "还没有菜品，先去添加吧")
    if ov:
        # 「我要吃别的」后：只写自己一个人的任务，不动全局结果
        execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (uid,))
        tid = execute("INSERT INTO eat_tasks (owner_id, dish_id, store_id, created_at) VALUES (?,?,?,?)",
                      (uid, row["id"], row["store_id"], _now()))
    else:
        # 全局共享：清掉旧的全局任务，写入新的一条，所有人看到同一结果
        execute_update("DELETE FROM eat_tasks WHERE owner_id IS NULL")
        tid = execute("INSERT INTO eat_tasks (owner_id, dish_id, store_id, created_at) VALUES (?,?,?,?)",
                      (None, row["id"], row["store_id"], _now()))
    return {"ok": True, "global": not ov, "task": _task_out(query_one("SELECT * FROM eat_tasks WHERE id = ?", (tid,)))}


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


@router.post("/task/other")
def task_other(user: dict = Depends(get_current_user)):
    """「我要吃别的」：给自己置 override，从此不跟随全局任务。"""
    _set_override(user["id"], True)
    execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (user["id"],))
    return {"ok": True, "overridden": True}


# ---------------------------------------------------------------- 评价（同评 + 点赞）
def _review_out(r, uid: Optional[int]) -> dict:
    author = query_one("SELECT * FROM users WHERE id = ?", (r["user_id"],))
    dish = query_one("SELECT * FROM eat_dishes WHERE id = ?", (r["dish_id"],)) if r["dish_id"] else None
    store = query_one("SELECT * FROM eat_stores WHERE id = ?", (r["store_id"],)) if r["store_id"] else None
    like_count = query_one("SELECT COUNT(*) c FROM eat_review_likes WHERE review_id = ?", (r["id"],))["c"]
    liked = bool(uid and query_one("SELECT 1 FROM eat_review_likes WHERE review_id = ? AND user_id = ?",
                                   (r["id"], uid)))
    same_count = query_one("SELECT COUNT(*) c FROM eat_reviews WHERE parent_id = ?", (r["id"],))["c"]
    return {"id": r["id"], "user": user_public(author) if author else {},
            "storeId": r["store_id"], "storeName": store["name"] if store else "",
            "dishId": r["dish_id"], "dishName": dish["name"] if dish else "",
            "price": dish["price"] if dish else 0, "dishImage": dish["image"] if dish else "",
            "parentId": r["parent_id"], "rating": r["rating"], "comment": r["comment"] or "",
            "image": r["image"] or "", "createdAt": r["created_at"],
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
    image = str(payload.get("image", ""))[:400]
    rid = execute(
        "INSERT INTO eat_reviews (user_id, store_id, dish_id, parent_id, rating, comment, image, created_at) "
        "VALUES (?,?,?,?,?,?,?,?)",
        (uid, store_id, dish_id, parent_id, rating, comment, image, _now()),
    )
    # 打完卡：清掉自己的任务；若跟随的是全局任务，给自己打 override，避免重复提示
    execute_update("DELETE FROM eat_tasks WHERE owner_id = ?", (uid,))
    if not parent and _global_task():
        _set_override(uid, True)
    r = query_one("SELECT * FROM eat_reviews WHERE id = ?", (rid,))
    return {"ok": True, "review": _review_out(r, uid)}


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


# ---------------------------------------------------------------- 图片墙 / 总结
@router.get("/wall")
def wall(limit: int = 300):
    rows = query_all("SELECT * FROM eat_reviews WHERE image <> '' ORDER BY id DESC LIMIT ?",
                     (max(1, min(limit, 500)),))
    out = []
    for r in rows:
        author = query_one("SELECT * FROM users WHERE id = ?", (r["user_id"],))
        dish = query_one("SELECT name FROM eat_dishes WHERE id = ?", (r["dish_id"],)) if r["dish_id"] else None
        store = query_one("SELECT name FROM eat_stores WHERE id = ?", (r["store_id"],)) if r["store_id"] else None
        out.append({"id": r["id"], "image": r["image"], "comment": r["comment"] or "",
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
        "SELECT * FROM eat_reviews WHERE user_id = ? AND created_at >= ? AND parent_id IS NULL "
        "ORDER BY id DESC", (uid, since))
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
