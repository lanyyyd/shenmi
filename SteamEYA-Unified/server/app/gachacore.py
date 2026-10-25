"""抽卡核心逻辑（原神式保底）。

- 概率 / 保底参数存 settings.gacha_config（后台可改）；
- 卡池名字与价格存 gacha_pools 表（后台可改）；
- 保底按「用户 × 卡池」分别永久累计；
- 奖品按稀有度分档（普通/精品/稀有/限定五星），类型决定奖励内容（积分/经验/实物）。

概率模型（与需求图一致）：
    稀有档  p5(n) = 0.6%（n≤73） / 0.6%+6%*(n-73)（74≤n≤89） / 100%（n≥90）
    精品档  p4(n) = 5.1%（n<10） / 100%（n≥10）   —— 即「每十抽必出紫色以上」
    限定概率（50/50），歪一次后下次稀有必为限定（大保底）。
"""
from __future__ import annotations

import json
import random
from copy import deepcopy

from . import levelcore, security
from .db import execute, execute_update, query_all, query_one
from .wallet import add_points as _add_points, balance as _balance

RARITIES = ("normal", "premium", "rare", "limited")
RARITY_NAMES = {"normal": "普通", "premium": "精品", "rare": "稀有", "limited": "限定五星"}
KIND_NAMES = {"points": "积分", "exp": "经验", "physical": "实物"}

DEFAULT_RATES = {
    "enabled": True,
    "fiveStar": {"base": 0.006, "softStart": 74, "step": 0.06, "hard": 90},
    "fourStar": {"base": 0.051, "hard": 10},
    "limitedRate": 0.5,
}

_CFG_CACHE: dict = {"val": None, "at": 0}
_CFG_TTL = 5000


