"""群红包核心逻辑。

- 资产：积分 / 经验（**经验红包仅管理员可发**）；
- 模式：random（总额 + 个数，二倍均值随机拆分，每人 ≥1）/ fixed（单个数量 × 个数）；
- 管理员发红包不扣余额；其他用户发积分红包从余额扣除；
- 每人每个红包限领 1 次，发包者本人不可领；
- 24 小时未领完：剩余部分退回发包者（管理员红包未扣款，故无退回）。
"""
from __future__ import annotations

import random

from . import chat_logic as CL
from . import levelcore, security
from .db import execute, execute_update, query_all, query_one
from .wallet import add_points, balance

ASSETS = ("points", "exp")
MODES = ("random", "fixed")
ASSET_NAMES = {"points": "积分", "exp": "经验"}
MODE_NAMES = {"random": "拼手气", "fixed": "固定"}

EXPIRE_HOURS = 24
MAX_COUNT = 100
MAX_TOTAL = 1_000_000
MIN_PER = 1


def _now() -> int:
    return security.now_ms()


def _status(r) -> str:
    if int(r["grabbed_count"]) >= int(r["count"]):
        return "done"
    if _now() > int(r["expire_at"]):
        return "expired"
    return "active"


def get(packet_id: int):
    return query_one("SELECT * FROM red_packets WHERE id = ?", (packet_id,))


def _brief_from(r, sender, viewer_id: int | None, my_amount) -> dict:
    st = _status(r)
    return {
        "id": r["id"], "asset": r["asset"], "assetName": ASSET_NAMES.get(r["asset"], r["asset"]),
        "mode": r["mode"], "modeName": MODE_NAMES.get(r["mode"], r["mode"]),
        "total": int(r["total"]), "count": int(r["count"]), "per": int(r["per"]),
        "remark": r["remark"] or "", "senderId": r["sender_id"],
        "senderName": (sender["nickname"] or sender["username"]) if sender else "已注销用户",
        "isAdminFree": bool(r["admin_free"]),
        "grabbedCount": int(r["grabbed_count"]), "grabbedAmount": int(r["grabbed_amount"]),
        "status": st, "expireAt": int(r["expire_at"]), "createdAt": int(r["created_at"]),
        "isMine": bool(viewer_id and viewer_id == r["sender_id"]),
        "myAmount": my_amount,
        "canGrab": bool(viewer_id and st == "active" and viewer_id != r["sender_id"] and my_amount is None),
    }


def brief(packet_id: int, viewer_id: int | None = None) -> dict | None:
    r = get(packet_id)
    if not r:
        return None
    sender = query_one("SELECT username, nickname FROM users WHERE id = ?", (r["sender_id"],))
    my = None
    if viewer_id:
        g = query_one("SELECT amount FROM red_packet_grabs WHERE packet_id = ? AND user_id = ?",
                      (packet_id, viewer_id))
        my = int(g["amount"]) if g else None
    return _brief_from(r, sender, viewer_id, my)


def briefs(packet_ids, viewer_id: int | None = None) -> dict:
    """批量取红包简报（历史/搜索列表用，避免逐条查询）。返回 {packet_id: brief}。"""
    ids = []
    for i in packet_ids:
        try:
            v = int(i or 0)
        except Exception:
            v = 0
        if v and v not in ids:
            ids.append(v)
    if not ids:
        return {}
    marks = ",".join("?" * len(ids))
    rows = query_all(f"SELECT * FROM red_packets WHERE id IN ({marks})", tuple(ids))
    if not rows:
        return {}
    sids = list({r["sender_id"] for r in rows})
    smarks = ",".join("?" * len(sids))
    smap = {s["id"]: s for s in query_all(
        f"SELECT id, username, nickname FROM users WHERE id IN ({smarks})", tuple(sids))}
    mine: dict[int, int] = {}
    if viewer_id:
        for g in query_all(
                f"SELECT packet_id, amount FROM red_packet_grabs WHERE user_id = ? AND packet_id IN ({marks})",
                (viewer_id, *ids)):
            mine[g["packet_id"]] = int(g["amount"])
    return {r["id"]: _brief_from(r, smap.get(r["sender_id"]), viewer_id, mine.get(r["id"])) for r in rows}


def detail(packet_id: int, viewer_id: int | None = None) -> dict | None:
    d = brief(packet_id, viewer_id)
    if not d:
        return None
    rows = query_all(
        "SELECT g.user_id, g.amount, g.created_at, "
        "COALESCE(NULLIF(u.nickname,''), u.username, '已注销用户') AS name "
        "FROM red_packet_grabs g LEFT JOIN users u ON u.id = g.user_id "
        "WHERE g.packet_id = ? ORDER BY g.id", (packet_id,))
    d["grabs"] = [{"userId": x["user_id"], "name": x["name"], "amount": int(x["amount"]),
                   "createdAt": int(x["created_at"])} for x in rows]
    return d


def _random_amount(remaining: int, left_cnt: int) -> int:
    """二倍均值法；保证每人 ≥1 且总和恰好用完。"""
    if left_cnt <= 1:
        return remaining
    hi = int(remaining / left_cnt * 2)
    if hi < 1:
        hi = 1
    hi = min(hi, remaining - (left_cnt - 1))
    if hi < 1:
        hi = 1
    return random.randint(1, hi)


