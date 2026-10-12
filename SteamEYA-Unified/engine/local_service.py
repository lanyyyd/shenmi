"""云游戏引擎本地服务（Windows 侧运行）。

把原 tkinter 客户端里的 Selenium 能力（抓/注入 Cookie、有效性检测、续期）
封装为一个本地 HTTP 服务，供 WinUI 客户端通过 127.0.0.1 调用。

设计要点：
- 保留浏览器抓取/注入的原始逻辑（browser_driver.py），不改动核心算法；
- 每个游戏独立 profile，可同时上号多个游戏；
- 数据目录（浏览器 profile）默认放在 exe/脚本同级目录，可用 EYA_ENGINE_DATA 覆盖到非 C 盘。

启动：python local_service.py --port 6971
"""
from __future__ import annotations

import argparse
import os
import sys
import threading

from fastapi import Body, FastAPI
from fastapi.middleware.cors import CORSMiddleware

# 数据目录：优先环境变量，其次 exe/脚本所在目录
def _base_dir() -> str:
    env = os.getenv("EYA_ENGINE_DATA")
    if env:
        os.makedirs(env, exist_ok=True)
        return env
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


BASE_DIR = _base_dir()
_driver_lock = threading.RLock()
_capture_drivers: dict = {}
_login_drivers: dict = {}


def _drv():
    """延迟导入 browser_driver（未安装 selenium 时也能起服务并给出 health）。"""
    import browser_driver  # noqa: WPS433
    return browser_driver


def _capture_profile(game: str) -> str:
    return os.path.join(BASE_DIR, f"capture_profile_{game}")


def _login_profile(game: str) -> str:
    return os.path.join(BASE_DIR, f"login_profile_{game}")


app = FastAPI(title="SteamEYA 云游戏引擎", version="1.0.0")
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_credentials=False,
                   allow_methods=["*"], allow_headers=["*"])


@app.get("/engine/health")
def health():
    try:
        games = _drv().GAMES
        ready = True
    except Exception:
        games, ready = {}, False
    return {"ok": True, "selenium_ready": ready,
            "games": [{"key": k, "name": v["name"], "url": v["url"], "vendor": v.get("vendor")}
                      for k, v in games.items()]}


def _get_capture_driver(game: str, browser: str):
    d = _capture_drivers.get(game)
    if d is not None:
        try:
            _ = d.current_url
            return d, True
        except Exception:
            _capture_drivers.pop(game, None)
    d = _drv().launch_browser(browser_type=browser, user_data_dir=_capture_profile(game), capture_perf=True)
    _capture_drivers[game] = d
    return d, False


def _get_login_driver(game: str, browser: str):
    d = _login_drivers.get(game)
    if d is not None:
        try:
            _ = d.current_url
            return d, True
        except Exception:
            _login_drivers.pop(game, None)
    d = _drv().launch_browser(browser_type=browser, user_data_dir=_login_profile(game), detach=True)
    _login_drivers[game] = d
    return d, False


@app.post("/engine/browser/open")
def browser_open(payload: dict = Body(...)):
    game = payload.get("game") or "genshin"
    browser = payload.get("browser") or "edge"
    with _driver_lock:
        try:
            drv = _drv()
            driver, reused = _get_capture_driver(game, browser)
            if not reused:
                drv.navigate_to_game(driver, game)
            return {"ok": True, "reused": reused, "name": drv.GAMES.get(game, {}).get("name", game)}
        except Exception as e:
            return {"ok": False, "msg": str(e)}


@app.post("/engine/capture")
def capture(payload: dict = Body(...)):
    game = payload.get("game") or "genshin"
    with _driver_lock:
        drv = _drv()
        driver = _capture_drivers.get(game)
        if driver is None:
            return {"ok": False, "msg": f"请先为「{drv.GAMES.get(game, {}).get('name', game)}」打开浏览器"}
        try:
            cookies = drv.capture_cookies(driver, game)
            combo = ""
            if drv.GAMES.get(game, {}).get("vendor") == "mihoyo":
                combo = drv.capture_combo_token(driver, timeout=30) or ""
            return {"ok": True, "cookies": cookies, "combo_token": combo, "count": len(cookies)}
        except Exception as e:
            return {"ok": False, "msg": str(e)}


@app.post("/engine/login")
def login(payload: dict = Body(...)):
    game = payload.get("game") or "genshin"
    browser = payload.get("browser") or "edge"
    cookies = payload.get("cookies") or []
    if not cookies:
        return {"ok": False, "msg": "缺少 Cookie"}
    with _driver_lock:
        try:
            drv = _drv()
            driver, reused = _get_login_driver(game, browser)
            drv.inject_cookies(driver, cookies, game)
            drv.navigate_to_game(driver, game)
            import time
            time.sleep(3)
            ok = drv.check_login_status(driver, timeout=15, game=game)
            return {"ok": True, "logged_in": bool(ok), "reused": reused}
        except Exception as e:
            return {"ok": False, "msg": str(e)}


@app.post("/engine/check")
def check(payload: dict = Body(...)):
    game = payload.get("game") or "genshin"
    cookies = payload.get("cookies") or []
    combo = payload.get("combo_token") or ""
    try:
        return {"ok": True, **_drv().check_cookie_valid(cookies, game, combo)}
    except Exception as e:
        return {"ok": False, "msg": str(e)}


@app.post("/engine/refresh")
def refresh(payload: dict = Body(...)):
    game = payload.get("game") or "genshin"
    cookies = payload.get("cookies") or []
    try:
        return {"ok": True, **_drv().refresh_cookies(cookies, game)}
    except Exception as e:
        return {"ok": False, "msg": str(e)}


if __name__ == "__main__":
    import uvicorn
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=6971)
    args = parser.parse_args()
    uvicorn.run(app, host=args.host, port=args.port)