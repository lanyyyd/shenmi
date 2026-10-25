"""等级 / 经验核心逻辑（与路由解耦，供 deps / 聊天 / 积分等复用）。

- 等级由经验总量按阈值推导，不单独存等级；
- 阈值、经验来源单位值、每日上限全部存 settings 的 level_config，后台可改；
- 老用户按折算规则一次性初始化（幂等，不会重复发放）。
"""
from __future__ import annotations

import json
from copy import deepcopy
from datetime import datetime, timedelta, timezone

from . import security
from .db import execute, execute_update, query_all, query_one

# 来源标识 → 中文名（经验明细 / 说明用）
SOURCE_NAMES = {
    "signin": "每日签到",
    "daily_login": "每日首登",
    "group_msg": "群聊发言",
    "pm_msg": "私聊发言",
    "upload": "发送文件",
    "redeem": "兑换商品",
    "transfer": "转账",
    "admin": "系统调整",
    "init": "老用户折算",
}

# 默认配置：阈值为 B 站等级经验 ÷10；来源单位经验沿用方案默认。
#   dailyCap      每日经验上限（0 = 不限）
#   dailyCountCap 每日次数上限（0 = 不限）
#   oncePerDay    每天最多发放一次
DEFAULT_CONFIG = {
    "enabled": True,
    "thresholds": [0, 10, 20, 150, 450, 1080, 2880],
    "sources": {
        "signin":      {"enabled": True, "exp": 10, "dailyCap": 0, "dailyCountCap": 0,
                        "streakDays": 7, "streakMultiplier": 1.5},
        "daily_login": {"enabled": True, "exp": 5,  "dailyCap": 0, "oncePerDay": True},
        "group_msg":   {"enabled": True, "exp": 2,  "dailyCap": 20},
        "pm_msg":      {"enabled": True, "exp": 1,  "dailyCap": 10},
        "upload":      {"enabled": True, "exp": 5,  "dailyCap": 10},
        "redeem":      {"enabled": True, "exp": 5,  "dailyCap": 0, "dailyCountCap": 3},
        "transfer":    {"enabled": True, "exp": 1,  "dailyCap": 5},
    },
}

_CFG_CACHE: dict = {"val": None, "at": 0}
_CFG_TTL = 5000  # 毫秒：短时间内复用，避免每条消息都查一次 settings


def bj_day() -> str:
    """北京时间（UTC+8）的日期，与签到口径一致。"""
    return datetime.now(timezone(timedelta(hours=8))).strftime("%Y-%m-%d")


def _get_setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key = ?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str) -> None:
    execute("INSERT INTO settings (key, value, updated_at) VALUES (?, ?, ?) "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
            (key, value, security.now_ms()))


# ---------------------------------------------------------------- 配置
def _sanitize_thresholds(raw) -> list | None:
    """阈值需为「非负、严格递增、首项 0、至少 2 项」的整数数组，否则返回 None。"""
    if not isinstance(raw, list) or len(raw) < 2:
        return None
    out: list[int] = []
    for v in raw:
        try:
            n = int(v)
        except Exception:
            return None
        if n < 0:
            return None
        out.append(n)
    out[0] = 0
    for i in range(1, len(out)):
        if out[i] <= out[i - 1]:
            return None
    return out


def _merge_source(dst: dict, src: dict) -> None:
    """把后台提交的来源配置并入默认值（忽略非法字段）。"""
    if not isinstance(src, dict):
        return
    if "enabled" in src:
        dst["enabled"] = bool(src["enabled"])
    for k in ("exp", "dailyCap", "dailyCountCap", "streakDays"):
        if k in src:
            try:
                dst[k] = max(int(src[k]), 0)
            except Exception:
                pass
    if "streakMultiplier" in src:
        try:
            dst["streakMultiplier"] = max(float(src["streakMultiplier"]), 0.0)
        except Exception:
            pass
    if "oncePerDay" in src:
        dst["oncePerDay"] = bool(src["oncePerDay"])


def _normalize_config(cfg) -> dict:
    out = deepcopy(DEFAULT_CONFIG)
    if not isinstance(cfg, dict):
        return out
    if "enabled" in cfg:
        out["enabled"] = bool(cfg["enabled"])
    th = _sanitize_thresholds(cfg.get("thresholds"))
    if th:
        out["thresholds"] = th
    srcs = cfg.get("sources")
    if isinstance(srcs, dict):
        for name, spec in srcs.items():
            base = out["sources"].get(name)
            if base is None:                      # 允许后台新增自定义来源（默认关闭）
                base = {"enabled": False, "exp": 0, "dailyCap": 0}
                out["sources"][name] = base
            _merge_source(base, spec)
    return out


