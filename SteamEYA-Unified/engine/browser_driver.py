"""
浏览器驱动模块（支持 Edge / Chrome / 360极速浏览器）
负责：启动浏览器、注入 Cookie、抓取 Cookie、抓取 combo_token、登录状态检测
"""
import json
import logging
import os
import time
from urllib.parse import urlparse
from typing import Optional

import requests
from selenium import webdriver
from selenium.webdriver.edge.options import Options as EdgeOptions
from selenium.webdriver.chrome.options import Options as ChromeOptions
from selenium.webdriver.support.ui import WebDriverWait
from selenium.webdriver.support import expected_conditions as EC
from selenium.webdriver.common.by import By

logger = logging.getLogger(__name__)

# 支持的云游戏
# vendor: mihoyo（米哈游）/ wanmei（完美世界）
GAMES = {
    "genshin": {
        "name": "原神",
        "url": "https://ys.mihoyo.com/cloud/#/",
        "biz": "hk4e_cn",
        "vendor": "mihoyo",
        "home": "https://ys.mihoyo.com/",
        "cookie_domains": ("mihoyo.com", "hoyoverse.com"),
    },
    "starrail": {
        "name": "星穹铁道",
        "url": "https://sr.mihoyo.com/cloud/#/",
        "biz": "hkrpg_cn",
        "vendor": "mihoyo",
        "home": "https://sr.mihoyo.com/",
        "cookie_domains": ("mihoyo.com", "hoyoverse.com"),
    },
    "yihuan": {
        "name": "异环",
        "url": "https://yh.wanmei.com/cloud/index.html",
        "biz": "yh",
        "vendor": "wanmei",
        "home": "https://id.wanmei.com/",
        "cookie_domains": ("wanmei.com", "wmupd.com"),
    },
    # 哔哩哔哩（视频网站，不是云游戏）：用提前抓取的 Cookie 免登录打开
    "bilibili": {
        "name": "哔哩哔哩",
        "url": "https://www.bilibili.com/",
        "biz": "",
        "vendor": "bilibili",
        "home": "https://www.bilibili.com/",
        "cookie_domains": ("bilibili.com",),
    },
}
CLOUD_GENSHIN_URL = GAMES["genshin"]["url"]  # 兼容旧引用
MIHOYO_HOME = "https://ys.mihoyo.com/"
COOKIE_DOMAIN_KEYWORDS = ("mihoyo.com", "mihoyo", "hoyoverse.com")

# 哔哩哔哩登录态校验接口：code=0 且 data.isLogin=true 表示 Cookie 有效
BILI_NAV_API = "https://api.bilibili.com/x/web-interface/nav"

# 完美世界（异环云游戏）登录态检测接口
# 注意：cloud/game/getUserInfo 需要先有 userId（登录后才拿到），缺参数会返回 400 BAD_REQUEST；
#       cloud/game/login 只靠 Cookie 鉴权、无需 userId，是判断登录态的正确入口。
WANMEI_LOGIN_API = "https://id.wanmei.com/cloud/game/login"
WANMEI_INITCONFIG_API = "https://id.wanmei.com/cloud/game/initConfig"
WANMEI_USERINFO_API = "https://id.wanmei.com/cloud/game/getUserInfo"

# 米哈游云游戏钱包接口（查询剩余免费时长，需要抓到的 x-rpc-combo_token）
# 返回结构：data.free_time.free_time（剩余免费分钟）、data.play_card.short_msg、data.coin.coin_num
# retcode=0 成功；retcode=-100 combo_token 失效
MIHOYO_CLOUD_WALLET = {
    "genshin": {
        "url": "https://api-cloudgame.mihoyo.com/hk4e_cg_cn/wallet/wallet/get",
        "host": "api-cloudgame.mihoyo.com",
        "referer": "https://app.mihoyo.com/",
        "origin": "https://ys.mihoyo.com",
    },
    "starrail": {
        "url": "https://cg-hkrpg-api.mihoyo.com/hkrpg_cn/cg/wallet/wallet/get?cost_method=0",
        "host": "cg-hkrpg-api.mihoyo.com",
        "referer": "https://sr.mihoyo.com/",
        "origin": "https://sr.mihoyo.com",
    },
}


