"""米哈游云游戏 Cookie 自动续期。

机制与 MiyoQian 一致：登录时保存长效 stoken，之后每次用 stoken 换新的
ltoken / cookie_token（passport 的 getLTokenBySToken /
getCookieAccountInfoBySToken，带 DS 签名）。只要 stoken 没被注销，注入浏览器
的 Cookie 就一直是新鲜的，不需要用户重新扫码。

非米哈游站点（异环 / 哔哩哔哩）是会话型 Cookie，没有可换票的接口，这里直接跳过。
"""
from __future__ import annotations

import hashlib
import logging
import os
import random
import threading
import time

import requests

from . import security
from .db import execute, query_all, query_one

log = logging.getLogger("cloud-refresh")

PASSPORT_API = "https://passport-api.mihoyo.com"
LTOKEN_URL = f"{PASSPORT_API}/account/auth/api/getLTokenBySToken"
COOKIE_TOKEN_URL = f"{PASSPORT_API}/account/auth/api/getCookieAccountInfoBySToken"
PASSPORT_X4_SALT = "xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs"
BBS_VERSION = "2.106.2"
UA = f"Mozilla/5.0 (Windows NT 10.0; Win64; x64) miHoYoBBS/{BBS_VERSION}"

# 只有这两个内置游戏走米哈游账号体系；自定义游戏一律视为会话型。
MIHOYO_GAMES = ("genshin", "starrail")

# passport 表示「stoken 已注销/登录态失效」的返回码，这种 stoken 没必要再试。
DEAD_STOKEN_CODES = (-100, -101, 10001)

# 上号前懒续期：超过 12 小时没换过票就先续一次
LAZY_REFRESH_SECONDS = 12 * 3600
# 后台巡检：凌晨那一轮会把超过 24 小时没续期的账号全部换一遍
BACKGROUND_REFRESH_SECONDS = 24 * 3600
# 续期失败后至少隔 6 小时再试，避免反复请求触发风控
FAIL_BACKOFF_SECONDS = 6 * 3600
# 后台巡检间隔
SWEEP_SECONDS = 1800

_lock = threading.Lock()


def _md5(text: str) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def ds_x4(query: str = "", body: str = "") -> str:
    """passport 的 DS 签名：t,r,md5(salt&t&r&b&q)。"""
    t = str(int(time.time()))
    r = str(random.randint(100000, 200000))
    sign = _md5(f"salt={PASSPORT_X4_SALT}&t={t}&r={r}&b={body}&q={query}")
    return f"{t},{r},{sign}"


def device_id_for(user_id: int, game_uid: str) -> str:
    """每个账号固定一个设备号；随机设备号容易触发风控。"""
    return hashlib.sha256(f"teatt-cloud:{user_id}:{game_uid}".encode("utf-8")).hexdigest()[:32]


def credentials_from(cookies: list) -> tuple[str, str, str]:
    """从 Cookie 数组里提取 (stoken, mid, uid)。"""
    cmap = {c.get("name"): c.get("value") for c in cookies if c.get("name")}
    stoken = str(cmap.get("stoken") or "")
    mid = str(cmap.get("mid") or cmap.get("account_mid_v2") or cmap.get("ltmid_v2") or "")
    uid = str(cmap.get("stuid") or cmap.get("account_id") or cmap.get("ltuid")
              or cmap.get("ltuid_v2") or cmap.get("account_id_v2") or "")
    return stoken, mid, uid


def stoken_record(user_id: int, mid: str = "", uid: str = "") -> dict | None:
    """按 mid / stuid 查用户扫码存下的 stoken；Cookie 里没有 stoken 时用它兜底。"""
    mid = str(mid or "").strip()
    uid = str(uid or "").strip()
    if not mid and not uid:
        return None
    # 优先按 mid 精确匹配；Cookie 没有 mid 时才退回 stuid，避免把 A 账号的
    # stoken 拼到 B 账号的 Cookie 上。
    if mid:
        row = query_one(
            "SELECT mid, stuid, stoken, device_id FROM cloud_stokens "
            "WHERE user_id=? AND mid=? ORDER BY updated_at DESC LIMIT 1",
            (user_id, mid),
        )
    else:
        row = query_one(
            "SELECT mid, stuid, stoken, device_id FROM cloud_stokens "
            "WHERE user_id=? AND stuid=? AND stuid<>'' ORDER BY updated_at DESC LIMIT 1",
            (user_id, uid),
        )
    if not row:
        return None
    try:
        stoken = security.decrypt_blob(row["stoken"])
    except Exception:
        return None
    if not stoken:
        return None
    return {"stoken": str(stoken), "mid": row["mid"] or "", "stuid": row["stuid"] or "",
            "device_id": row["device_id"] or ""}