def create(sender: dict, asset: str, mode: str, total: int = 0, count: int = 0,
           per: int = 0, remark: str = "") -> dict:
    if asset not in ASSETS:
        return {"error": "红包类型不合法"}
    if mode not in MODES:
        return {"error": "红包模式不合法"}
    admin = bool(sender.get("is_admin"))
    if asset == "exp" and not admin:
        return {"error": "只有管理员可以发经验红包"}
    try:
        count = int(count)
        total = int(total or 0)
        per = int(per or 0)
    except Exception:
        return {"error": "数量参数不合法"}
    if count < 1 or count > MAX_COUNT:
        return {"error": f"红包个数需在 1~{MAX_COUNT} 之间"}
    if mode == "fixed":
        if per < MIN_PER:
            return {"error": "单个金额至少 1"}
        total = per * count
    else:
        per = 0
        if total < count:
            return {"error": "随机红包总额不能小于个数（每人至少 1）"}
    if total < 1 or total > MAX_TOTAL:
        return {"error": f"红包总额需在 1~{MAX_TOTAL} 之间"}
    if asset == "points" and not admin:
        bal = balance(sender["id"])
        if bal < total:
            return {"error": "积分不足", "need": total, "balance": bal}

    remark = (remark or "").strip()[:40]
    now = _now()
    pid = execute(
        "INSERT INTO red_packets (room, sender_id, asset, mode, total, count, per, remark, admin_free, expire_at, created_at) "
        "VALUES ('group',?,?,?,?,?,?,?,?,?,?)",
        (sender["id"], asset, mode, total, count, per, remark,
         1 if admin else 0, now + EXPIRE_HOURS * 3600 * 1000, now))
    # 扣款（管理员不扣）
    if asset == "points" and not admin:
        add_points(sender["id"], -total, "redpacket_send", f"红包#{pid}", remark or "发红包")
    # 生成群聊消息（内容 = 红包 id）
    payload = CL.insert_chat(sender["id"], "redpacket", str(pid), None, None, "")
    execute_update("UPDATE red_packets SET msg_id = ? WHERE id = ?", (payload["id"], pid))
    payload["redpacketId"] = pid
    payload["redpacket"] = brief(pid, None)      # 中性简报：实时卡片直接可用（不含领取者视角）
    return {"ok": True, "id": pid, "payload": payload, "packet": brief(pid, sender["id"])}


def grab(packet_id: int, user: dict) -> dict:
    r = get(packet_id)
    if not r:
        return {"error": "红包不存在"}
    uid = user["id"]
    if uid == r["sender_id"]:
        return {"error": "不能领取自己发的红包"}
    g = query_one("SELECT amount FROM red_packet_grabs WHERE packet_id = ? AND user_id = ?", (packet_id, uid))
    if g:
        return {"ok": True, "already": True, "amount": int(g["amount"]), "packet": brief(packet_id, uid)}
    if _now() > int(r["expire_at"]):
        expire_due()
        return {"error": "红包已过期"}
    if int(r["grabbed_count"]) >= int(r["count"]):
        return {"error": "手慢了，红包已被领完"}

    remaining = int(r["total"]) - int(r["grabbed_amount"])
    left_cnt = int(r["count"]) - int(r["grabbed_count"])
    amount = int(r["per"]) if r["mode"] == "fixed" else _random_amount(remaining, left_cnt)
    amount = max(1, min(amount, remaining))
    try:
        execute("INSERT INTO red_packet_grabs (packet_id, user_id, amount, created_at) VALUES (?,?,?,?)",
                (packet_id, uid, amount, _now()))
    except Exception:
        # 并发重复领取：唯一索引兜底
        g2 = query_one("SELECT amount FROM red_packet_grabs WHERE packet_id = ? AND user_id = ?", (packet_id, uid))
        return {"ok": True, "already": True, "amount": int(g2["amount"]) if g2 else 0,
                "packet": brief(packet_id, uid)}
    execute_update("UPDATE red_packets SET grabbed_count = grabbed_count + 1, "
                   "grabbed_amount = grabbed_amount + ? WHERE id = ?", (amount, packet_id))

    sender = query_one("SELECT username, nickname FROM users WHERE id = ?", (r["sender_id"],))
    sname = ((sender["nickname"] or sender["username"]) if sender else "") or f"红包#{packet_id}"
    if r["asset"] == "points":
        add_points(uid, amount, "redpacket_grab", sname, "抢红包")
    else:
        levelcore.adjust(uid, amount, "redpacket", sname, "抢红包")
    return {"ok": True, "amount": amount, "packet": brief(packet_id, uid)}


def expire_due() -> int:
    """到期结算：未领完的剩余退回发包者（仅积分红包、且发包时确实扣过款）。"""
    now = _now()
    rows = query_all("SELECT * FROM red_packets WHERE refunded = 0 AND expire_at < ? AND grabbed_count < count",
                     (now,))
    n = 0
    for r in rows:
        left = int(r["total"]) - int(r["grabbed_amount"])
        if left > 0 and not int(r["admin_free"]) and r["asset"] == "points":
            add_points(r["sender_id"], left, "redpacket_refund", f"红包#{r['id']}", "红包过期退回")
        execute_update("UPDATE red_packets SET refunded = 1 WHERE id = ?", (r["id"],))
        n += 1
    # 已领完的也标记结算，避免反复扫描
    execute_update("UPDATE red_packets SET refunded = 1 WHERE refunded = 0 AND grabbed_count >= count", ())
    return n