def _get_mihoyo_free_duration(combo_token: str, game: str) -> dict:
    """调用米哈游云游戏钱包接口，查询剩余免费时长（分钟）
    返回 {"ok": bool, "free_duration": int|None, "total_duration": int|None,
          "play_card": str, "coin": int|None, "expired": bool, "msg": str}
    """
    cfg = MIHOYO_CLOUD_WALLET.get(game)
    if not cfg:
        return {"ok": False, "msg": f"{GAMES.get(game, {}).get('name', game)} 暂不支持云游戏钱包查询"}
    if not combo_token:
        return {"ok": False, "msg": "缺少 combo_token，无法查询免费时长"}

    headers = {
        "Host": cfg["host"],
        "Accept": "*/*",
        "Referer": cfg["referer"],
        "Origin": cfg["origin"],
        "x-rpc-combo_token": combo_token,
        "Accept-Encoding": "gzip, deflate",
        "User-Agent": UA,
    }
    if game == "starrail":
        headers.update({
            "x-rpc-client_type": "3",
            "x-rpc-cg_game_biz": "hkrpg_cn",
            "x-rpc-cg_game_id": "9000096",
            "x-rpc-vendor_id": "2",
            "x-rpc-op_biz": "clgm_hkrpg-cn",
            "x-rpc-language": "zh-cn",
            "Connection": "Keep-Alive",
        })
    try:
        data = requests.get(cfg["url"], headers=headers, timeout=12).json()
    except Exception as e:
        return {"ok": False, "msg": f"请求失败：{e}"}

    retcode = data.get("retcode")
    if retcode == -100:
        return {"ok": False, "expired": True, "msg": "combo_token 已失效，请重新抓取"}
    if retcode != 0:
        return {"ok": False, "msg": f"{data.get('message', '查询失败')}（retcode={retcode}）"}

    d = data.get("data") or {}
    ft = d.get("free_time") or {}
    try:
        free_duration = int(ft.get("free_time"))
    except (TypeError, ValueError):
        free_duration = None
    return {
        "ok": True,
        "free_duration": free_duration,
        "total_duration": None,
        "play_card": (d.get("play_card") or {}).get("short_msg", ""),
        "coin": (d.get("coin") or {}).get("coin_num"),
        "msg": "查询成功",
    }


def _wanmei_params() -> dict:
    """完美世界接口参数（严格对齐官网前端常量）
    - Tt=10607(appId) jt=1289(oneAppId) Rt=22(PC) Pt=6800(channelId)
    - deviceId 需为 yhweb_ + 8位十六进制；deviceType 为字符串枚举；deviceSys=6
    - sdkVersion 需带真实值（window.WelinkCloudGameVersion），留空会 400；env 取 URL 参数 env，默认 0
    """
    return {
        "appId": "10607",
        "deviceId": "yhweb_5f3a9c21",
        "bid": "com.pwrd.cloud.yh.laohuweb",
        "provider": "0",
        "channelId": "6800",
        "version": "1.1.5",
        "sdkVersion": "5.15.3.260622023945-wlweb-release",
        "env": "0",
        "deviceModel": "Chrome/120.0.0.0 Safari/537.36",
        "deviceSys": "6",
        "deviceType": "Windows",
        "deviceName": "Chrome/120.0.0.0",
        "oneAppId": "1289",
        "onePlatformId": "22",
        "t": str(int(time.time() * 1000)),
    }

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")

# 支持的浏览器类型
BROWSER_EDGE = "edge"
BROWSER_CHROME = "chrome"
BROWSER_360 = "360"

# 360极速浏览器常见安装路径
_360_PATHS = [
    os.path.expandvars(r"%LOCALAPPDATA%\360Chrome\Chrome\Application\360chrome.exe"),
    os.path.expandvars(r"%PROGRAMFILES(X86)%\360\360Chrome\Chrome\Application\360chrome.exe"),
    os.path.expandvars(r"%PROGRAMFILES%\360\360Chrome\Chrome\Application\360chrome.exe"),
]