def save_stoken(user_id: int, stoken: str, mid: str, stuid: str = "", device_id: str = "") -> None:
    """保存米游社扫码拿到的 stoken（加密入库，同一 mid 覆盖）。"""
    mid = str(mid or "").strip()
    if not mid or not stoken:
        return
    now = security.now_s()
    execute(
        "INSERT INTO cloud_stokens (user_id, mid, stuid, stoken, device_id, created_at, updated_at) "
        "VALUES (?,?,?,?,?,?,?) "
        "ON CONFLICT(user_id, mid) DO UPDATE SET stuid=excluded.stuid, stoken=excluded.stoken, "
        "device_id=excluded.device_id, updated_at=excluded.updated_at",
        (user_id, mid, str(stuid or ""), security.encrypt_blob(str(stoken)),
         str(device_id or ""), now, now),
    )


def stoken_bound(user_id: int, cookies: list) -> bool:
    """这套 Cookie 是否能用长期 stoken 续期（自身带 stoken，或扫码存过）。"""
    stoken, mid, uid = credentials_from(cookies)
    if stoken:
        return True
    return stoken_record(user_id, mid, uid) is not None


def merge_stoken(cookies: list, user_id: int) -> tuple[list, str]:
    """Cookie 里缺 stoken 时，把扫码存下的补进去。

    返回 (新 Cookie 数组, 扫码时用的设备号)；设备号必须与拿 stoken 时一致，
    否则换票容易触发风控。
    """
    stoken, mid, uid = credentials_from(cookies)
    if stoken:
        return cookies, ""
    rec = stoken_record(user_id, mid, uid)
    if not rec:
        return cookies, ""

    new_cookies = [dict(c) for c in cookies]
    names = {c.get("name") for c in new_cookies}
    stoken_mid = rec.get("mid") or ""
    stoken_uid = rec.get("stuid") or ""
    if "stoken" not in names:
        new_cookies.append({"name": "stoken", "value": rec["stoken"],
                            "domain": ".mihoyo.com", "path": "/"})
    if "mid" not in names and not mid and stoken_mid:
        new_cookies.append({"name": "mid", "value": stoken_mid,
                            "domain": ".mihoyo.com", "path": "/"})
    if "stuid" not in names and not uid and stoken_uid:
        new_cookies.append({"name": "stuid", "value": stoken_uid,
                            "domain": ".mihoyo.com", "path": "/"})
    return new_cookies, rec.get("device_id") or ""


def _headers(stoken: str, mid: str, device_id: str, client_type: str) -> dict:
    headers = {
        "user-agent": UA,
        "x-rpc-app_version": BBS_VERSION,
        "x-rpc-client_type": client_type,
        "x-requested-with": "com.mihoyo.hyperion",
        "referer": "https://webstatic.mihoyo.com",
        "x-rpc-device_id": device_id,
        "ds": ds_x4(query=f"stoken={stoken}"),
        "cookie": f"mid={mid};stoken={stoken}",
        "accept": "application/json, text/plain, */*",
    }
    if client_type == "2":
        headers["x-rpc-aigis"] = ""
    return headers


def _exchange(url: str, stoken: str, mid: str, device_id: str, client_type: str) -> dict:
    """调用一次 passport 换票接口，返回 {"ok", "value", "msg"}。"""
    try:
        resp = requests.get(url, params={"stoken": stoken},
                            headers=_headers(stoken, mid, device_id, client_type), timeout=12)
        data = resp.json()
    except Exception as e:
        return {"ok": False, "msg": f"请求失败：{e}"}
    if not isinstance(data, dict):
        return {"ok": False, "msg": "接口返回格式异常"}
    if data.get("retcode") != 0:
        return {"ok": False, "retcode": data.get("retcode"),
                "msg": f"{data.get('message') or '续期失败'}（retcode={data.get('retcode')}）"}
    payload = data.get("data") or {}
    return {"ok": True, "value": payload, "msg": ""}