def _build_config() -> dict:
    raw = _get_setting("level_config", "")
    if not raw:
        return deepcopy(DEFAULT_CONFIG)
    try:
        stored = json.loads(raw)
    except Exception:
        return deepcopy(DEFAULT_CONFIG)
    return _normalize_config(stored)


def load_config(force: bool = False) -> dict:
    now = security.now_ms()
    if not force and _CFG_CACHE["val"] is not None and (now - _CFG_CACHE["at"]) < _CFG_TTL:
        return _CFG_CACHE["val"]
    cfg = _build_config()
    _CFG_CACHE["val"] = cfg
    _CFG_CACHE["at"] = now
    return cfg


def save_config(cfg) -> dict:
    """规范化后落库，并刷新缓存；返回落库后的配置。"""
    norm = _normalize_config(cfg)
    _set_setting("level_config", json.dumps(norm, ensure_ascii=False))
    _CFG_CACHE["val"] = None
    return load_config(force=True)


# ---------------------------------------------------------------- 等级换算
def level_of(exp: int, thresholds: list | None = None) -> int:
    th = thresholds or load_config()["thresholds"]
    lv = 0
    for i, t in enumerate(th):
        if exp >= t:
            lv = i
    return lv


def level_info(exp: int, thresholds: list | None = None) -> dict:
    th = thresholds or load_config()["thresholds"]
    exp = max(int(exp or 0), 0)
    lv = level_of(exp, th)
    cur = th[lv]
    is_max = lv >= len(th) - 1
    nxt = None if is_max else th[lv + 1]
    if is_max:
        ratio, remain = 1.0, 0
    else:
        span = max(int(nxt) - int(cur), 1)
        ratio = min(max((exp - cur) / span, 0.0), 1.0)
        remain = max(int(nxt) - exp, 0)
    return {
        "exp": exp, "level": lv, "maxLevel": len(th) - 1,
        "curThreshold": int(cur), "nextThreshold": nxt, "remain": remain,
        "ratio": round(ratio, 4), "isMax": is_max,
    }


def exp_of(user_id: int) -> int:
    row = query_one("SELECT exp FROM user_exp WHERE user_id = ?", (user_id,))
    return int(row["exp"]) if row else 0


def profile_of(user_id: int, cfg: dict | None = None) -> dict:
    cfg = cfg or load_config()
    return level_info(exp_of(user_id), cfg["thresholds"])


def public_sources(cfg: dict | None = None) -> list:
    """给前端展示「经验怎么来」的规则列表。"""
    cfg = cfg or load_config()
    out = []
    for key, spec in cfg["sources"].items():
        out.append({
            "key": key, "name": SOURCE_NAMES.get(key, key),
            "enabled": bool(spec.get("enabled")),
            "exp": int(spec.get("exp") or 0),
            "dailyCap": int(spec.get("dailyCap") or 0),
            "dailyCountCap": int(spec.get("dailyCountCap") or 0),
            "oncePerDay": bool(spec.get("oncePerDay")),
            "streakDays": int(spec.get("streakDays") or 0),
            "streakMultiplier": float(spec.get("streakMultiplier") or 1),
        })
    return out


# ---------------------------------------------------------------- 经验写入
def _apply_delta(user_id: int, delta: int, source: str, ref: str = "", remark: str = "") -> tuple[int, int]:
    """落库一笔经验变动（负数会被夹到 0，不出现负经验）。返回 (新经验, 实际变动)。"""
    now = security.now_ms()
    old = exp_of(user_id)
    new = max(old + int(delta), 0)
    real = new - old
    if query_one("SELECT user_id FROM user_exp WHERE user_id = ?", (user_id,)):
        execute_update("UPDATE user_exp SET exp = ?, updated_at = ? WHERE user_id = ?", (new, now, user_id))
    else:
        execute("INSERT INTO user_exp (user_id, exp, updated_at) VALUES (?, ?, ?)", (user_id, new, now))
    execute("INSERT INTO exp_logs (user_id, delta, exp_after, source, ref, remark, created_at) "
            "VALUES (?,?,?,?,?,?,?)",
            (user_id, real, new, source or "", ref or "", remark or "", now))
    return new, real


def _daily_row(user_id: int, day: str, source: str):
    return query_one("SELECT gained, cnt FROM daily_exp_gain WHERE user_id = ? AND day = ? AND source = ?",
                     (user_id, day, source))


def _bump_daily(user_id: int, day: str, source: str, gained: int) -> None:
    execute("INSERT INTO daily_exp_gain (user_id, day, source, gained, cnt) VALUES (?,?,?,?,1) "
            "ON CONFLICT(user_id, day, source) DO UPDATE SET "
            "gained = gained + excluded.gained, cnt = cnt + 1",
            (user_id, day, source, gained))