def find_360_path() -> Optional[str]:
    """查找 360极速浏览器可执行文件路径"""
    for p in _360_PATHS:
        if os.path.exists(p):
            return p
    return None


def _build_common_args(options, headless: bool, user_data_dir: Optional[str],
                       capture_perf: bool, detach: bool):
    """为 Edge/Chrome 选项添加通用参数"""
    options.add_argument("--disable-blink-features=AutomationControlled")
    options.add_experimental_option("excludeSwitches", ["enable-automation"])
    options.add_experimental_option("useAutomationExtension", False)

    options.add_argument("--no-first-run")
    options.add_argument("--no-default-browser-check")
    options.add_argument("--disable-popup-blocking")
    options.add_argument("--start-maximized")
    options.add_argument("--disable-extensions")

    if headless:
        options.add_argument("--headless=new")

    if user_data_dir:
        options.add_argument(f"--user-data-dir={user_data_dir}")

    if detach:
        options.add_experimental_option("detach", True)

    if capture_perf:
        options.set_capability("goog:loggingPrefs", {"performance": "ALL"})


def launch_browser(
    browser_type: str = BROWSER_EDGE,
    user_data_dir: Optional[str] = None,
    headless: bool = False,
    capture_perf: bool = False,
    detach: bool = False,
) -> webdriver.Remote:
    """
    启动浏览器
    browser_type: "edge" / "chrome" / "360"
    Selenium 4 自带 Selenium Manager，自动下载匹配的驱动
    """
    if browser_type == BROWSER_EDGE:
        options = EdgeOptions()
        _build_common_args(options, headless, user_data_dir, capture_perf, detach)
        driver = webdriver.Edge(options=options)

    elif browser_type == BROWSER_CHROME:
        options = ChromeOptions()
        _build_common_args(options, headless, user_data_dir, capture_perf, detach)
        driver = webdriver.Chrome(options=options)

    elif browser_type == BROWSER_360:
        exe_path = find_360_path()
        if not exe_path:
            raise RuntimeError(
                "未找到 360极速浏览器，请手动确认安装路径。\n"
                "常见路径：C:\\Users\\你的用户名\\AppData\\Local\\360Chrome\\Chrome\\Application\\360chrome.exe"
            )
        options = ChromeOptions()
        options.binary_location = exe_path
        _build_common_args(options, headless, user_data_dir, capture_perf, detach)
        driver = webdriver.Chrome(options=options)

    else:
        raise ValueError(f"不支持的浏览器类型: {browser_type}")

    # 移除 webdriver 标记
    try:
        driver.execute_cdp_cmd(
            "Page.addScriptToEvaluateOnNewDocument",
            {"source": "Object.defineProperty(navigator, 'webdriver', {get: () => undefined});"},
        )
    except Exception:
        pass

    driver.set_page_load_timeout(30)
    return driver


def inject_cookies(driver: webdriver.Remote, cookies: list, game: str = "genshin", url: str | None = None) -> None:
    """向浏览器注入 Cookie（必须先访问目标域名一次）"""
    cfg = _resolve_cfg(game, url)
    driver.get(cfg.get("home") or url or MIHOYO_HOME)
    time.sleep(1)

    for cookie in cookies:
        cookie_dict = {
            "name": cookie["name"],
            "value": cookie["value"],
        }
        if cookie.get("domain"):
            cookie_dict["domain"] = cookie["domain"]
        if cookie.get("path"):
            cookie_dict["path"] = cookie["path"]
        if cookie.get("secure") is not None:
            cookie_dict["secure"] = cookie["secure"]
        if cookie.get("httpOnly") is not None:
            cookie_dict["httpOnly"] = cookie["httpOnly"]
        if cookie.get("expiry"):
            cookie_dict["expiry"] = cookie["expiry"]
        try:
            driver.add_cookie(cookie_dict)
        except Exception as e:
            logger.debug(f"跳过 Cookie {cookie['name']}: {e}")


def _resolve_cfg(game: str, url: str | None = None) -> dict:
    """取游戏配置：内置查表，自定义按传入地址推导（域名用于 Cookie 过滤）。"""
    cfg = GAMES.get(game)
    if cfg:
        return cfg

    host = ""
    if url:
        try:
            host = (urlparse(url).netloc or "").lower()
        except Exception:
            host = ""
    return {
        "name": game,
        "url": url or "",
        "vendor": "custom",
        "home": url or "",
        "biz": "",
        "cookie_domains": (host,) if host else (),
    }