def _apply(cookies: list, ltoken: str, cookie_token: str) -> tuple[list, list]:
    """把新 token 写回 Cookie 数组（兼容 v1/v2 字段名），返回 (新数组, 更新字段)。"""
    new_cookies: list = []
    updated: list = []
    for c in cookies:
        item = dict(c)
        name = item.get("name")
        if name in ("ltoken", "ltoken_v2"):
            item["value"] = ltoken
            updated.append(name)
        elif name in ("cookie_token", "cookie_token_v2"):
            item["value"] = cookie_token
            updated.append(name)
        new_cookies.append(item)

    existing = {c.get("name") for c in new_cookies}
    if "ltoken" not in existing:
        new_cookies.append({"name": "ltoken", "value": ltoken, "domain": ".mihoyo.com", "path": "/"})
        updated.append("ltoken")
    if "cookie_token" not in existing:
        new_cookies.append({"name": "cookie_token", "value": cookie_token,
                            "domain": ".mihoyo.com", "path": "/"})
        updated.append("cookie_token")
    return new_cookies, sorted(set(updated))


def _without_stoken(cookies: list) -> list:
    """摘掉已失效的 stoken（保留 mid/stuid，方便之后按账号补新的）。"""
    return [dict(c) for c in cookies if str(c.get("name") or "") != "stoken"]


def _with_stoken(cookies: list, stoken: str, rec: dict | None) -> list:
    """把 Cookie 里的 stoken 换成指定的那个（顺带补 mid），返回新的 Cookie 数组。"""
    new_cookies: list = []
    names: set = set()
    for c in cookies:
        item = dict(c)
        name = item.get("name")
        names.add(name)
        if name == "stoken":
            item["value"] = stoken
        new_cookies.append(item)
    if "stoken" not in names:
        new_cookies.append({"name": "stoken", "value": stoken, "domain": ".mihoyo.com", "path": "/"})
    mid = str((rec or {}).get("mid") or "")
    if mid and "mid" not in names:
        new_cookies.append({"name": "mid", "value": mid, "domain": ".mihoyo.com", "path": "/"})
    return new_cookies


