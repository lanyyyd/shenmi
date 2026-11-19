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

# 旧版客户端没有退出信号，先给一个较长的兜底有效期，正常情况由探测提前结束
CLOUD_TTL = 3 * 3600
# 新版客户端每 60 秒上报一次心跳；90 秒没续上就认为已退出（崩溃/被杀也最多拖 90 秒）
CLIENT_TTL = 90
# 探测间隔：上号后第一次、以及每次探测后，都在 10~30 分钟内随机
PROBE_MIN = 10 * 60
PROBE_MAX = 30 * 60
# Steam 状态缓存与刷新间隔
STEAM_TTL = 180
STEAM_REFRESH = 60

STEAM_API = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/"

_lock = threading.Lock()
# user_id -> {game, uid, name, until, last, next, miss, client}
_sessions: dict[int, dict] = {}
# user_id -> (文案, 过期时间)，放 Steam 和客户端自行上报的状态
_activity: dict[int, tuple[str, float]] = {}
# 自行上报 Steam 的用户：以客户端为准，不让服务端 Steam API 覆盖
_client_steam: set[int] = set()
_started = False


def _game_name(game: str) -> str:
    fixed = {"genshin": "原神", "starrail": "星穹铁道", "steam": "Steam"}.get(game)
    if fixed:
        return fixed
    # 自定义游戏取后台配置的显示名
    row = query_one("SELECT name FROM cloud_games WHERE game_key=?", (game,))
    return (row or {}).get("name") or game


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
        # 新版客户端已经用心跳接管了状态，旧接口不要再覆盖成 3 小时探测会话
        if cur and cur.get("client") and cur["until"] > now:
            return
        if cur and cur["game"] == game and cur["uid"] == row["game_uid"]:
            cur["until"] = now + CLOUD_TTL
            return
        _sessions[user_id] = {
            "game": game, "uid": row["game_uid"] or "", "name": _game_name(game),
            "until": now + CLOUD_TTL, "last": base, "miss": 0, "client": False,
            "next": now + random.randint(PROBE_MIN, PROBE_MAX),
        }


def note_playing(user_id: int, game: str, uid: str = "") -> None:
    """新版客户端上号/心跳：开始或续期一条 90 秒的实时「在玩」会话。"""
    if not game:
        return
    row = None
    if uid:
        row = query_one(
            "SELECT game_uid FROM cloud_cookies WHERE user_id=? AND game=? AND game_uid=?",
            (user_id, game, uid))
    if not row:
        row = query_one(
            "SELECT game_uid FROM cloud_cookies WHERE user_id=? AND game=? "
            "ORDER BY updated_at DESC, id DESC LIMIT 1", (user_id, game))
    now = time.time()
    name = _game_name(game)
    with _lock:
        _activity.pop(user_id, None)
        _sessions[user_id] = {
            "game": game, "uid": (row or {}).get("game_uid") or uid,
            "name": name, "until": now + CLIENT_TTL,
            "last": None, "miss": 0, "client": True,
            "next": now + CLIENT_TTL,
        }


def note_stopped(user_id: int, game: str = "") -> None:
    """客户端退出游戏：立刻清掉「在玩」，不用等 TTL。"""
    with _lock:
        cur = _sessions.get(user_id)
        if cur and (not game or cur["game"] == game):
            _sessions.pop(user_id, None)
        if not game or game == "steam":
            _client_steam.discard(user_id)
            _activity.pop(user_id, None)


def note_steam(user_id: int, running: bool) -> None:
    """客户端 Steam 进程心跳：运行中续 90 秒，退出立刻清掉。"""
    now = time.time()
    with _lock:
        if running:
            _client_steam.add(user_id)
            _activity[user_id] = ("在玩 Steam", now + CLIENT_TTL)
        else:
            _client_steam.discard(user_id)
            _activity.pop(user_id, None)


def activity_snapshot() -> dict[int, str]:
    """当前所有用户的「在玩」文案，用来判断状态有没有变化、需不需要广播。"""
    now = time.time()
    with _lock:
        out: dict[int, str] = {}
        for uid, s in _sessions.items():
            if s["until"] > now:
                out[uid] = f"在玩{s['name']}"
        for uid, (text, exp) in _activity.items():
            if exp > now and uid not in out:
                out[uid] = text
        return out


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
            elif s.get("client"):
                # 客户端心跳会话由 TTL 负责，不再探测剩余时长
                continue
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
            # 保留客户端进程心跳上报的 Steam 状态，只清服务端自己探测出来的旧值
            for uid in list(_activity):
                if uid not in _client_steam:
                    _activity.pop(uid, None)
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
    if not mapping:
        with _lock:
            for uid in list(_activity):
                if uid not in _client_steam:
                    _activity.pop(uid, None)
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
            # 客户端心跳还新鲜时以客户端为准；过期后再让服务端 API 兜底
            client_alive = uid in _client_steam and (_activity.get(uid) or ("", 0))[1] > now
            if client_alive:
                continue
            p = states.get(sid) or {}
            online_now = int(p.get("personastate") or 0) > 0 or bool(p.get("gameid"))
            if online_now:
                _activity[uid] = ("在玩 Steam", now + STEAM_TTL)
            else:
                _activity.pop(uid, None)


def _loop() -> None:
    from .hub import hub

    while True:
        before = activity_snapshot()
        try:
            _probe_cloud(time.time())
        except Exception as e:
            log.warning("云游戏在玩探测失败：%s", e)
        try:
            _refresh_steam()
        except Exception as e:
            log.warning("Steam 在玩状态刷新失败：%s", e)
        # 状态变化主动推给聊天室，否则要等下次有人上下线才会刷新
        if activity_snapshot() != before:
            hub.broadcast_online_threadsafe()
        time.sleep(STEAM_REFRESH)


def start_background() -> None:
    global _started
    if _started:
        return
    _started = True
    threading.Thread(target=_loop, name="presence", daemon=True).start()