def grant(user_id: int, source: str, ref: str = "", remark: str = "", multiplier: float = 1.0):
    """按来源自动发放经验：遵循总开关 / 来源开关 / 每日上限 / 每日一次。

    返回 dict（含 delta/exp/level）或 None（未发放，调用方无需特殊处理）。
    """
    if not user_id:
        return None
    cfg = load_config()
    if not cfg.get("enabled"):
        return None
    spec = cfg["sources"].get(source)
    if not spec or not spec.get("enabled"):
        return None
    base = int(spec.get("exp") or 0)
    if base <= 0:
        return None
    try:
        mult = float(multiplier or 1.0)
    except Exception:
        mult = 1.0
    amount = int(round(base * mult))
    if amount <= 0:
        return None

    day = bj_day()
    row = _daily_row(user_id, day, source)
    gained = int(row["gained"]) if row else 0
    cnt = int(row["cnt"]) if row else 0
    if spec.get("oncePerDay") and cnt > 0:
        return None
    if int(spec.get("dailyCountCap") or 0) > 0 and cnt >= int(spec["dailyCountCap"]):
        return None
    cap = int(spec.get("dailyCap") or 0)
    if cap > 0:
        remain = cap - gained
        if remain <= 0:
            return None
        amount = min(amount, remain)
    if amount <= 0:
        return None

    new_exp, real = _apply_delta(user_id, amount, source, ref, remark or SOURCE_NAMES.get(source, ""))
    if real <= 0:
        return None
    _bump_daily(user_id, day, source, real)
    info = level_info(new_exp, cfg["thresholds"])
    info.update({"delta": real, "source": source})
    return info


def adjust(user_id: int, delta: int, source: str = "admin", ref: str = "", remark: str = ""):
    """后台手工调整（可为负），不受来源开关与上限限制。返回 dict。"""
    cfg = load_config()
    new_exp, real = _apply_delta(user_id, int(delta), source, ref, remark or SOURCE_NAMES.get(source, ""))
    info = level_info(new_exp, cfg["thresholds"])
    info.update({"delta": real, "source": source})
    return info


# ---------------------------------------------------------------- 老用户折算初始化
def init_preview(only_uninitialized: bool = True) -> list:
    """折算规则：注册天数×2（≤300）+ 发言数（≤800）+ 签到次数×10 + 现有积分×1。

    只对「尚无经验记录」的用户计算，因此可重复执行且不会重复发放。
    """
    now = security.now_ms()
    users = query_all("SELECT id, username, nickname, created_at FROM users")
    inited = {r["user_id"] for r in query_all("SELECT user_id FROM user_exp")}
    msg_map = {r["from_id"]: int(r["c"]) for r in
               query_all("SELECT from_id, COUNT(*) AS c FROM messages GROUP BY from_id")}
    sign_map = {r["user_id"]: int(r["c"]) for r in
                query_all("SELECT user_id, COUNT(*) AS c FROM daily_signin GROUP BY user_id")}
    point_map = {r["user_id"]: int(r["balance"] or 0) for r in
                 query_all("SELECT user_id, balance FROM point_accounts")}
    out = []
    for u in users:
        uid = u["id"]
        if only_uninitialized and uid in inited:
            continue
        days = int((now - int(u["created_at"] or now)) / 86400000)
        if days < 0:
            days = 0
        reg_exp = min(days * 2, 300)
        msg_count = int(msg_map.get(uid, 0))
        msg_exp = min(msg_count, 800)
        signin_count = int(sign_map.get(uid, 0))
        signin_exp = signin_count * 10
        points = max(int(point_map.get(uid, 0)), 0)
        exp = reg_exp + msg_exp + signin_exp + points
        out.append({
            "userId": uid, "username": u["username"], "nickname": u["nickname"] or "",
            "regDays": days, "regExp": reg_exp,
            "msgCount": msg_count, "msgExp": msg_exp,
            "signinCount": signin_count, "signinExp": signin_exp,
            "points": points, "pointsExp": points, "exp": exp,
            "level": level_of(exp),
        })
    return out


def init_apply() -> dict:
    rows = init_preview(only_uninitialized=True)
    applied = 0
    now = security.now_ms()
    for r in rows:
        if r["exp"] > 0:
            _apply_delta(r["userId"], r["exp"], "init", "", "老用户折算初始化")
        else:
            # 折算为 0 的用户也登记一行，避免每次初始化都重复统计
            execute("INSERT OR IGNORE INTO user_exp (user_id, exp, updated_at) VALUES (?, 0, ?)",
                    (r["userId"], now))
        applied += 1
    if applied:
        _set_setting("level_init_at", str(security.now_ms()))
    return {"applied": applied, "users": rows}


def init_at() -> int:
    try:
        return int(_get_setting("level_init_at", "0") or 0)
    except Exception:
        return 0