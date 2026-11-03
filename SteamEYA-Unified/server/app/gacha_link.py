"""用米游社 stoken 生成游戏抽卡记录链接（Auth Key B）。

流程：
1. 用扫码存下的 stoken 调 getUserGameRolesByStoken 拿到绑定的游戏角色（uid/region）。
2. 调 genAuthKey（auth_appid=webview_gacha）换一个临时 authkey。
3. 拼成 webstatic 查看页 / getGachaLog 接口链接。

authkey 约 24 小时过期，过期后重新调用即可；stoken 不失效就一直能生成。
只覆盖国服原神、星穹铁道；B 服角色走同一接口，region 是 cn_qd01。
"""
from __future__ import annotations

import hashlib
import random
import string
import time
import urllib.parse

import requests

from . import security
from .cloud_refresh import MIHOYO_GAMES, credentials_from, device_id_for
from .db import query_all

APP_VERSION = "2.81.1"
APP_ID = "bll8iq97cem8"
# 2.81.1 的 K2 salt（DS1 用）；来自 UIGF / starudream 的公开整理。
K2_SALT = "QVu5OdwEWxkq9ygpYBgDprR5tI471HWQ"
UA = ("Mozilla/5.0 (Linux; Android 13; 22011211C Build/TP1A.220624.014; wv) "
      f"AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/104.0.5112.97 "
      f"Mobile Safari/537.36 miHoYoBBS/{APP_VERSION}")

ROLES_URL = "https://api-takumi.miyoushe.com/binding/api/getUserGameRolesByStoken"
AUTHKEY_URL = "https://api-takumi.miyoushe.com/binding/api/genAuthKey"

GAMES = {
    "genshin": {
        "name": "原神",
        "biz": "hk4e_cn",
        "host": "public-operation-hk4e.mihoyo.com",
        "web": "https://webstatic.mihoyo.com/hk4e/event/e20190909gacha-v3/index.html",
        "gacha_id": "50eef7be3b7945930041f9f20310e0bbcb8a7a",
        "version": "CNRELAndroid5.0.0",
        "plat_type": "android",
        "gacha_type": "301",
    },
    "starrail": {
        "name": "星穹铁道",
        "biz": "hkrpg_cn",
        "host": "public-operation-hkrpg.mihoyo.com",
        "web": "https://webstatic.mihoyo.com/hkrpg/event/e20211215gacha-v2/index.html",
        "gacha_id": "",
        "version": "2.5.0",
        "plat_type": "prod_gf_cn",
        "gacha_type": "11",
    },
}


def _md5(text: str) -> str:
    return hashlib.md5(text.encode("utf-8")).hexdigest()


def ds1() -> str:
    """App 端 DS1：t,r,md5(salt&t&r)，r 为 6 位字母数字。"""
    t = int(time.time())
    r = "".join(random.choices(string.ascii_letters + string.digits, k=6))
    return f"{t},{r},{_md5(f'salt={K2_SALT}&t={t}&r={r}')}"


def _headers(stoken: str, mid: str, device_id: str, client_type: str) -> dict:
    cookie = f"mid={mid};stoken={stoken}" if mid else f"stoken={stoken}"
    return {
        "User-Agent": UA,
        "x-rpc-app_version": APP_VERSION,
        "x-rpc-client_type": client_type,
        "x-rpc-app_id": APP_ID,
        "x-rpc-device_id": device_id or "teatt-gacha",
        "DS": ds1(),
        "Cookie": cookie,
        "Accept": "application/json, text/plain, */*",
        "x-requested-with": "com.mihoyo.hyperion",
        "Referer": "https://app.mihoyo.com",
    }


def _failure(data: dict) -> str:
    code = data.get("retcode")
    msg = str(data.get("message") or "请求失败")
    if code in (-100, -111, -101):
        return "米游社 stoken 已失效，请重新扫码"
    if code == 1016:
        return "该游戏账号没有绑定这个米游社账号"
    if code == 1002:
        return "请求参数不正确"
    return f"{msg}（retcode={code}）"


def fetch_roles(stoken: str, mid: str, device_id: str) -> dict:
    """拿 stoken 对应米游社账号绑定的游戏角色。"""
    try:
        data = requests.get(ROLES_URL, headers=_headers(stoken, mid, device_id, "2"), timeout=15).json()
    except Exception as e:
        return {"ok": False, "msg": f"获取游戏账号失败：{e}", "list": []}
    if data.get("retcode") != 0:
        return {"ok": False, "msg": _failure(data), "list": []}
    games = (data.get("data") or {}).get("list") or []
    out = []
    for g in games:
        if str(g.get("game_biz") or "") not in ("hk4e_cn", "hkrpg_cn"):
            continue
        out.append({
            "game_biz": str(g.get("game_biz") or ""),
            "game": "genshin" if g.get("game_biz") == "hk4e_cn" else "starrail",
            "game_name": GAMES["genshin" if g.get("game_biz") == "hk4e_cn" else "starrail"]["name"],
            "game_uid": str(g.get("game_uid") or ""),
            "region": str(g.get("region") or ""),
            "region_name": str(g.get("region_name") or ""),
            "nickname": str(g.get("nickname") or ""),
            "level": g.get("level"),
            "is_official": bool(g.get("is_official")),
        })
    return {"ok": True, "msg": "", "list": out}


