"""云游戏模块：Cookie 上传/下载/检测/上报（原端口 6966 的客户端接口）。

Cookie 在服务端以主密钥加密存储；检测逻辑（米哈游/完美世界/哔哩哔哩）从原实现移植。
"""
from __future__ import annotations

import json
import os
import time
import uuid
from datetime import datetime

import requests
from fastapi import APIRouter, Body, Depends, HTTPException, Request

from .. import security
from .. import cloud_refresh
from .. import mihoyo_qr
from ..config import DATA_DIR
from ..db import execute, execute_update, query_all, query_one
from ..deps import get_current_user, require_admin

router = APIRouter(prefix="/api/cloud", tags=["cloud"])

GAMES = {
    "genshin": {"name": "原神", "biz": "hk4e_cn", "vendor": "mihoyo", "url": "https://ys.mihoyo.com/cloud/#/"},
    "starrail": {"name": "星穹铁道", "biz": "hkrpg_cn", "vendor": "mihoyo", "url": "https://sr.mihoyo.com/cloud/#/"},
    "yihuan": {"name": "异环", "biz": "yh", "vendor": "wanmei", "url": "https://yh.wanmei.com/cloud/index.html"},
    "bilibili": {"name": "哔哩哔哩", "biz": "", "vendor": "bilibili", "url": "https://www.bilibili.com/"},
}

BILI_NAV_API = "https://api.bilibili.com/x/web-interface/nav"
WANMEI_LOGIN_API = "https://id.wanmei.com/cloud/game/login"
WANMEI_INITCONFIG_API = "https://id.wanmei.com/cloud/game/initConfig"
WANMEI_USERINFO_API = "https://id.wanmei.com/cloud/game/getUserInfo"

MIHOYO_CLOUD_WALLET = {
    "genshin": {"url": "https://api-cloudgame.mihoyo.com/hk4e_cg_cn/wallet/wallet/get",
                "host": "api-cloudgame.mihoyo.com", "referer": "https://app.mihoyo.com/", "origin": "https://ys.mihoyo.com"},
    "starrail": {"url": "https://cg-hkrpg-api.mihoyo.com/hkrpg_cn/cg/wallet/wallet/get?cost_method=0",
                 "host": "cg-hkrpg-api.mihoyo.com", "referer": "https://sr.mihoyo.com/", "origin": "https://sr.mihoyo.com"},
}

DEFAULT_GAME_KEY = "genshin"


def _normalize_url(raw: str) -> str:
    """补全协议：用户填 douyin.com 就补成 https://douyin.com。"""
    url = (raw or "").strip()
    if not url:
        return ""
    if not url.lower().startswith(("http://", "https://")):
        url = "https://" + url.lstrip("/")
    return url[:500]


def seed_builtin_games() -> None:
    """把内置 4 个游戏同步进表（幂等），这样列表接口只需要查表。"""
    now = security.now_s()
    for key, cfg in GAMES.items():
        execute(
            "INSERT INTO cloud_games (game_key, name, url, vendor, biz, builtin, visible, created_at) "
            "VALUES (?,?,?,?,?,1,1,?) "
            "ON CONFLICT(game_key) DO UPDATE SET name=excluded.name, url=excluded.url, "
            "vendor=excluded.vendor, biz=excluded.biz, builtin=1, visible=1",
            (key, cfg["name"], cfg["url"], cfg["vendor"], cfg["biz"], now),
        )


def game_name_map() -> dict:
    """game_key → 显示名。内置表优先，其次用户自定义游戏（cloud_games 表），都没有才回退 key。

    之前 admin/账号列表直接 `GAMES.get(...) or key`，自定义游戏不在 GAMES 里，
    于是后台和客户端的「我的账号」都显示成 custom_xxxxxxxx。
    """
    mapping = {k: v["name"] for k, v in GAMES.items()}
    try:
        for row in query_all("SELECT game_key, name FROM cloud_games"):
            mapping.setdefault(row["game_key"], row["name"])
    except Exception:
        # 老库还没建表时不阻断列表接口，回退到内置表。
        pass
    return mapping


def resolve_game_name(game_key: str) -> str:
    if not game_key:
        return ""
    return game_name_map().get(game_key) or game_key


def game_exists(game_key: str) -> bool:
    if game_key in GAMES:
        return True
    return query_one("SELECT 1 FROM cloud_games WHERE game_key=?", (game_key,)) is not None


@router.get("/games")
def list_games(user: dict = Depends(get_current_user)):
    """可见游戏 = 内置 + 已公开的自定义游戏 + 自己创建但还没公开的。"""
    seed_builtin_games()
    rows = query_all(
        "SELECT game_key, name, url, vendor, builtin, visible, created_by FROM cloud_games "
        "WHERE visible=1 OR created_by=? ORDER BY builtin DESC, id",
        (user["id"],),
    )
    return {
        "ok": True,
        "games": [
            {
                "key": r["game_key"],
                "name": r["name"],
                "url": r["url"] or "",
                "vendor": r["vendor"] or "custom",
                "builtin": bool(r["builtin"]),
                "visible": bool(r["visible"]),
                "mine": r["created_by"] == user["id"],
            }
            for r in rows
        ],
    }