def navigate_to_game(driver: webdriver.Remote, game: str = "genshin", url: str | None = None) -> None:
    """跳转到指定游戏页面；自定义游戏直接用它自己的地址。"""
    cfg = _resolve_cfg(game, url)
    target = url or cfg.get("url") or GAMES["genshin"]["url"]
    driver.get(target)


def navigate_to_cloud_genshin(driver: webdriver.Remote) -> None:
    """兼容旧调用：跳转云原神"""
    navigate_to_game(driver, "genshin")


def refresh_cookies(cookies: list, game: str = "genshin") -> dict:
    """
    尝试用 login_ticket 刷新 Cookie（延长有效期），无需重新扫码登录。

    原理：米游社提供 getMultiTokenByLoginTicket 接口，
    用 Cookie 里的 login_ticket + account_id 换取新的 cookie_token / ltoken。

    返回 {"ok": bool, "cookies": [...], "msg": str, "updated": [字段名]}
    """
    cfg = _resolve_cfg(game)
    if cfg.get("vendor") != "mihoyo":
        return {"ok": False, "msg": f"{cfg['name']} 使用会话 Cookie，不支持 login_ticket 续期",
                "cookies": cookies, "updated": []}

    cmap = {c.get("name"): c.get("value") for c in cookies if c.get("name")}
    login_ticket = cmap.get("login_ticket")
    uid = (cmap.get("stuid") or cmap.get("account_id")
           or cmap.get("ltuid") or cmap.get("ltuid_v2"))
    stoken = cmap.get("stoken", "")

    if not login_ticket:
        return {"ok": False, "msg": "Cookie 中没有 login_ticket，无法自动续期"}
    if not uid:
        return {"ok": False, "msg": "Cookie 中没有 account_id / stuid，无法自动续期"}

    url = "https://api-takumi.mihoyo.com/auth/api/getMultiTokenByLoginTicket"
    params = {"login_ticket": login_ticket, "uid": uid, "token_types": "3"}
    headers = {
        "User-Agent": UA,
        "Referer": "https://webstatic.mihoyo.com/",
        "Accept": "application/json, text/plain, */*",
    }
    jar = {"login_ticket": login_ticket, "stuid": uid, "stoken": stoken}
    try:
        data = requests.get(url, params=params, headers=headers, cookies=jar, timeout=12).json()
    except Exception as e:
        return {"ok": False, "msg": f"请求失败：{e}"}

    if data.get("retcode") != 0:
        return {"ok": False, "msg": f"续期失败：{data.get('message')}（retcode={data.get('retcode')}）"}

    tokens = (data.get("data") or {}).get("list") or []
    if not tokens:
        return {"ok": False, "msg": "续期接口未返回新 token"}

    # token_type: 1 = cookie_token, 2 = ltoken
    mapping = {}
    for t in tokens:
        tt = t.get("token_type")
        tok = t.get("token")
        if not tok:
            continue
        if tt == 1:
            mapping["cookie_token"] = tok
        elif tt == 2:
            mapping["ltoken"] = tok

    if not mapping:
        return {"ok": False, "msg": "续期接口返回的 token 类型无法识别"}

    # 写回 Cookie，兼容 v1 / v2 命名
    new_cookies = []
    updated = []
    for c in cookies:
        name = c.get("name")
        value = c.get("value")
        if name in ("cookie_token", "cookie_token_v2") and "cookie_token" in mapping:
            value = mapping["cookie_token"]
            updated.append(name)
        elif name in ("ltoken", "ltoken_v2") and "ltoken" in mapping:
            value = mapping["ltoken"]
            updated.append(name)
        c2 = dict(c)
        c2["value"] = value
        new_cookies.append(c2)

    # 原 Cookie 里若缺少对应字段则补上
    existing = {c.get("name") for c in new_cookies}
    for n, v in mapping.items():
        if n not in existing:
            new_cookies.append({"name": n, "value": v, "domain": ".mihoyo.com", "path": "/"})
            updated.append(n)

    return {"ok": True, "cookies": new_cookies, "msg": "续期成功", "updated": sorted(set(updated))}


