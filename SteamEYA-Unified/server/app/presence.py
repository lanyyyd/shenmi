"""在线状态扩展：用户现在在玩什么。

云游戏：
    上号时客户端只会留下一条 cloud_login_logs，没有「退出」事件，所以用剩余免费时长
    做反向探测——上号后每隔 10~30 分钟随机查一次 wallet，时长在减少就说明还在玩；
    连续两次没有变化就判定已退出。探测失败（网络/风控）不判定退出，交给兜底 TTL。

Steam：
    服务端本来就存了 steam_api_key，按「在线用户当前占用的 Steam 账号」批量查
    GetPlayerSummaries，personastate 在线即显示「在玩 Steam」。

结果给 /ws 的 online 快照和 /api/chat/online 使用，客户端与网页无需改动协议。
"""
from __future__ import annotations

import logging
import random
import threading
import time

import requests

from .db import query_all, query_one

log = logging.getLogger("presence")

# 云游戏没有退出信号，先给一个较长的兜底有效期，正常情况由探测提前结束
CLOUD_TTL = 3 * 3600
# 探测间隔：上号后第一次、以及每次探测后，都在 10~30 分钟内随机
PROBE_MIN = 10 * 60
PROBE_MAX = 30 * 60
# Steam 状态缓存与刷新间隔
STEAM_TTL = 180
STEAM_REFRESH = 60

STEAM_API = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/"

_lock = threading.Lock()
# user_id -> {game, uid, name, until, last, next, miss}
_sessions: dict[int, dict] = {}
# user_id -> (文案, 过期时间)，目前只放 Steam
_activity: dict[int, tuple[str, float]] = {}
_started = False


def _game_name(game: str) -> str:
    # 只探测米哈游两个内置云游戏，固定文案即可，不必依赖 cloud.py
    return {"genshin": "原神", "starrail": "星穹铁道"}.get(game, game)


def note_cloud_login(user_id: int, game: str, uid: str = "") -> None:
    """客户端上号时调用：开始/延长一条「在玩云游戏」会话。"""
    if game not in ("genshin", "starrail"):
        return
    if uid:
        row = query_one(
            "SELECT game_uid, combo_token, free_duration, checked_at FROM cloud_cookies "
            "WHERE user_id=? AND game=? AND game_uid=?", (user_id, game, uid))
    else:
        row = query_one(
            "SELECT game_uid, combo_token, free_duration, checked_at FROM cloud_cookies "
            "WHERE user_id=? AND game=? ORDER BY updated_at DESC, id DESC LIMIT 1", (user_id, game))
    if not row or not row["combo_token"]:
        return
    now = time.time()
    # 上传/检测时记录的时长如果很新，直接当基线，第一次探测就能判断有没有在消耗
    base = None
    if row["checked_at"] and now - row["checked_at"] <= 1800 and row["free_duration"] is not None:
        base = int(row["free_duration"])
    with _lock:
        cur = _sessions.get(user_id)
        if cur and cur["game"] == game and cur["uid"] == row["game_uid"]:
            cur["until"] = now + CLOUD_TTL
            return
        _sessions[user_id] = {
            "game": game, "uid": row["game_uid"] or "", "name": _game_name(game),
            "until": now + CLOUD_TTL, "last": base, "miss": 0,
            "next": now + random.randint(PROBE_MIN, PROBE_MAX),
        }


def activity_of(user_id: int) -> str:
    """返回「在玩 xxx」，没有在玩时返回空串（前端保持原来的在线/设备显示）。"""
    now = time.time()
    with _lock:
        s = _sessions.get(user_id)
        if s and s["until"] > now:
            return f"在玩{s['name']}"
        a = _activity.get(user_id)
        if a and a[1] > now:
            return a[0]
    return ""


def _probe_cloud(now: float) -> None:
    from .routers.cloud import _get_mihoyo_free_duration

    due = []
    with _lock:
        for uid, s in list(_sessions.items()):
            if s["until"] <= now:
                _sessions.pop(uid, None)
            elif s["next"] <= now:
                due.append((uid, dict(s)))

    for uid, snap in due:
        row = query_one(
            "SELECT combo_token FROM cloud_cookies WHERE user_id=? AND game=? AND game_uid=?",
            (uid, snap["game"], snap["uid"]),
        )
        token = (row or {}).get("combo_token") or ""
        free = None
        if token:
            wr = _get_mihoyo_free_duration(token, snap["game"])
            if wr.get("expired"):
                # 凭证都失效了，不可能还在云游戏里
                with _lock:
                    _sessions.pop(uid, None)
                continue
            if wr.get("ok") and wr.get("free_duration") is not None:
                free = int(wr["free_duration"])

        with _lock:
            cur = _sessions.get(uid)
            if not cur or cur["uid"] != snap["uid"]:
                continue
            if free is not None:
                if cur["last"] is None:
                    cur["last"] = free
                    cur["miss"] = 0
                elif free < cur["last"]:
                    # 时长在减少 → 还在玩，续期
                    cur["last"] = free
                    cur["miss"] = 0
                    cur["until"] = time.time() + CLOUD_TTL
                else:
                    cur["last"] = free
                    cur["miss"] += 1
                    if cur["miss"] >= 2:
                        _sessions.pop(uid, None)
                        continue
            cur["next"] = time.time() + random.randint(PROBE_MIN, PROBE_MAX)


def _refresh_steam() -> None:
    from .routers.steam import _get_setting

    api_key = _get_setting("steam_api_key")
    if not api_key:
        with _lock:
            _activity.clear()
        return

    online = query_all(
        "SELECT u.id AS uid, u.username AS username FROM online o "
        "JOIN users u ON u.id = o.user_id GROUP BY u.id"
    )
    assigned = query_all(
        "SELECT assigned_to, steam_id FROM accounts "
        "WHERE status='in_use' AND assigned_to<>'' AND steam_id<>''"
    )
    by_name: dict[str, set] = {}
    for a in assigned:
        by_name.setdefault(a["assigned_to"], set()).add(str(a["steam_id"]))

    mapping: dict[str, int] = {}
    for u in online:
        for sid in by_name.get(u["username"], ()):
            mapping[sid] = u["uid"]

    now = time.time()
    if not api_key or not mapping:
        with _lock:
            _activity.clear()
        return

    states: dict[str, dict] = {}
    ids = list(mapping)
    for i in range(0, len(ids), 100):
        chunk = ids[i:i + 100]
        try:
            resp = requests.get(STEAM_API, params={"key": api_key, "steamids": ",".join(chunk)}, timeout=10)
            data = resp.json()
        except Exception as e:
            log.debug("Steam 状态查询失败：%s", e)
            continue
        for p in ((data.get("response") or {}).get("players") or []):
            states[str(p.get("steamid"))] = p

    with _lock:
        for sid, uid in mapping.items():
            p = states.get(sid) or {}
            online_now = int(p.get("personastate") or 0) > 0 or bool(p.get("gameid"))
            if online_now:
                _activity[uid] = ("在玩 Steam", now + STEAM_TTL)
            else:
                _activity.pop(uid, None)


def _loop() -> None:
    while True:
        try:
            _probe_cloud(time.time())
        except Exception as e:
            log.warning("云游戏在玩探测失败：%s", e)
        try:
            _refresh_steam()
        except Exception as e:
            log.warning("Steam 在玩状态刷新失败：%s", e)
        time.sleep(STEAM_REFRESH)


def start_background() -> None:
    global _started
    if _started:
        return
    _started = True
    threading.Thread(target=_loop, name="presence", daemon=True).start()