def gen_authkey(stoken: str, mid: str, device_id: str, game_biz: str,
                game_uid: str, region: str) -> dict:
    """换 Auth Key B（webview_gacha 类型）。"""
    body = {"game_biz": game_biz, "game_uid": int(game_uid), "region": region,
            "auth_appid": "webview_gacha"}
    try:
        data = requests.post(AUTHKEY_URL, json=body,
                             headers=_headers(stoken, mid, device_id, "5"),
                             timeout=15).json()
    except Exception as e:
        return {"ok": False, "msg": f"生成抽卡链接失败：{e}"}
    if data.get("retcode") != 0:
        return {"ok": False, "msg": _failure(data)}
    payload = data.get("data") or {}
    authkey = str(payload.get("authkey") or "")
    if not authkey:
        return {"ok": False, "msg": "接口没有返回 authkey"}
    return {"ok": True, "authkey": authkey,
            "authkey_ver": payload.get("authkey_ver") or 1,
            "sign_type": payload.get("sign_type") or 2}


def build_link(game: str, authkey: str, region: str) -> dict:
    """拼出查看页链接和可直接请求的 getGachaLog 链接。"""
    cfg = GAMES[game]
    params = [
        ("authkey_ver", "1"), ("sign_type", "2"), ("auth_appid", "webview_gacha"),
        ("init_type", "301"), ("gacha_id", cfg["gacha_id"]), ("timestamp", str(int(time.time()))),
        ("lang", "zh-cn"), ("device_type", "mobile"), ("game_version", cfg["version"]),
        ("plat_type", cfg["plat_type"]), ("region", region), ("authkey", authkey),
        ("game_biz", cfg["biz"]),
    ]
    query = urllib.parse.urlencode(params)
    return {
        "url": f"{cfg['web']}?{query}#/log",
        "api_url": (f"https://{cfg['host']}/gacha_info/api/getGachaLog?{query}"
                    f"&gacha_type={cfg['gacha_type']}&size=20&page=1&end_id=0"),
        "expires_in": 24 * 3600,
    }


def accounts(user_id: int) -> list[dict]:
    """所有可用的 stoken：优先扫码表，其次手动上传的云 Cookie 里自带的。"""
    out: list[dict] = []
    seen: set[tuple[str, str]] = set()

    for row in query_all(
        "SELECT mid, stuid, stoken, device_id FROM cloud_stokens "
        "WHERE user_id=? ORDER BY updated_at DESC", (user_id,)
    ):
        try:
            stoken = security.decrypt_blob(row["stoken"])
        except Exception:
            continue
        if not stoken:
            continue
        mid = str(row["mid"] or "")
        key = (mid, str(stoken))
        if key in seen:
            continue
        seen.add(key)
        out.append({"stoken": str(stoken), "mid": mid, "stuid": str(row["stuid"] or ""),
                    "device_id": str(row["device_id"] or "")})

    for row in query_all(
        "SELECT game_uid, data FROM cloud_cookies WHERE user_id=? AND game IN (?,?)",
        (user_id, *MIHOYO_GAMES),
    ):
        try:
            cookies = security.decrypt_blob(row["data"])
        except Exception:
            continue
        stoken, mid, uid = credentials_from(cookies)
        if not stoken:
            continue
        key = (mid, stoken)
        if key in seen:
            continue
        seen.add(key)
        out.append({"stoken": stoken, "mid": mid, "stuid": uid,
                    "device_id": device_id_for(user_id, str(row["game_uid"] or ""))})
    return out


def roles_for_user(user_id: int) -> tuple[list[dict], list[str]]:
    accs = accounts(user_id)
    if not accs:
        raise ValueError("还没有可用的米游社 stoken，请先在客户端用米游社 App 扫码")
    roles: list[dict] = []
    errors: list[str] = []
    for acc in accs:
        res = fetch_roles(acc["stoken"], acc["mid"], acc["device_id"])
        if not res["ok"]:
            errors.append(res["msg"])
            continue
        for role in res["list"]:
            role["mid"] = acc["mid"]
            roles.append(role)
    return roles, errors


def link_for_user(user_id: int, game: str, game_uid: str, region: str,
                  mid: str = "") -> dict:
    if game not in GAMES:
        raise ValueError("只支持原神和星穹铁道")
    if not game_uid or not region:
        raise ValueError("缺少游戏账号 UID 或区服")
    cfg = GAMES[game]
    accs = accounts(user_id)
    if mid:
        accs = [a for a in accs if a["mid"] == mid]
    if not accs:
        raise ValueError("还没有可用的米游社 stoken，请先在客户端用米游社 App 扫码")

    last = "没有可用的 stoken"
    for acc in accs:
        got = gen_authkey(acc["stoken"], acc["mid"], acc["device_id"],
                          cfg["biz"], game_uid, region)
        if got["ok"]:
            link = build_link(game, got["authkey"], region)
            return {"game": game, "game_name": cfg["name"], "game_uid": game_uid,
                    "region": region, **link}
        last = got["msg"]
    raise ValueError(last)