def check_cookie_valid(cookies: list, game: str = "genshin", combo_token: str = "") -> dict:
    """
    检测 Cookie 是否有效，并返回账号信息
    combo_token：米哈游云游戏 token（可选），用于额外查询剩余免费时长
    返回 {"valid": True/False/None, "msg": str, "nickname": str, "game_uid": str, ...}
    """
    cfg = GAMES.get(game, GAMES["genshin"])
    cmap = {c.get("name"): c.get("value") for c in cookies if c.get("name")}
    if not cmap:
        return {"valid": None, "msg": "Cookie 为空"}

    if cfg.get("vendor") == "wanmei":
        return _check_wanmei_valid(cmap)
    if cfg.get("vendor") == "bilibili":
        return _check_bilibili_valid(cmap)

    cookie_str = "; ".join(f"{k}={v}" for k, v in cmap.items())
    url = f"https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz={cfg['biz']}"
    headers = {
        "Cookie": cookie_str,
        "User-Agent": UA,
        "Referer": "https://webstatic.mihoyo.com/",
        "Accept": "application/json, text/plain, */*",
    }
    try:
        data = requests.get(url, headers=headers, timeout=12).json()
    except Exception as e:
        return {"valid": None, "msg": f"请求失败：{e}"}

    retcode = data.get("retcode")
    if retcode == 0:
        roles = (data.get("data") or {}).get("list") or []
        result = {
            "valid": True, "msg": "Cookie 有效",
            "nickname": roles[0].get("nickname", "") if roles else "",
            "game_uid": str(roles[0].get("game_uid", "")) if roles else "",
            "level": roles[0].get("level") if roles else None,
            "region": roles[0].get("region_name", "") if roles else "",
        }
        if not roles:
            result["msg"] = "Cookie 有效（未查询到角色）"
        # 附加：用 combo_token 查询云游戏剩余免费时长（失败不影响 Cookie 有效性判定）
        if combo_token:
            wr = _get_mihoyo_free_duration(combo_token, game)
            if wr.get("ok"):
                result["free_duration"] = wr["free_duration"]
                result["total_duration"] = wr.get("total_duration")
                if wr.get("free_duration") is not None:
                    result["msg"] = f"Cookie 有效，剩余免费时长 {wr['free_duration']} 分钟"
        return result
    if retcode in (-100, -101, 10001, -110):
        return {"valid": False, "msg": data.get("message", "Cookie 已失效")}
    return {"valid": False, "msg": f"{data.get('message', '验证失败')}（retcode={retcode}）"}


def _find_user_id(obj, depth: int = 0) -> int:
    """递归在返回 JSON 里找 userId/uid（前端字段名随版本变化，故不写死）"""
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


def _check_wanmei_valid(cmap: dict) -> dict:
    """检测完美世界（异环）Cookie
    login 判登录态（10004＝失效）→ 取 userId → getUserInfo 拿剩余免费时长
    """
    cookie_str = "; ".join(f"{k}={v}" for k, v in cmap.items())
    headers = {
        "Cookie": cookie_str,
        "User-Agent": UA,
        "Referer": "https://yh.wanmei.com/",
        "Origin": "https://yh.wanmei.com",
        "Accept": "application/json, text/javascript, */*; q=0.01",
        "X-Requested-With": "XMLHttpRequest",
    }
    valid = None
    uid = 0
    fail_msg = ""

    try:
        data = requests.post(WANMEI_LOGIN_API, data=_wanmei_params(),
                             headers=headers, timeout=12).json()
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
            d2 = requests.get(WANMEI_INITCONFIG_API, params=_wanmei_params(),
                              headers=headers, timeout=12).json()
            if d2.get("code") == 0:
                uid = _find_user_id(d2.get("result"))
        except Exception:
            pass

    free_duration = total_duration = None
    if uid:
        try:
            u = requests.get(WANMEI_USERINFO_API,
                             params={**_wanmei_params(), "userId": str(uid)},
                             headers=headers, timeout=12).json()
            ucode = u.get("code")
            if ucode == 0:
                res = u.get("result") or {}
                valid = True
                uid = res.get("userId") or uid
                free_duration = res.get("remainedFreeDuration")
                total_duration = res.get("remainedDuration")
            elif ucode == 10004:
                return {"valid": False, "msg": u.get("message", "登录态失效")}
        except Exception:
            pass

    if valid:
        msg = "Cookie 有效"
        if free_duration is not None:
            msg += f"，剩余免费时长 {free_duration} 分钟"
        return {"valid": True, "msg": msg, "game_uid": str(uid or ""),
                "nickname": "", "free_duration": free_duration,
                "total_duration": total_duration}
    return {"valid": None, "msg": fail_msg or "无法确定登录态"}