# ---------------------------------------------------------------- 配置存取
def _get_setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str) -> None:
    execute("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            (key, value, security.now_ms()))


def _num(v, default, lo=None, hi=None):
    try:
        x = float(v)
    except Exception:
        return default
    if x != x:                       # NaN
        return default
    if lo is not None and x < lo:
        x = lo
    if hi is not None and x > hi:
        x = hi
    return x


def _int(v, default, lo=None, hi=None):
    try:
        x = int(v)
    except Exception:
        return default
    if lo is not None and x < lo:
        x = lo
    if hi is not None and x > hi:
        x = hi
    return x


def _normalize_rates(raw) -> dict:
    out = deepcopy(DEFAULT_RATES)
    if not isinstance(raw, dict):
        return out
    if "enabled" in raw:
        out["enabled"] = bool(raw["enabled"])
    fs = raw.get("fiveStar") if isinstance(raw.get("fiveStar"), dict) else {}
    out["fiveStar"] = {
        "base": _num(fs.get("base"), DEFAULT_RATES["fiveStar"]["base"], 0.0, 1.0),
        "softStart": _int(fs.get("softStart"), DEFAULT_RATES["fiveStar"]["softStart"], 1, 100000),
        "step": _num(fs.get("step"), DEFAULT_RATES["fiveStar"]["step"], 0.0, 1.0),
        "hard": _int(fs.get("hard"), DEFAULT_RATES["fiveStar"]["hard"], 1, 100000),
    }
    if out["fiveStar"]["softStart"] > out["fiveStar"]["hard"]:
        out["fiveStar"]["softStart"] = out["fiveStar"]["hard"]
    f4 = raw.get("fourStar") if isinstance(raw.get("fourStar"), dict) else {}
    out["fourStar"] = {
        "base": _num(f4.get("base"), DEFAULT_RATES["fourStar"]["base"], 0.0, 1.0),
        "hard": _int(f4.get("hard"), DEFAULT_RATES["fourStar"]["hard"], 1, 100000),
    }
    out["limitedRate"] = _num(raw.get("limitedRate"), DEFAULT_RATES["limitedRate"], 0.0, 1.0)
    return out


def load_rates(force: bool = False) -> dict:
    now = security.now_ms()
    if not force and _CFG_CACHE["val"] is not None and (now - _CFG_CACHE["at"]) < _CFG_TTL:
        return _CFG_CACHE["val"]
    raw = _get_setting("gacha_config", "")
    try:
        stored = json.loads(raw) if raw else {}
    except Exception:
        stored = {}
    cfg = _normalize_rates(stored)
    _CFG_CACHE["val"] = cfg
    _CFG_CACHE["at"] = now
    return cfg


def save_rates(raw) -> dict:
    cfg = _normalize_rates(raw)
    _set_setting("gacha_config", json.dumps(cfg, ensure_ascii=False))
    _CFG_CACHE["val"] = None
    return load_rates(force=True)


# ---------------------------------------------------------------- 概率曲线
def p5(n: int, rates: dict | None = None) -> float:
    """第 n 抽（距上次稀有）出稀有的概率。"""
    r = (rates or load_rates())["fiveStar"]
    base, soft, step, hard = r["base"], r["softStart"], r["step"], r["hard"]
    if n >= hard:
        return 1.0
    if n >= soft:
        return min(1.0, base + step * (n - (soft - 1)))
    return float(base)


def p4(n: int, rates: dict | None = None) -> float:
    """第 n 抽（距上次精品以上）出精品以上的概率。"""
    r = (rates or load_rates())["fourStar"]
    if n >= r["hard"]:
        return 1.0
    return float(r["base"])


# ---------------------------------------------------------------- 卡池 / 奖品
def list_pools(active_only: bool = False) -> list:
    sql = "SELECT * FROM gacha_pools"
    if active_only:
        sql += " WHERE active = 1"
    sql += " ORDER BY sort ASC, id ASC"
    return query_all(sql)


def get_pool(pool_id: int):
    return query_one("SELECT * FROM gacha_pools WHERE id = ?", (pool_id,))


def pool_out(r) -> dict:
    return {"id": r["id"], "name": r["name"], "cost1": int(r["cost1"] or 0),
            "cost10": int(r["cost10"] or 0), "sort": int(r["sort"] or 0),
            "active": bool(r["active"])}


def prize_out(r) -> dict:
    kind = r["kind"] or ""
    rarity = r["rarity"] or "normal"
    return {
        "id": r["id"], "poolId": r["pool_id"], "kind": kind,
        "kindName": KIND_NAMES.get(kind, kind), "rarity": rarity,
        "rarityName": RARITY_NAMES.get(rarity, rarity), "name": r["name"] or "",
        "description": r["description"] or "", "image": r["image_path"] or "",
        "value": int(r["value"] or 0), "stock": int(r["stock"] if r["stock"] is not None else -1),
        "weight": int(r["weight"] or 1), "active": bool(r["active"]),
    }


def prize_label(p) -> str:
    """奖品展示名：积分/经验显示数量，实物显示名称。"""
    kind = p["kind"] if isinstance(p, dict) else p["kind"]
    if kind == "points":
        return f'{int((p["value"] if isinstance(p, dict) else p["value"]) or 0)} 积分'
    if kind == "exp":
        return f'{int((p["value"] if isinstance(p, dict) else p["value"]) or 0)} 经验'
    nm = (p["name"] if isinstance(p, dict) else p["name"]) or ""
    return nm or "实物奖品"


def pool_prizes(pool_id: int, include_inactive: bool = False) -> list:
    sql = "SELECT * FROM gacha_prizes WHERE pool_id = ?"
    if not include_inactive:
        sql += " AND active = 1"
    sql += " ORDER BY id ASC"
    return [prize_out(r) for r in query_all(sql, (pool_id,))]


def _pick(pool_id: int, rarity: str):
    """从某档位里按权重随机取一个可用奖品（库存为 0 的实物不参与）。"""
    rows = query_all("SELECT * FROM gacha_prizes WHERE pool_id=? AND rarity=? AND active=1 AND stock<>0",
                     (pool_id, rarity))
    if not rows:
        return None
    weights = [max(int(r["weight"] or 1), 1) for r in rows]
    x = random.uniform(0, sum(weights))
    acc = 0.0
    for r, w in zip(rows, weights):
        acc += w
        if x <= acc:
            return r
    return rows[-1]


def _pick_any(pool_id: int, order) -> object:
    for rarity in order:
        p = _pick(pool_id, rarity)
        if p is not None:
            return p
    return None


# ---------------------------------------------------------------- 状态
def get_state(user_id: int, pool_id: int) -> dict:
    r = query_one("SELECT * FROM gacha_state WHERE user_id=? AND pool_id=?", (user_id, pool_id))
    if not r:
        return {"pity5": 0, "pity4": 0, "guaranteed": 0,
                "total_draws": 0, "rare_total": 0, "limited_total": 0}
    return {"pity5": int(r["pity5"]), "pity4": int(r["pity4"]), "guaranteed": int(r["guaranteed"]),
            "total_draws": int(r["total_draws"]), "rare_total": int(r["rare_total"]),
            "limited_total": int(r["limited_total"])}


def _save_state(user_id: int, pool_id: int, s: dict) -> None:
    execute("INSERT INTO gacha_state (user_id,pool_id,pity5,pity4,guaranteed,total_draws,rare_total,limited_total,updated_at) "
            "VALUES (?,?,?,?,?,?,?,?,?) "
            "ON CONFLICT(user_id,pool_id) DO UPDATE SET pity5=excluded.pity5, pity4=excluded.pity4, "
            "guaranteed=excluded.guaranteed, total_draws=excluded.total_draws, rare_total=excluded.rare_total, "
            "limited_total=excluded.limited_total, updated_at=excluded.updated_at",
            (user_id, pool_id, s["pity5"], s["pity4"], s["guaranteed"], s["total_draws"],
             s["rare_total"], s["limited_total"], security.now_ms()))


def state_out(s: dict, rates: dict | None = None) -> dict:
    r = rates or load_rates()
    hard = int(r["fiveStar"]["hard"])
    return {
        "pity5": s["pity5"], "pity4": s["pity4"], "guaranteed": bool(s["guaranteed"]),
        "totalDraws": s["total_draws"], "rareTotal": s["rare_total"], "limitedTotal": s["limited_total"],
        "hard": hard, "softStart": int(r["fiveStar"]["softStart"]),
        "toPity": max(hard - s["pity5"], 0),
    }


# ---------------------------------------------------------------- 奖励发放
def _create_physical_order(user_id: int, prize) -> int:
    """实物奖品 → 生成一条待兑现订单，复用「我的订单」/后台兑现流程。"""
    return execute(
        "INSERT INTO point_redemptions (user_id, item_id, item_name, price, status, created_at, source) "
        "VALUES (?,?,?,0,'redeemed',?,'gacha')",
        (user_id, prize["id"], prize["name"] or "实物奖品", security.now_ms()))


def _grant(user_id: int, prize) -> None:
    kind = prize["kind"]
    value = int(prize["value"] or 0)
    name = prize["name"] or KIND_NAMES.get(kind, "奖品")
    if kind == "points" and value > 0:
        _add_points(user_id, value, "gacha", name[:60], "抽卡奖励")
    elif kind == "exp" and value > 0:
        levelcore.adjust(user_id, value, "gacha", name[:60], "抽卡奖励")
    elif kind == "physical":
        if int(prize["stock"] if prize["stock"] is not None else -1) > 0:
            execute_update("UPDATE gacha_prizes SET stock = stock - 1 WHERE id = ? AND stock > 0", (prize["id"],))
        _create_physical_order(user_id, prize)


# ---------------------------------------------------------------- 抽卡
def _draw_one(user_id: int, pool_id: int, st: dict, rates: dict) -> dict:
    st["pity5"] += 1
    st["pity4"] += 1
    st["total_draws"] += 1
    guaranteed_before = 1 if st["guaranteed"] else 0

    if random.random() < p5(st["pity5"], rates):
        tier = "rare"
    elif random.random() < p4(st["pity4"], rates):
        tier = "premium"
    else:
        tier = "normal"

    pull_no = 0
    prize = None
    if tier == "rare":
        pull_no = st["pity5"]
        # 50/50 与大保底：决定本次五星是「限定」还是「歪到非限定」
        want_limited = bool(st["guaranteed"]) or (random.random() < float(rates["limitedRate"]))
        if want_limited:
            prize = _pick_any(pool_id, ("limited", "rare", "premium", "normal"))
        else:
            prize = _pick_any(pool_id, ("rare", "limited", "premium", "normal"))
        st["pity5"] = 0
        st["pity4"] = 0
        st["rare_total"] += 1
        if prize is not None:
            if prize["rarity"] == "limited":
                st["limited_total"] += 1
                st["guaranteed"] = 0
            else:
                st["guaranteed"] = 1
    elif tier == "premium":
        prize = _pick_any(pool_id, ("premium", "normal"))
        st["pity4"] = 0
    else:
        prize = _pick_any(pool_id, ("normal", "premium"))

    if prize is None:
        # 空卡池兜底：不改变保底之外的状态，返回一条占位结果
        return {"prizeId": None, "kind": "", "kindName": "", "rarity": "normal",
                "rarityName": "普通", "name": "卡池暂无奖品", "label": "卡池暂无奖品",
                "value": 0, "image": "", "isLimited": False, "pullNo": pull_no,
                "guaranteedBefore": bool(guaranteed_before), "tier": tier}

    _grant(user_id, prize)
    is_limited = (prize["rarity"] == "limited")
    out = {
        "prizeId": prize["id"], "kind": prize["kind"], "kindName": KIND_NAMES.get(prize["kind"], ""),
        "rarity": prize["rarity"], "rarityName": RARITY_NAMES.get(prize["rarity"], prize["rarity"]),
        "name": prize["name"] or "", "label": prize_label(prize), "value": int(prize["value"] or 0),
        "image": prize["image_path"] or "", "isLimited": is_limited, "pullNo": pull_no,
        "guaranteedBefore": bool(guaranteed_before), "tier": tier,
    }
    log_id = execute(
        "INSERT INTO gacha_logs (user_id,pool_id,prize_id,kind,rarity,name,value,image_path,is_limited,"
        "pity5,pull_no,guaranteed_before,created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (user_id, pool_id, prize["id"], prize["kind"], prize["rarity"], prize["name"] or "",
         int(prize["value"] or 0), prize["image_path"] or "", 1 if is_limited else 0,
         st["pity5"], pull_no, guaranteed_before, security.now_ms()))
    out["logId"] = log_id
    return out


