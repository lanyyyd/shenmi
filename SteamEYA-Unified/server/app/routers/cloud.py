"""云游戏模块：Cookie 上传/下载/检测/上报（原端口 6966 的客户端接口）。

Cookie 在服务端以主密钥加密存储；检测逻辑（米哈游/完美世界/哔哩哔哩）从原实现移植。
"""
from __future__ import annotations

import time

import requests
from fastapi import APIRouter, Body, Depends, HTTPException, Request

from .. import security
from ..db import execute, query_all, query_one
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


def _check_bilibili_valid(cookie_str: str, cmap: dict) -> dict:
    if not (cmap.get("SESSDATA") or cmap.get("DedeUserID")):
        return {"valid": False, "msg": "Cookie 中缺少 SESSDATA / DedeUserID，未登录"}
    headers = {"Cookie": cookie_str, "User-Agent": UA, "Referer": "https://www.bilibili.com/",
               "Accept": "application/json, text/plain, */*"}
    try:
        data = requests.get(BILI_NAV_API, headers=headers, timeout=12).json()
    except Exception as e:
        return {"valid": None, "msg": f"请求失败：{e}"}
    code = data.get("code")
    d = data.get("data") or {}
    if code == 0 and d.get("isLogin"):
        return {"valid": True, "msg": "有效", "nickname": d.get("uname", ""),
                "game_uid": str(d.get("mid", "") or ""),
                "level": (d.get("level_info") or {}).get("current_level"), "free_duration": None}
    if code == -101:
        return {"valid": False, "msg": "账号未登录，Cookie 已失效"}
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
    cfg = GAMES.get(game, GAMES["genshin"])
    cmap = {c.get("name"): c.get("value") for c in cookies if c.get("name")}
    if not cmap:
        return {"valid": None, "msg": "Cookie 为空"}
    cookie_str = "; ".join(f"{k}={v}" for k, v in cmap.items())
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
        if combo_token:
            wr = _get_mihoyo_free_duration(combo_token, game)
            if wr.get("ok"):
                result["free_duration"] = wr["free_duration"]
                if wr.get("free_duration") is not None:
                    result["msg"] = f"有效，剩余免费时长 {wr['free_duration']} 分钟"
        return result
    if retcode in (-100, -101, 10001, -110):
        return {"valid": False, "msg": data.get("message", "Cookie 已失效")}
    return {"valid": False, "msg": f"{data.get('message', '验证失败')}（retcode={retcode}）"}


def _client_ip(request: Request) -> str:
    xf = (request.headers.get("x-forwarded-for") or "").split(",")[0].strip()
    return xf or (request.client.host if request.client else "")