def _check_bilibili_valid(cmap: dict) -> dict:
    """检测哔哩哔哩 Cookie 是否有效
    nav 接口：code=0 且 data.isLogin=true 表示有效，同时能拿到昵称与 mid
    """
    if not (cmap.get("SESSDATA") or cmap.get("DedeUserID")):
        return {"valid": False, "msg": "Cookie 中缺少 SESSDATA / DedeUserID，未登录"}
    cookie_str = "; ".join(f"{k}={v}" for k, v in cmap.items())
    headers = {
        "Cookie": cookie_str,
        "User-Agent": UA,
        "Referer": "https://www.bilibili.com/",
        "Accept": "application/json, text/plain, */*",
    }
    try:
        data = requests.get(BILI_NAV_API, headers=headers, timeout=12).json()
    except Exception as e:
        return {"valid": None, "msg": f"请求失败：{e}"}

    code = data.get("code")
    d = data.get("data") or {}
    if code == 0 and d.get("isLogin"):
        level = (d.get("level_info") or {}).get("current_level")
        return {
            "valid": True,
            "msg": "Cookie 有效",
            "nickname": d.get("uname", ""),
            "game_uid": str(d.get("mid", "") or ""),
            "level": level,
            "region": "",
            "free_duration": None,
        }
    if code == -101:
        return {"valid": False, "msg": "账号未登录，Cookie 已失效"}
    return {"valid": False, "msg": f"{data.get('message', '验证失败')}（code={code}）"}


def capture_cookies(driver: webdriver.Remote, game: str = "genshin", url: str | None = None) -> list:
    """抓取浏览器中该游戏对应域名下的 Cookie。

    自定义游戏用地址里的域名过滤；地址也没给（理论上不会）才退回全量，
    否则用户辛苦登录后的 Cookie 会被过滤成空数组。
    """
    cfg = _resolve_cfg(game, url)
    kws = cfg.get("cookie_domains") or ()
    all_cookies = driver.get_cookies()
    if not kws:
        return all_cookies
    return [c for c in all_cookies if any(kw in (c.get("domain") or "") for kw in kws)]


def capture_mihoyo_cookies(driver: webdriver.Remote) -> list:
    """兼容旧调用：抓取米哈游 Cookie"""
    return capture_cookies(driver, "genshin")


_INTERCEPT_JS = """
window.__captured_combo_token = '';
(function(){
  const origFetch = window.fetch;
  window.fetch = function(input, init){
    try {
      if (init && init.headers) {
        const h = init.headers;
        if (h instanceof Headers) {
          h.forEach(function(v, k){
            if (k.toLowerCase() === 'x-rpc-combo_token' && v) window.__captured_combo_token = v;
          });
        } else {
          Object.keys(h).forEach(function(k){
            if (k.toLowerCase() === 'x-rpc-combo_token' && h[k]) window.__captured_combo_token = h[k];
          });
        }
      }
    } catch(e){}
    return origFetch.apply(this, arguments);
  };
  const origSetHeader = XMLHttpRequest.prototype.setRequestHeader;
  XMLHttpRequest.prototype.setRequestHeader = function(name, value){
    try {
      if (name.toLowerCase() === 'x-rpc-combo_token' && value) window.__captured_combo_token = value;
    } catch(e){}
    return origSetHeader.apply(this, arguments);
  };
})();
"""