def draw(user_id: int, pool_id: int, times: int = 1) -> dict:
    """抽卡入口：times = 1 或 10。返回 {ok, results, balance, state, pool, cost} 或 {error}。"""
    pool = get_pool(pool_id)
    if not pool or not pool["active"]:
        return {"error": "卡池不存在或已停用"}
    rates = load_rates()
    if not rates.get("enabled"):
        return {"error": "抽卡功能暂未开启"}
    times = 10 if int(times) == 10 else 1
    # 空卡池保护：没有任何可用奖品时直接拒绝，避免扣了积分却抽不出东西
    avail = query_one("SELECT COUNT(*) AS c FROM gacha_prizes WHERE pool_id=? AND active=1 AND stock<>0",
                      (pool_id,))["c"]
    if not avail:
        return {"error": "该卡池尚未配置奖品，请先在后台添加"}
    cost = int((pool["cost10"] if times == 10 else pool["cost1"]) or 0)
    bal = _balance(user_id)
    if cost > 0 and bal < cost:
        return {"error": "积分不足", "balance": bal, "need": cost}
    if cost > 0:
        _add_points(user_id, -cost, "gacha", pool["name"], "抽卡消耗")
    st = get_state(user_id, pool_id)
    results = [_draw_one(user_id, pool_id, st, rates) for _ in range(times)]
    _save_state(user_id, pool_id, st)
    return {"ok": True, "results": results, "balance": _balance(user_id),
            "state": state_out(st, rates), "pool": pool_out(pool), "cost": cost, "times": times}