def refresh_cookies(cookies: list, user_id: int, game_uid: str = "", device_id: str = "") -> dict:
    """用 stoken 换一套新的 ltoken / cookie_token。

    返回 {"ok": bool, "cookies": [...], "msg": str, "updated": [字段名], "need_stoken": bool}。
    失败时仍返回原 Cookie，调用方可以继续用旧值（至少还能尝试登录）。

    Cookie 里自带的 stoken 可能是浏览器 profile 残留的旧号/已注销账号（换票直接 -100）。
    所以优先用扫码存下、按 mid 绑定的 stoken，失败再退回 Cookie 自带的那份。
    """
    stoken, mid, uid = credentials_from(cookies)
    # 扫码拿到的 stoken 必须沿用扫码时的设备号，否则换票会被风控。
    default_device = device_id or device_id_for(user_id, game_uid)
    rec = stoken_record(user_id, mid, uid) if (mid or uid) else None

    attempts: list[tuple[str, str, str]] = []
    rec_stoken = str((rec or {}).get("stoken") or "")
    # 扫码存下的 stoken 按 mid 绑定，归属确定，优先用它；Cookie 里夹带的那份可能是
    # 浏览器 profile 残留的旧号，先换它反而会把凭据换成别的账号。
    if rec_stoken and rec_stoken != stoken:
        attempts.append((rec_stoken, str(rec.get("mid") or "") or mid,
                         str(rec.get("device_id") or "") or default_device))
    if stoken:
        attempts.append((stoken, mid, default_device))

    if not attempts:
        return {"ok": False, "need_stoken": True, "cookies": cookies, "updated": [],
                "msg": "Cookie 中没有 stoken，无法自动续期（请在客户端用米游社 App 扫码补 stoken）"}

    fail_msg = ""
    dead = False
    for token, token_mid, dev in attempts:
        if token.startswith("v2_") and not token_mid:
            fail_msg = "v2 stoken 需要 mid，请在客户端重新扫码补 stoken"
            continue
        ltoken = _exchange(LTOKEN_URL, token, token_mid, dev, "5")
        if not ltoken.get("ok"):
            fail_msg = f"ltoken 续期失败：{ltoken.get('msg')}"
            dead = dead or ltoken.get("retcode") in DEAD_STOKEN_CODES
            continue
        cookie_token = _exchange(COOKIE_TOKEN_URL, token, token_mid, dev, "2")
        if not cookie_token.get("ok"):
            fail_msg = f"cookie_token 续期失败：{cookie_token.get('msg')}"
            dead = dead or cookie_token.get("retcode") in DEAD_STOKEN_CODES
            continue

        new_ltoken = str((ltoken.get("value") or {}).get("ltoken") or "")
        new_cookie = str((cookie_token.get("value") or {}).get("cookie_token") or "")
        if not new_ltoken or not new_cookie:
            fail_msg = "续期接口未返回新 token"
            continue

        # 用扫码 stoken 才成功时，把它写回 Cookie，下次就不会再拿旧号换票。
        base = cookies if token == stoken else _with_stoken(cookies, token, rec)
        new_cookies, updated = _apply(base, new_ltoken, new_cookie)
        return {"ok": True, "msg": "自动续期成功", "cookies": new_cookies, "updated": updated}

    if dead:
        # -100/-101 是「登录状态失效」，这个 stoken 已经彻底没用了：从 Cookie 里摘掉，
        # 避免它被继续注入浏览器、也避免每次上号都弹同样的报错，改为提示用户扫码。
        # 扫码入库的那份若已注销也要删掉，否则 merge_stoken 每次又会把它补回来。
        if rec and rec.get("mid"):
            try:
                execute("DELETE FROM cloud_stokens WHERE user_id=? AND mid=?", (user_id, rec["mid"]))
            except Exception:
                log.exception("删除已失效 stoken 失败：user=%s mid=%s", user_id, rec["mid"])
        return {"ok": False, "msg": "登录状态已失效，请重新扫码补 stoken",
                "cookies": _without_stoken(cookies), "updated": [],
                "need_stoken": True, "drop_stoken": True}
    return {"ok": False, "msg": fail_msg, "cookies": cookies, "updated": [],
            "need_stoken": fail_msg.startswith("v2 stoken")}


def refresh_stored(user_id: int, game: str = "", uid: str = "", force: bool = False,
                   max_age: int = LAZY_REFRESH_SECONDS) -> dict:
    """对数据库里的某条云 Cookie 做续期并回写。

    force=False 时按 max_age 节流；最近续期失败的账号还会额外等 FAIL_BACKOFF。
    返回 {"ok", "msg", "refreshed", "skipped"}；找不到记录时返回 {}。
    """
    if game not in MIHOYO_GAMES:
        return {"ok": False, "skipped": True, "refreshed": False,
                "msg": "会话型 Cookie 不支持自动续期"}

    if uid:
        row = query_one("SELECT id, data, game_uid, refresh_at, refresh_ok FROM cloud_cookies "
                        "WHERE user_id=? AND game=? AND game_uid=?", (user_id, game, uid))
    else:
        row = query_one("SELECT id, data, game_uid, refresh_at, refresh_ok FROM cloud_cookies "
                        "WHERE user_id=? AND game=? ORDER BY updated_at DESC, id DESC LIMIT 1",
                        (user_id, game))
    if not row:
        return {}

    now = security.now_s()
    age = now - int(row["refresh_at"] or 0)
    if not force:
        if age < max_age:
            return {"ok": bool(row["refresh_ok"]), "skipped": True, "refreshed": False,
                    "msg": "未到续期时间"}
        if row["refresh_ok"] == 0 and age < FAIL_BACKOFF_SECONDS:
            return {"ok": False, "skipped": True, "refreshed": False,
                    "msg": "最近续期失败，稍后自动重试"}

    with _lock:
        # 加锁后重新读一次，避免上号线程和后台线程同时续同一条。
        row = query_one("SELECT id, data, game_uid FROM cloud_cookies WHERE id=?", (row["id"],))
        if not row:
            return {}
        cookies = security.decrypt_blob(row["data"])
        # Cookie 里没有 stoken 时，把扫码存下的补进来再换票。
        cookies, device_id = merge_stoken(cookies, user_id)
        result = refresh_cookies(cookies, user_id, row["game_uid"] or "", device_id=device_id)
        msg = str(result.get("msg") or "")[:200]
        need_stoken = bool(result.get("need_stoken"))
        if result.get("ok"):
            execute("UPDATE cloud_cookies SET data=?, refresh_at=?, refresh_ok=1, refresh_msg=? WHERE id=?",
                    (security.encrypt_blob(result["cookies"]), now, msg, row["id"]))
            log.info("云游戏 Cookie 已自动续期：user=%s game=%s uid=%s", user_id, game, row["game_uid"])
        elif result.get("drop_stoken"):
            # stoken 已注销：把摘掉它的 Cookie 写回去，别再把废凭据注入浏览器。
            execute("UPDATE cloud_cookies SET data=?, refresh_at=?, refresh_ok=0, refresh_msg=? WHERE id=?",
                    (security.encrypt_blob(result["cookies"]), now, msg, row["id"]))
            log.warning("云游戏 stoken 已失效，已从 Cookie 中移除：user=%s game=%s uid=%s",
                        user_id, game, row["game_uid"])
        else:
            execute("UPDATE cloud_cookies SET refresh_at=?, refresh_ok=0, refresh_msg=? WHERE id=?",
                    (now, msg, row["id"]))
            log.warning("云游戏 Cookie 自动续期失败：user=%s game=%s uid=%s msg=%s",
                        user_id, game, row["game_uid"], msg)
    return {"ok": bool(result.get("ok")), "refreshed": True, "skipped": False, "msg": msg,
            "need_stoken": need_stoken}