@router.post("/upload")
def upload(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = payload.get("game") or "genshin"
    cookies = payload.get("cookies")
    combo_token = payload.get("combo_token") or ""
    if game not in GAMES:
        raise HTTPException(400, "未知游戏")
    if not cookies:
        raise HTTPException(400, "Cookie 数据为空")
    blob = security.encrypt_blob(cookies)
    now = security.now_s()
    execute(
        "INSERT INTO cloud_cookies (user_id, game, data, combo_token, updated_at) VALUES (?,?,?,?,?) "
        "ON CONFLICT(user_id, game) DO UPDATE SET data=excluded.data, combo_token=excluded.combo_token, "
        "updated_at=excluded.updated_at, valid=NULL, checked_at=NULL",
        (user["id"], game, blob, combo_token, now),
    )
    result = check_cookie_valid(cookies, game, combo_token)
    execute(
        "UPDATE cloud_cookies SET valid=?, checked_at=?, game_uid=?, nickname=?, free_duration=?, total_duration=? "
        "WHERE user_id=? AND game=?",
        (1 if result.get("valid") else 0, security.now_s(), result.get("game_uid", ""),
         result.get("nickname", ""), result.get("free_duration"), result.get("total_duration"),
         user["id"], game),
    )
    return {"ok": True, "msg": "上传成功", "valid": result.get("valid"),
            "nickname": result.get("nickname", ""), "detail": result.get("msg")}


@router.get("/download")
def download(game: str = "genshin", user: dict = Depends(get_current_user)):
    row = query_one("SELECT data, combo_token, updated_at FROM cloud_cookies WHERE user_id=? AND game=?",
                    (user["id"], game))
    if not row:
        raise HTTPException(404, "该账号尚未上传该游戏的 Cookie")
    try:
        cookies = security.decrypt_blob(row["data"])
    except Exception:
        raise HTTPException(500, "数据解密失败")
    return {"ok": True, "cookies": cookies, "combo_token": row["combo_token"] or "",
            "updated_at": row["updated_at"]}


@router.post("/report_login")
def report_login(request: Request, payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = payload.get("game") or "genshin"
    now = security.now_s()
    execute("UPDATE users SET last_login_at=? WHERE id=?", (now, user["id"]))
    execute("INSERT INTO cloud_login_logs (user_id, game, login_at, client_ip) VALUES (?,?,?,?)",
            (user["id"], game, now, _client_ip(request)))
    return {"ok": True}


@router.post("/check")
def check(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = payload.get("game") or "genshin"
    row = query_one("SELECT data, combo_token FROM cloud_cookies WHERE user_id=? AND game=?",
                    (user["id"], game))
    if not row:
        raise HTTPException(404, "尚未上传该游戏 Cookie")
    cookies = security.decrypt_blob(row["data"])
    result = check_cookie_valid(cookies, game, row["combo_token"] or "")
    execute(
        "UPDATE cloud_cookies SET valid=?, checked_at=?, game_uid=?, nickname=?, free_duration=?, total_duration=? "
        "WHERE user_id=? AND game=?",
        (1 if result.get("valid") else (0 if result.get("valid") is False else None),
         security.now_s(), result.get("game_uid", ""), result.get("nickname", ""),
         result.get("free_duration"), result.get("total_duration"), user["id"], game),
    )
    return {"ok": True, **result}


@router.get("/my")
def my(user: dict = Depends(get_current_user)):
    rows = query_all(
        "SELECT game, updated_at, valid, checked_at, nickname, game_uid, free_duration, total_duration "
        "FROM cloud_cookies WHERE user_id=?", (user["id"],),
    )
    return {"ok": True, "games": [dict(r) for r in rows]}


# ---------------- 管理员：云游戏账号管理 ----------------
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
        "c.game_uid, c.nickname, c.free_duration, c.total_duration, "
        "COALESCE(NULLIF(u.nickname,''), u.username, '（已注销用户）') AS user_name, u.username "
        "FROM cloud_cookies c LEFT JOIN users u ON u.id = c.user_id ORDER BY c.updated_at DESC")
    out = []
    for r in rows:
        d = dict(r)
        d["game_name"] = (GAMES.get(r["game"]) or {}).get("name", r["game"])
        d["has_combo_token"] = bool(r["combo_token"])
        d.pop("combo_token", None)
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
    valid_val = 1 if result.get("valid") else (0 if result.get("valid") is False else None)
    execute(
        "UPDATE cloud_cookies SET valid=?, checked_at=?, game_uid=?, nickname=?, free_duration=?, total_duration=? "
        "WHERE id=?",
        (valid_val, security.now_s(), result.get("game_uid", ""), result.get("nickname", ""),
         result.get("free_duration"), result.get("total_duration"), row_id),
    )
    return {"ok": True, **result}


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
    # cloud_cookies 对 (user_id, game) 唯一：目标用户已有同游戏记录时不允许直接改归属
    if query_one("SELECT id FROM cloud_cookies WHERE user_id = ? AND game = ?", (target["id"], row["game"])):
        raise HTTPException(409, "该用户已有同一游戏的云端账号，请先删除或合并后再分配")
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