@router.post("/games")
def create_game(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    """新增自定义游戏。创建后仅创建者可见，等他成功上传一次 Cookie 再对所有人公开。"""
    name = (payload.get("name") or "").strip()[:40]
    url = _normalize_url(str(payload.get("url") or ""))
    if not name:
        raise HTTPException(400, "游戏名不能为空")
    if not url:
        raise HTTPException(400, "请填写游戏地址")
    if "://" not in url:
        raise HTTPException(400, "地址格式不正确")

    seed_builtin_games()

    # 同名直接复用，避免同一个人或不同人把「抖音」建出十条。
    existing = query_one("SELECT game_key, visible, created_by FROM cloud_games WHERE name=?", (name,))
    if existing:
        return {"ok": True, "key": existing["game_key"], "existing": True,
                "visible": bool(existing["visible"])}

    game_key = "custom_" + uuid.uuid4().hex[:12]
    execute(
        "INSERT INTO cloud_games (game_key, name, url, vendor, biz, builtin, visible, created_by, created_at) "
        "VALUES (?,?,?,?,?,0,0,?,?)",
        (game_key, name, url, "custom", "", user["id"], security.now_s()),
    )
    return {"ok": True, "key": game_key, "existing": False, "visible": False}


UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")


def _get_mihoyo_free_duration(combo_token: str, game: str) -> dict:
    cfg = MIHOYO_CLOUD_WALLET.get(game)
    if not cfg:
        return {"ok": False, "msg": f"{game} 暂不支持云游戏钱包查询"}
    if not combo_token:
        return {"ok": False, "msg": "缺少 combo_token，无法查询免费时长"}
    headers = {
        "Host": cfg["host"], "Accept": "*/*", "Referer": cfg["referer"], "Origin": cfg["origin"],
        "x-rpc-combo_token": combo_token, "Accept-Encoding": "gzip, deflate", "User-Agent": UA,
    }
    if game == "starrail":
        headers.update({
            "x-rpc-client_type": "3", "x-rpc-cg_game_biz": "hkrpg_cn", "x-rpc-cg_game_id": "9000096",
            "x-rpc-vendor_id": "2", "x-rpc-op_biz": "clgm_hkrpg-cn", "x-rpc-language": "zh-cn",
            "Connection": "Keep-Alive",
        })
    try:
        data = requests.get(cfg["url"], headers=headers, timeout=12).json()
    except Exception as e:
        return {"ok": False, "msg": f"请求失败：{e}"}
    if data.get("retcode") == -100:
        return {"ok": False, "expired": True, "msg": "combo_token 已失效，请重新抓取"}
    if data.get("retcode") != 0:
        return {"ok": False, "msg": f"{data.get('message', '查询失败')}（retcode={data.get('retcode')}）"}
    d = data.get("data") or {}
    ft = d.get("free_time") or {}
    try:
        free_duration = int(ft.get("free_time"))
    except (TypeError, ValueError):
        free_duration = None
    return {"ok": True, "free_duration": free_duration, "total_duration": None,
            "play_card": (d.get("play_card") or {}).get("short_msg", ""),
            "coin": (d.get("coin") or {}).get("coin_num"), "msg": "查询成功"}


# B 站风控/限流类返回码：这些不是「Cookie 失效」，而是这次请求被拦了，不能覆盖成失效。
BILI_RISK_CODES = {-352: "风控校验失败", -412: "请求被拦截", -400: "请求参数错误",
                   -799: "请求过于频繁", -509: "请求过于频繁", -504: "服务调用超时"}


def _check_bilibili_valid(cookie_str: str, cmap: dict) -> dict:
    if not cmap.get("SESSDATA"):
        return {"valid": False, "msg": "Cookie 中缺少 SESSDATA，未登录"}
    headers = {"Cookie": cookie_str, "User-Agent": UA, "Referer": "https://www.bilibili.com/",
               "Accept": "application/json, text/plain, */*"}
    try:
        data = requests.get(BILI_NAV_API, headers=headers, timeout=12).json()
    except Exception as e:
        return {"valid": None, "msg": f"请求失败：{e}"}
    code = data.get("code")
    d = data.get("data") or {}
    if code == 0 and d.get("isLogin"):
        vip = d.get("vipStatus") == 1
        return {"valid": True, "msg": "有效" + ("（大会员）" if vip else ""),
                "nickname": d.get("uname", ""),
                "game_uid": str(d.get("mid", "") or ""),
                "level": (d.get("level_info") or {}).get("current_level"),
                "coin": d.get("money"), "vip": vip, "free_duration": None}
    if code == -101:
        return {"valid": False, "msg": "账号未登录，Cookie 已失效"}
    if code == 0 and not d.get("isLogin"):
        return {"valid": False, "msg": "账号未登录，Cookie 已失效"}
    if code in BILI_RISK_CODES:
        # 风控拦截时无法判断真实登录态，保留上一次的检测结果，不要误判成失效。
        return {"valid": None, "msg": f"B 站风控拦截（{BILI_RISK_CODES[code]}，code={code}），请稍后再试"}
    return {"valid": False, "msg": f"{data.get('message', '验证失败')}（code={code}）"}


def _wanmei_params() -> dict:
    return {
        "appId": "10607", "deviceId": "yhweb_5f3a9c21", "bid": "com.pwrd.cloud.yh.laohuweb",
        "provider": "0", "channelId": "6800", "version": "1.1.5",
        "sdkVersion": "5.15.3.260622023945-wlweb-release", "env": "0",
        "deviceModel": "Chrome/120.0.0.0 Safari/537.36", "deviceSys": "6", "deviceType": "Windows",
        "deviceName": "Chrome/120.0.0.0", "oneAppId": "1289", "onePlatformId": "22",
        "t": str(int(time.time() * 1000)),
    }


def _wanmei_headers(cookie_str: str) -> dict:
    return {"Cookie": cookie_str, "User-Agent": UA, "Referer": "https://yh.wanmei.com/",
            "Origin": "https://yh.wanmei.com", "Accept": "application/json, text/javascript, */*; q=0.01",
            "X-Requested-With": "XMLHttpRequest"}


def _find_user_id(obj, depth: int = 0) -> int:
    if depth > 4 or obj is None:
        return 0
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k.lower() in ("userid", "uid", "user_id") and isinstance(v, int) and v > 0:
                return v
            if isinstance(v, (dict, list)):
                got = _find_user_id(v, depth + 1)
                if got:
                    return got
    elif isinstance(obj, list):
        for it in obj:
            got = _find_user_id(it, depth + 1)
            if got:
                return got
    return 0


def _check_wanmei_valid(cookie_str: str) -> dict:
    headers = _wanmei_headers(cookie_str)
    valid, uid, fail_msg = None, 0, ""
    try:
        data = requests.post(WANMEI_LOGIN_API, data=_wanmei_params(), headers=headers, timeout=12).json()
        code = data.get("code")
        if code == 0:
            valid = True
            uid = _find_user_id(data.get("result"))
        elif code == 10004:
            return {"valid": False, "msg": data.get("message", "登录态失效")}
        else:
            fail_msg = f"{data.get('message', '验证失败')}（code={code}）"
    except Exception as e:
        fail_msg = f"请求失败：{e}"
    if valid and not uid:
        try:
            d2 = requests.get(WANMEI_INITCONFIG_API, params=_wanmei_params(), headers=headers, timeout=12).json()
            if d2.get("code") == 0:
                uid = _find_user_id(d2.get("result"))
        except Exception:
            pass
    free_duration = total_duration = None
    if uid:
        try:
            u = requests.get(WANMEI_USERINFO_API, params={**_wanmei_params(), "userId": str(uid)},
                             headers=headers, timeout=12).json()
            if u.get("code") == 0:
                res = u.get("result") or {}
                valid = True
                uid = res.get("userId") or uid
                free_duration = res.get("remainedFreeDuration")
                total_duration = res.get("remainedDuration")
            elif u.get("code") == 10004:
                return {"valid": False, "msg": u.get("message", "登录态失效")}
        except Exception:
            pass
    if valid:
        return {"valid": True, "msg": "有效", "game_uid": str(uid or ""), "nickname": "",
                "free_duration": free_duration, "total_duration": total_duration}
    return {"valid": None, "msg": fail_msg or "无法确定登录态"}


def check_cookie_valid(cookies: list, game: str, combo_token: str = "") -> dict:
    cfg = GAMES.get(game)
    cmap = {c.get("name"): c.get("value") for c in cookies if c.get("name")}
    if not cmap:
        return {"valid": None, "msg": "Cookie 为空"}
    if cfg is None:
        # 用户自定义游戏：没有对应的官方接口可查，只要 cookie 抓到了就当作可用。
        return {"valid": True, "msg": "自定义游戏不做有效性校验", "nickname": "", "game_uid": ""}
    cookie_str = "; ".join(f"{k}={v}" for k, v in cmap.items())
    # 兼容只在 Cookie 里带了云游戏凭证的情况（部分 WebView / 粘贴来源会这样）
    combo_token = combo_token or cmap.get("combo_token", "") or cmap.get("x-rpc-combo_token", "")
    if cfg.get("vendor") == "wanmei":
        return _check_wanmei_valid(cookie_str)
    if cfg.get("vendor") == "bilibili":
        return _check_bilibili_valid(cookie_str, cmap)
    url = f"https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz={cfg['biz']}"
    headers = {"Cookie": cookie_str, "User-Agent": UA, "Referer": "https://webstatic.mihoyo.com/",
               "Accept": "application/json, text/plain, */*"}
    try:
        data = requests.get(url, headers=headers, timeout=12).json()
    except Exception as e:
        return {"valid": None, "msg": f"请求失败：{e}"}
    retcode = data.get("retcode")
    if retcode == 0:
        roles = (data.get("data") or {}).get("list") or []
        result = {"valid": True, "msg": "有效",
                  "nickname": roles[0].get("nickname", "") if roles else "",
                  "game_uid": str(roles[0].get("game_uid", "")) if roles else "",
                  "level": roles[0].get("level") if roles else None,
                  "region": roles[0].get("region_name", "") if roles else "",
                  "role_count": len(roles)}
        if not roles:
            result["msg"] = "Cookie 有效（未查询到角色）"
        # 没抓到 combo_token 时不能因此判 Cookie 无效：安卓 WebView / 粘贴来源经常只有 Cookie，
        # 云游戏页面自己也能用 Cookie 换到凭证，只是后台暂时显示不了剩余时长。
        if not combo_token:
            result["msg"] = ("Cookie 有效（未查询到角色）" if not roles else "有效") + \
                            "（未抓取云游戏凭证，剩余时长未知）"
            result["free_duration"] = None
            result["total_duration"] = None
            return result
        # 原神 / 星穹铁道：查不到剩余免费时长时区分「凭证过期」和「临时查询失败」
        wr = _get_mihoyo_free_duration(combo_token, game)
        if not wr.get("ok") or wr.get("free_duration") is None:
            if wr.get("expired"):
                msg = "云游戏凭证已失效，请重新抓取"
                if wr.get("msg"):
                    msg += f"：{wr['msg']}"
                return {
                    "valid": False,
                    "msg": msg,
                    "nickname": result.get("nickname", ""),
                    "game_uid": result.get("game_uid", ""),
                    "level": result.get("level"),
                    "region": result.get("region", ""),
                    "role_count": result.get("role_count", 0),
                    "free_duration": None,
                    "total_duration": None,
                }
            result["msg"] = f"有效（剩余时长查询失败：{wr.get('msg') or '接口未返回时长'}）"
            result["free_duration"] = None
            result["total_duration"] = None
            return result
        result["free_duration"] = wr["free_duration"]
        result["total_duration"] = wr.get("total_duration")
        result["msg"] = f"有效，剩余免费时长 {wr['free_duration']} 分钟"
        return result
    if retcode in (-100, -101, 10001, -110):
        return {"valid": False, "msg": data.get("message", "Cookie 已失效")}
    return {"valid": False, "msg": f"{data.get('message', '验证失败')}（retcode={retcode}）"}


def _client_ip(request: Request) -> str:
    xf = (request.headers.get("x-forwarded-for") or "").split(",")[0].strip()
    return xf or (request.client.host if request.client else "")


# 同一用户同一游戏在这个时间窗内只记一条上号日志。
# 电脑端上号会先 download 再 report_login，两处都记录会变成两条。
CLOUD_LOGIN_DEDUPE_S = 60


def _record_cloud_login(user_id: int, game: str, ip: str) -> None:
    """记一条上号日志（含更新最近登录时间）；时间窗内重复调用只记一次。"""
    now = security.now_s()
    # users.last_login_at 全站统一用毫秒；cloud_login_logs.login_at 仍用秒（后台按秒展示）
    execute("UPDATE users SET last_login_at=? WHERE id=?", (security.now_ms(), user_id))
    last = query_one("SELECT login_at FROM cloud_login_logs WHERE user_id=? AND game=? "
                     "ORDER BY login_at DESC, id DESC LIMIT 1", (user_id, game))
    if last and last["login_at"] and now - last["login_at"] < CLOUD_LOGIN_DEDUPE_S:
        return
    execute("INSERT INTO cloud_login_logs (user_id, game, login_at, client_ip) VALUES (?,?,?,?)",
            (user_id, game, now, ip))


@router.post("/upload")
def upload(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = payload.get("game") or DEFAULT_GAME_KEY
    cookies = payload.get("cookies")
    combo_token = payload.get("combo_token") or ""
    # 有些来源把云游戏凭证放在 Cookie 里，这里补到 combo_token，后续检测 / 续期才用得上
    if not combo_token:
        for c in cookies or []:
            if str(c.get("name") or "").lower() in ("combo_token", "x-rpc-combo_token"):
                combo_token = str(c.get("value") or "")
                break
    if not game_exists(game):
        raise HTTPException(400, "未知游戏")
    if not cookies:
        raise HTTPException(400, "Cookie 数据为空")
    # 网页抓到的 Cookie 通常没有 stoken；如果之前扫码存过，这里直接补进 Cookie 再入库，
    # 后续下载 / 检测 / 后台巡检就都能无感续期。
    if game in cloud_refresh.MIHOYO_GAMES:
        cookies, _device = cloud_refresh.merge_stoken(cookies, user["id"])
    # 先检测：用 game_uid 区分同一用户下的多个游戏账号（同游戏可存多条）
    result = check_cookie_valid(cookies, game, combo_token)
    game_uid = str(result.get("game_uid") or "").strip()[:64]
    blob = security.encrypt_blob(cookies)
    now = security.now_s()
    execute(
        "INSERT INTO cloud_cookies (user_id, game, game_uid, data, combo_token, updated_at, "
        "valid, checked_at, nickname, free_duration, total_duration) VALUES (?,?,?,?,?,?,?,?,?,?,?) "
        "ON CONFLICT(user_id, game, game_uid) DO UPDATE SET data=excluded.data, "
        # 新数据没带 combo_token 时保留旧的，别把之前能用的凭证清掉
        "combo_token=COALESCE(NULLIF(excluded.combo_token,''), cloud_cookies.combo_token), "
        "updated_at=excluded.updated_at, valid=excluded.valid, "
        "checked_at=excluded.checked_at, nickname=excluded.nickname, "
        "free_duration=excluded.free_duration, total_duration=excluded.total_duration",
        (user["id"], game, game_uid, blob, combo_token, now,
         1 if result.get("valid") else 0, now, result.get("nickname", ""),
         result.get("free_duration"), result.get("total_duration")),
    )
    # 自定义游戏：首个成功上传的人让它对所有人可见，其他人就不用再自己建一遍。
    execute_update("UPDATE cloud_games SET visible=1 WHERE game_key=? AND visible=0", (game,))

    # 米哈游账号：上传后立刻用 stoken 换一次票，验证「以后能长期无感续期」。
    # 会话型游戏（异环/哔哩哔哩）没有可换的票，refresh_ok 返回 null，客户端不提示。
    if game in cloud_refresh.MIHOYO_GAMES:
        refreshed = cloud_refresh.refresh_stored(user["id"], game, game_uid, force=True)
        refresh_ok = refreshed.get("ok")
        refresh_msg = refreshed.get("msg") or ""
        need_stoken = bool(refreshed.get("need_stoken"))
    else:
        refresh_ok = None
        refresh_msg = ""
        need_stoken = False
    return {"ok": True, "msg": "上传成功", "valid": result.get("valid"),
            "nickname": result.get("nickname", ""), "uid": game_uid,
            "detail": result.get("msg"),
            "refresh_ok": refresh_ok, "refresh_msg": refresh_msg,
            "need_stoken": need_stoken}


# ---------------- 内部转发：让旧 6966 上号器服务跨库同步 ----------------
# 仅允许本机调用，且必须携带共享密钥（文件 / 环境变量），不对外暴露。
RELAY_KEY_FILE = DATA_DIR / "cloud_relay.key"
CLOUD_USER_MAP_FILE = DATA_DIR / "cloud_user_map.json"


def _relay_key() -> str:
    key = (os.getenv("CLOUD_RELAY_KEY") or "").strip()
    if key:
        return key
    try:
        return RELAY_KEY_FILE.read_text(encoding="utf-8").strip()
    except Exception:
        return ""


def _load_user_map() -> dict:
    try:
        data = json.loads(CLOUD_USER_MAP_FILE.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def _ensure_user(username: str, nickname: str = ""):
    username = (username or "").strip()[:32]
    if not username:
        return None
    row = query_one("SELECT * FROM users WHERE username = ?", (username,))
    if row:
        return dict(row)
    uid = execute(
        "INSERT INTO users (username, password_hash, nickname, created_at) VALUES (?,?,?,?)",
        (username, security.gen_token(24), (nickname or username)[:20], security.now_ms()))
    return dict(query_one("SELECT * FROM users WHERE id = ?", (uid,)))


@router.post("/internal/relay")
def internal_relay(request: Request, payload: dict = Body(...)):
    """接收旧版 6966 服务转发来的 Cookie（仅本机 + 共享密钥）。"""
    key = _relay_key()
    if not key or request.headers.get("x-relay-key") != key:
        raise HTTPException(403, "forbidden")
    ip = request.client.host if request.client else ""
    if ip not in ("127.0.0.1", "::1"):
        raise HTTPException(403, "forbidden")
    game = str(payload.get("game") or "")
    if game not in GAMES:
        raise HTTPException(400, "未知游戏")
    cookies = payload.get("cookies")
    if not isinstance(cookies, list) or not cookies:
        raise HTTPException(400, "Cookie 数据为空")
    src_user = str(payload.get("username") or "").strip()
    target = str(_load_user_map().get(src_user) or src_user).strip()
    user = _ensure_user(target, src_user)
    if not user:
        raise HTTPException(400, "无法确定目标用户")
    valid = payload.get("valid")
    valid_val = 1 if valid is True else (0 if valid is False else None)
    game_uid = str(payload.get("game_uid") or "").strip()[:64]
    now = security.now_s()
    execute(
        "INSERT INTO cloud_cookies (user_id, game, game_uid, data, combo_token, updated_at, valid, "
        "checked_at, nickname, free_duration, total_duration) VALUES (?,?,?,?,?,?,?,?,?,?,?) "
        "ON CONFLICT(user_id, game, game_uid) DO UPDATE SET data=excluded.data, "
        # 新数据没带 combo_token 时保留旧的，别把之前能用的凭证清掉
        "combo_token=COALESCE(NULLIF(excluded.combo_token,''), cloud_cookies.combo_token), "
        "updated_at=excluded.updated_at, valid=excluded.valid, "
        "checked_at=excluded.checked_at, nickname=excluded.nickname, "
        "free_duration=excluded.free_duration, total_duration=excluded.total_duration",
        (user["id"], game, game_uid, security.encrypt_blob(cookies),
         str(payload.get("combo_token") or ""), now, valid_val, now,
         str(payload.get("nickname") or ""), payload.get("free_duration"),
         payload.get("total_duration")))
    return {"ok": True, "userId": user["id"], "username": user["username"], "uid": game_uid}


@router.get("/download")
def download(request: Request, game: str = "genshin", uid: str = "",
             user: dict = Depends(get_current_user)):
    """下载 Cookie；传 uid 指定账号，缺省取最近上传的那个。

    米哈游账号超过 12 小时没续期会先自动换票，客户端拿到的就是新鲜 Cookie，
    用户完全无感；换票失败不影响返回旧 Cookie，但会带上失败原因。

    这里同时记一条上号日志：安卓端上号只调这个接口（不调 report_login），
    在服务端补记才能让各端上号记录统一。
    """
    refresh_result = cloud_refresh.refresh_stored(user["id"], game, uid, force=False)
    need_stoken = bool(refresh_result.get("need_stoken"))
    if uid:
        row = query_one("SELECT data, combo_token, updated_at, game_uid, refresh_at, refresh_ok, refresh_msg "
                        "FROM cloud_cookies WHERE user_id=? AND game=? AND game_uid=?",
                        (user["id"], game, uid))
    else:
        row = query_one("SELECT data, combo_token, updated_at, game_uid, refresh_at, refresh_ok, refresh_msg "
                        "FROM cloud_cookies WHERE user_id=? AND game=? "
                        "ORDER BY updated_at DESC, id DESC LIMIT 1", (user["id"], game))
    if not row:
        raise HTTPException(404, "该账号尚未上传该游戏的 Cookie")
    try:
        cookies = security.decrypt_blob(row["data"])
    except Exception:
        raise HTTPException(500, "数据解密失败")
    _record_cloud_login(user["id"], game, _client_ip(request))
    return {"ok": True, "cookies": cookies, "combo_token": row["combo_token"] or "",
            "game_uid": row["game_uid"] or "", "updated_at": row["updated_at"],
            "refresh_at": row["refresh_at"],
            "refresh_ok": None if row["refresh_ok"] is None else bool(row["refresh_ok"]),
            "refresh_msg": row["refresh_msg"] or "",
            "need_stoken": need_stoken}


@router.delete("/my")
def delete_my(game: str = "genshin", uid: str = "", user: dict = Depends(get_current_user)):
    """删除自己的某个云账号；uid 为空时删该游戏下最新一条。

    客户端（安卓/PC）的「删除」按钮走这里；之前没有这个接口，按钮点了只会报 405。
    """
    if uid:
        deleted = execute_update(
            "DELETE FROM cloud_cookies WHERE user_id=? AND game=? AND game_uid=?",
            (user["id"], game, uid))
    else:
        row = query_one("SELECT id FROM cloud_cookies WHERE user_id=? AND game=? "
                        "ORDER BY updated_at DESC, id DESC LIMIT 1", (user["id"], game))
        deleted = execute_update("DELETE FROM cloud_cookies WHERE id=? AND user_id=?",
                                 (row["id"], user["id"])) if row else 0
    if not deleted:
        raise HTTPException(404, "没有找到要删除的云账号")
    return {"ok": True, "deleted": deleted}


# ---------------- 米游社扫码补 stoken ----------------
# 云游戏网页 Cookie 只有短期 token，没有 stoken 就无法自动续期。
# 这里用米游社 App 扫码拿长期 stoken（不是云游戏 combo_token）。
@router.post("/stoken/create")
def stoken_create(user: dict = Depends(get_current_user)):
    result = mihoyo_qr.create_session(user["id"])
    if not result.get("ok"):
        raise HTTPException(400, result.get("msg") or "生成二维码失败")
    return result


@router.post("/stoken/poll")
def stoken_poll(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    session = str(payload.get("session") or "").strip()
    if not session:
        raise HTTPException(400, "缺少扫码会话")
    result = mihoyo_qr.poll_session(user["id"], session)
    if result.get("state") == "confirmed":
        matched = cloud_refresh.bind_and_refresh(
            user["id"], result["stoken"], result["mid"], result.get("stuid", ""),
            result.get("device_id", ""))
        result["matched"] = matched
        if matched:
            result["msg"] = f"{result['msg']}，已为 {matched} 个云账号续期"
        else:
            result["msg"] = f"{result['msg']}；下次抓取 Cookie 时会自动合并"
    # stoken 属于敏感凭据，不回传给客户端
    result.pop("stoken", None)
    return result


@router.post("/report_login")
def report_login(request: Request, payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = payload.get("game") or "genshin"
    _record_cloud_login(user["id"], game, _client_ip(request))
    return {"ok": True}


@router.post("/check")
def check(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = payload.get("game") or "genshin"
    uid = str(payload.get("uid") or "").strip()
    if uid:
        row = query_one("SELECT id, data, combo_token, game_uid, nickname FROM cloud_cookies "
                        "WHERE user_id=? AND game=? AND game_uid=?", (user["id"], game, uid))
    else:
        row = query_one("SELECT id, data, combo_token, game_uid, nickname FROM cloud_cookies "
                        "WHERE user_id=? AND game=? ORDER BY updated_at DESC, id DESC LIMIT 1", (user["id"], game))
    if not row:
        raise HTTPException(404, "尚未上传该游戏 Cookie")
    row_id = row["id"]
    cookies = security.decrypt_blob(row["data"])
    result = check_cookie_valid(cookies, game, row["combo_token"] or "")
    refresh_msg = ""
    need_stoken = False
    if result.get("valid") is False and game in cloud_refresh.MIHOYO_GAMES:
        # 检测到失效先尝试 stoken 换票，再复检一次：很多「失效」只是短期 token 到期。
        rr = cloud_refresh.refresh_stored(user["id"], game, row["game_uid"] or "", force=True)
        if rr.get("ok"):
            fresh = query_one("SELECT data, combo_token, game_uid, nickname FROM cloud_cookies WHERE id=?",
                              (row["id"],))
            if fresh:
                cookies = security.decrypt_blob(fresh["data"])
                result = check_cookie_valid(cookies, game, fresh["combo_token"] or "")
                row = fresh
                refresh_msg = "检测前已自动续期"
        elif rr.get("refreshed"):
            refresh_msg = rr.get("msg") or "自动续期失败"
            need_stoken = bool(rr.get("need_stoken"))
    execute(
        "UPDATE cloud_cookies SET valid=?, checked_at=?, game_uid=?, nickname=?, free_duration=?, total_duration=? "
        "WHERE id=?",
        (1 if result.get("valid") else (0 if result.get("valid") is False else None),
         security.now_s(), result.get("game_uid") or row["game_uid"] or "",
         result.get("nickname") or row["nickname"] or "",
         result.get("free_duration"), result.get("total_duration"), row_id),
    )
    return {"ok": True, **result, "refresh_msg": refresh_msg, "need_stoken": need_stoken}


@router.get("/my")
def my(user: dict = Depends(get_current_user)):
    rows = query_all(
        "SELECT game, updated_at, valid, checked_at, nickname, game_uid, free_duration, total_duration, "
        "refresh_at, refresh_ok, refresh_msg, data FROM cloud_cookies WHERE user_id=? "
        "ORDER BY game, updated_at DESC, id DESC", (user["id"],),
    )
    names = game_name_map()
    out = []
    for row in rows:
        item = dict(row)
        blob = item.pop("data", None)
        item["refresh_at"] = int(item.get("refresh_at") or 0)
        item["refresh_msg"] = item.get("refresh_msg") or ""
        item["game_name"] = names.get(item["game"]) or item["game"]
        # 客户端据此显示「stoken 未补」并引导扫码；会话型游戏恒为 False。
        has_stoken = False
        if item["game"] in cloud_refresh.MIHOYO_GAMES and blob:
            try:
                has_stoken = cloud_refresh.stoken_bound(user["id"], security.decrypt_blob(blob))
            except Exception:
                has_stoken = False
        item["has_stoken"] = has_stoken
        out.append(item)
    return {"ok": True, "games": out}


# ---------------- 管理员：云游戏账号管理 ----------------
@router.get("/admin/games")
def admin_games(admin: dict = Depends(require_admin)):
    """后台：全部游戏（内置 + 用户自定义），供管理页面做「删除自定义游戏名」。"""
    seed_builtin_games()
    rows = query_all(
        "SELECT g.game_key, g.name, g.url, g.vendor, g.builtin, g.visible, g.created_by, g.created_at, "
        "COALESCE(NULLIF(u.nickname,''), u.username, '') AS creator "
        "FROM cloud_games g LEFT JOIN users u ON u.id = g.created_by "
        "ORDER BY g.builtin DESC, g.id"
    )
    out = []
    for r in rows:
        d = dict(r)
        # 该游戏下还留着的云端 Cookie 数，删之前先告诉管理员会连带删掉多少
        d["cookie_count"] = query_one(
            "SELECT COUNT(*) AS c FROM cloud_cookies WHERE game=?", (r["game_key"],)
        )["c"]
        d["builtin"] = bool(r["builtin"])
        d["visible"] = bool(r["visible"])
        d["mine"] = r["created_by"] == admin["id"]
        out.append(d)
    return {"ok": True, "games": out}


@router.delete("/admin/games/{game_key}")
def admin_delete_game(game_key: str, admin: dict = Depends(require_admin)):
    """后台：删除一个用户自定义的游戏名。

    删掉后客户端的游戏列表里就不会再有它（/api/cloud/games 是查 cloud_games 表的）。
    内置游戏不允许删（否则会破坏原神/星铁这些核心功能）。

    连带删除该游戏下所有云端 Cookie —— 留着的话账号列表会出现「游戏名不存在」的孤儿记录。
    """
    row = query_one("SELECT game_key, name, builtin FROM cloud_games WHERE game_key=?", (game_key,))
    if not row:
        raise HTTPException(404, "游戏不存在")
    if row["builtin"]:
        raise HTTPException(400, "内置游戏不能删除")

    cookies = query_one(
        "SELECT COUNT(*) AS c FROM cloud_cookies WHERE game=?", (game_key,)
    )["c"]
    execute("DELETE FROM cloud_cookies WHERE game=?", (game_key,))
    execute("DELETE FROM cloud_games WHERE game_key=?", (game_key,))
    # 留痕（与 admin.py 的 _audit 写同一张 logs 表；_audit 是那个模块私有的，这里自己写）
    execute(
        "INSERT INTO logs (user_id, username, action, detail, ip_address, created_at) VALUES (?,?,?,?,?,?)",
        (admin["id"], admin["username"], "delete_cloud_game",
         f"{row['name']}（{game_key}），连带删除 {cookies} 条云端 Cookie", "",
         datetime.now().isoformat()),
    )
    return {"ok": True, "deleted_cookies": cookies}


@router.get("/admin/stats")
def admin_stats(admin: dict = Depends(require_admin)):
    total = query_one("SELECT COUNT(*) AS c FROM cloud_cookies")["c"]
    valid = query_one("SELECT COUNT(*) AS c FROM cloud_cookies WHERE valid = 1")["c"]
    users = query_one("SELECT COUNT(DISTINCT user_id) AS c FROM cloud_cookies")["c"]
    logs = query_one("SELECT COUNT(*) AS c FROM cloud_login_logs")["c"]
    return {"total": total, "valid": valid, "users": users, "logs": logs}


@router.get("/admin/accounts")
def admin_accounts(admin: dict = Depends(require_admin)):
    rows = query_all(
        "SELECT c.id, c.user_id, c.game, c.combo_token, c.updated_at, c.valid, c.checked_at, "
        "c.game_uid, c.nickname, c.free_duration, c.total_duration, c.data, "
        "COALESCE(NULLIF(u.nickname,''), u.username, '（已注销用户）') AS user_name, u.username "
        "FROM cloud_cookies c LEFT JOIN users u ON u.id = c.user_id ORDER BY c.updated_at DESC")
    out = []
    names = game_name_map()
    for r in rows:
        d = dict(r)
        blob = d.pop("data", None)
        d["game_name"] = names.get(r["game"]) or r["game"]
        d["has_combo_token"] = bool(r["combo_token"])
        d.pop("combo_token", None)
        # 后台直接看这个账号能不能长期自动续期（stoken 是否已补）
        has_stoken = False
        if r["game"] in cloud_refresh.MIHOYO_GAMES and blob:
            try:
                has_stoken = cloud_refresh.stoken_bound(r["user_id"], security.decrypt_blob(blob))
            except Exception:
                has_stoken = False
        d["has_stoken"] = has_stoken
        out.append(d)
    return {"ok": True, "accounts": out}


@router.post("/admin/check")
def admin_check(payload: dict = Body(...), admin: dict = Depends(require_admin)):
    row_id = int(payload.get("id") or 0)
    row = query_one("SELECT * FROM cloud_cookies WHERE id = ?", (row_id,))
    if not row:
        raise HTTPException(404, "记录不存在")
    cookies = security.decrypt_blob(row["data"])
    result = check_cookie_valid(cookies, row["game"], row["combo_token"] or "")
    valid_val = 1 if result.get("valid") is True else (0 if result.get("valid") is False else None)
    if valid_val is None:
        # 无法判定（网络异常 / 风控拦截）时只更新时间与资料，不覆盖原来的有效性结论。
        execute(
            "UPDATE cloud_cookies SET checked_at=?, game_uid=?, nickname=? WHERE id=?",
            (security.now_s(), result.get("game_uid") or row["game_uid"] or "",
             result.get("nickname") or row["nickname"] or "", row_id),
        )
    else:
        execute(
            "UPDATE cloud_cookies SET valid=?, checked_at=?, game_uid=?, nickname=?, free_duration=?, total_duration=? "
            "WHERE id=?",
            (valid_val, security.now_s(), result.get("game_uid") or row["game_uid"] or "",
             result.get("nickname") or row["nickname"] or "",
             result.get("free_duration"), result.get("total_duration"), row_id),
        )
    return {"ok": True, "id": row_id, "valid_state": valid_val, **result}


@router.delete("/admin/accounts/{row_id}")
def admin_delete(row_id: int, admin: dict = Depends(require_admin)):
    if not query_one("SELECT id FROM cloud_cookies WHERE id = ?", (row_id,)):
        raise HTTPException(404, "记录不存在")
    execute("DELETE FROM cloud_cookies WHERE id = ?", (row_id,))
    return {"ok": True}


@router.put("/admin/accounts/{row_id}")
def admin_assign(row_id: int, payload: dict = Body(...), admin: dict = Depends(require_admin)):
    """编辑：把该云游戏账号（Cookie 记录）分配给指定用户（可传 user_id 或 username）。"""
    row = query_one("SELECT * FROM cloud_cookies WHERE id = ?", (row_id,))
    if not row:
        raise HTTPException(404, "记录不存在")
    user_id = int(payload.get("user_id") or 0)
    username = str(payload.get("username") or "").strip()
    target = None
    if user_id:
        target = query_one("SELECT * FROM users WHERE id = ?", (user_id,))
    elif username:
        target = query_one("SELECT * FROM users WHERE username = ?", (username,))
    if not target:
        raise HTTPException(400, "指定的用户不存在")
    if target["id"] == row["user_id"]:
        return {"ok": True, "user_id": target["id"], "username": target["username"]}
    # cloud_cookies 对 (user_id, game, game_uid) 唯一：目标用户已有同 UID 记录时不允许直接改归属
    if query_one("SELECT id FROM cloud_cookies WHERE user_id = ? AND game = ? AND game_uid = ?",
                 (target["id"], row["game"], row["game_uid"] or "")):
        raise HTTPException(409, "该用户已有同一游戏、同一 UID 的云端账号，请先删除或合并后再分配")
    execute("UPDATE cloud_cookies SET user_id = ? WHERE id = ?", (target["id"], row_id))
    return {"ok": True, "user_id": target["id"], "username": target["username"]}


@router.get("/admin/users")
def admin_users(admin: dict = Depends(require_admin)):
    """分配云账号时前端用户下拉的数据源。"""
    rows = query_all("SELECT id, username, nickname, is_active FROM users ORDER BY id ASC")
    return {"ok": True, "users": [dict(r) for r in rows]}


@router.get("/admin/logs")
def admin_logs(admin: dict = Depends(require_admin)):
    rows = query_all(
        "SELECT l.id, l.game, l.login_at, l.client_ip, "
        "COALESCE(NULLIF(u.nickname,''), u.username, '（已注销用户）') AS user_name, u.username "
        "FROM cloud_login_logs l LEFT JOIN users u ON u.id = l.user_id "
        "ORDER BY l.login_at DESC LIMIT 100")
    return {"ok": True, "logs": [dict(r) for r in rows]}