def bind_and_refresh(user_id: int, stoken: str, mid: str, stuid: str = "", device_id: str = "") -> int:
    """扫码成功后：保存 stoken，并把该用户已上传且能对上的米哈游 Cookie 立刻续一遍。

    返回命中的 Cookie 条数；对不上（别的米游社账号）的记录不动。
    """
    save_stoken(user_id, stoken, mid, stuid, device_id)
    mid = str(mid or "").strip()
    stuid = str(stuid or "").strip()
    if not mid and not stuid:
        return 0

    rows = query_all(
        "SELECT game, game_uid, data FROM cloud_cookies WHERE user_id=? AND game IN (?,?)",
        (user_id, *MIHOYO_GAMES),
    )
    matched = 0
    for row in rows:
        try:
            cookies = security.decrypt_blob(row["data"])
        except Exception:
            continue
        _stoken, cmid, cuid = credentials_from(cookies)
        matched_account = (cmid == mid) if cmid else (bool(cuid) and cuid == stuid)
        if not matched_account:
            continue
        try:
            refresh_stored(user_id, row["game"], row["game_uid"] or "", force=True)
            matched += 1
        except Exception:
            log.exception("扫码后立即续期失败：user=%s game=%s", user_id, row["game"])
    return matched


def refresh_due(limit: int = 30) -> None:
    """后台巡检：把超过 24 小时没续期的米哈游账号依次续一遍。"""
    rows = query_all(
        "SELECT user_id, game, game_uid FROM cloud_cookies WHERE game IN (?,?) "
        "ORDER BY COALESCE(refresh_at, 0) ASC LIMIT ?",
        (*MIHOYO_GAMES, limit),
    )
    for row in rows:
        try:
            refresh_stored(row["user_id"], row["game"], row["game_uid"] or "",
                           force=False, max_age=BACKGROUND_REFRESH_SECONDS)
        except Exception:
            log.exception("云游戏 Cookie 后台续期异常：user=%s game=%s", row["user_id"], row["game"])
        # 账号之间随机停顿，避免同一时间打一批请求
        time.sleep(random.uniform(1.0, 3.0))


def start_background() -> None:
    """启动后台续期线程；设 EYA_CLOUD_REFRESH=0 可关闭。"""
    if os.getenv("EYA_CLOUD_REFRESH", "1") != "1":
        return

    def _loop():
        # 启动后先等一分钟，避免和登录/聊天等首批请求抢带宽
        time.sleep(60)
        while True:
            try:
                refresh_due()
            except Exception:
                log.exception("云游戏 Cookie 巡检失败")
            time.sleep(SWEEP_SECONDS)

    threading.Thread(target=_loop, name="cloud-cookie-refresh", daemon=True).start()