def capture_combo_token(driver: webdriver.Remote, timeout: int = 30) -> str:
    """通过 JS 拦截 fetch/XHR 请求头捕获 X-Rpc-Combo_token"""
    try:
        driver.execute_cdp_cmd(
            "Page.addScriptToEvaluateOnNewDocument",
            {"source": _INTERCEPT_JS},
        )
    except Exception as e:
        logger.debug(f"注入拦截脚本失败: {e}")

    try:
        driver.refresh()
    except Exception:
        pass

    deadline = time.time() + timeout
    token = ""
    while time.time() < deadline:
        try:
            val = driver.execute_script("return window.__captured_combo_token || '';")
            if val:
                token = val
                break
        except Exception:
            pass
        time.sleep(1)

    if not token:
        try:
            logs = driver.get_log("performance")
            for entry in logs:
                try:
                    msg = json.loads(entry["message"])["message"]
                    if msg["method"] == "Network.requestWillBeSentExtraInfo":
                        headers = msg["params"].get("headers", {})
                        for k, v in headers.items():
                            if k.lower() == "x-rpc-combo_token" and v:
                                token = v
                                break
                    if token:
                        break
                except Exception:
                    continue
        except Exception:
            pass

    return token


def _check_wanmei_login(driver: webdriver.Remote, timeout: int = 15) -> bool:
    """在页面上下文用 Cookie 调 cloud/game/login 判断登录态（完美世界）"""
    body = "&".join(f"{k}={v}" for k, v in _wanmei_params().items())
    js = (
        "var cb = arguments[arguments.length - 1];"
        f"fetch('{WANMEI_LOGIN_API}', {{method: 'POST', credentials: 'include',"
        "headers: {'Content-Type': 'application/x-www-form-urlencoded'},"
        f"body: '{body}'}})"
        ".then(function(r){return r.json();})"
        ".then(function(d){cb(d && d.code);})"
        ".catch(function(e){cb(-1);});"
    )
    try:
        driver.set_script_timeout(timeout)
        code = driver.execute_async_script(js)
        return code == 0
    except Exception:
        return False


def _check_bilibili_login(driver: webdriver.Remote, timeout: int = 15) -> bool:
    """在页面上下文用 Cookie 调 nav 接口判断 B站登录态"""
    js = (
        "var cb = arguments[arguments.length - 1];"
        f"fetch('{BILI_NAV_API}', {{credentials: 'include'}})"
        ".then(function(r){return r.json();})"
        ".then(function(d){cb(!!(d && d.code === 0 && d.data && d.data.isLogin));})"
        ".catch(function(e){cb(false);});"
    )
    try:
        driver.set_script_timeout(timeout)
        return bool(driver.execute_async_script(js))
    except Exception:
        return False


def check_login_status(driver: webdriver.Remote, timeout: int = 15, game: str = "genshin") -> bool:
    """检测云游戏页面是否已登录"""
    cfg = GAMES.get(game, GAMES["genshin"])
    if cfg.get("vendor") == "wanmei":
        return _check_wanmei_login(driver, timeout)
    if cfg.get("vendor") == "bilibili":
        return _check_bilibili_login(driver, timeout)

    try:
        WebDriverWait(driver, timeout).until(
            EC.presence_of_element_located((By.TAG_NAME, "body"))
        )
        time.sleep(2)

        page_text = driver.find_element(By.TAG_NAME, "body").text
        login_keywords = ["扫码登录", "请登录", "登录后", "二维码登录", "短信登录", "密码登录"]
        has_login_prompt = any(kw in page_text for kw in login_keywords)

        logged_in_keywords = ["开始游戏", "进入游戏", "我的钱包", "游戏时长", "退出登录", "uid"]
        has_logged_in = any(kw in page_text for kw in logged_in_keywords)

        if has_login_prompt and not has_logged_in:
            return False
        if has_logged_in:
            return True

        try:
            result = driver.execute_script(
                "return fetch('/account/auth/info').then(r=>r.json()).catch(e=>({}))"
            )
            if isinstance(result, dict) and result.get("retcode") == 0:
                return True
        except Exception:
            pass

        return not has_login_prompt
    except Exception:
        return False
