"""米游社 App 扫码登录，补云游戏 Cookie 缺失的 stoken。

云游戏网页里抓到的 Cookie 只有短期 ltoken / cookie_token，没有长期 stoken，
所以自动续期用不了。这里与 MiyoQian 的做法一致：走 APP 扫码接口
（createQRLogin / queryQRLoginStatus），扫码确认后拿到 stoken，
再由 cloud_refresh 用它换票。

二维码在服务端生成 0/1 矩阵，客户端自己画格子，避免客户端再引二维码库。
"""
from __future__ import annotations

import json
import logging
import threading
import time
import uuid

import qrcode
import requests

from .cloud_refresh import ds_x4

log = logging.getLogger("mihoyo-qr")

PASSPORT_API = "https://passport-api.mihoyo.com"
CREATE_URL = f"{PASSPORT_API}/account/ma-cn-passport/app/createQRLogin"
QUERY_URL = f"{PASSPORT_API}/account/ma-cn-passport/app/queryQRLoginStatus"

# 必须用 /app/ 接口：/web/ 那套不会返回 stoken（只有 web token）。
APP_ID = "bll8iq97cem8"          # 米游社 App
APP_VERSION = "2.90.1"
GAME_BIZ = "bbs_cn"
UA = f"Mozilla/5.0 (Windows NT 10.0; Win64; x64) miHoYoBBS/{APP_VERSION} Capture/2.2.0"

# 扫码会话只活 5 分钟（官方二维码本身也是几分钟过期）
SESSION_TTL = 300

_sessions: dict[str, dict] = {}
_lock = threading.Lock()


def _headers(device_id: str, body: str) -> dict:
    return {
        "User-Agent": UA,
        "Content-Type": "application/json",
        "Accept": "application/json",
        "x-rpc-client_type": "3",
        "x-rpc-app_id": APP_ID,
        "x-rpc-app_version": APP_VERSION,
        "x-rpc-sdk_version": APP_VERSION,
        "x-rpc-account_version": APP_VERSION,
        "x-rpc-game_biz": GAME_BIZ,
        "x-rpc-device_id": device_id,
        "x-rpc-device_name": "Windows",
        "x-rpc-device_model": "PC",
        "DS": ds_x4(query="", body=body),
    }


def qr_matrix(text: str) -> list[str]:
    """把二维码转成 0/1 矩阵（每行一个字符串），客户端直接画格子。"""
    qr = qrcode.QRCode(
        error_correction=qrcode.constants.ERROR_CORRECT_M,
        box_size=1,
        border=2,
    )
    qr.add_data(text)
    qr.make(fit=True)
    return ["".join("1" if cell else "0" for cell in row) for row in qr.get_matrix()]


def _cleanup() -> None:
    now = time.time()
    for key, item in list(_sessions.items()):
        if now - item["created_at"] > SESSION_TTL:
            _sessions.pop(key, None)


def create_session(user_id: int) -> dict:
    """请求米游社生成二维码，返回会话号 + 二维码矩阵。"""
    _cleanup()
    device_id = uuid.uuid4().hex
    body = "{}"
    try:
        resp = requests.post(CREATE_URL, data=body.encode("utf-8"),
                             headers=_headers(device_id, body), timeout=15)
        data = resp.json()
    except Exception as e:
        log.warning("创建米游社扫码会话失败：%s", e)
        return {"ok": False, "msg": f"请求米游社失败：{e}"}

    if not isinstance(data, dict) or data.get("retcode") != 0:
        msg = (data.get("message") if isinstance(data, dict) else "") or "米游社接口返回异常"
        retcode = data.get("retcode") if isinstance(data, dict) else None
        return {"ok": False, "msg": f"{msg}（retcode={retcode}）"}

    payload = data.get("data") or {}
    url = str(payload.get("url") or payload.get("qr_url") or "")
    ticket = str(payload.get("ticket") or "")
    if not url or not ticket:
        return {"ok": False, "msg": "米游社未返回二维码地址或 ticket"}

    try:
        rows = qr_matrix(url)
    except Exception as e:
        return {"ok": False, "msg": f"生成二维码失败：{e}"}

    session = uuid.uuid4().hex
    with _lock:
        _sessions[session] = {
            "user_id": user_id,
            "ticket": ticket,
            "device_id": device_id,
            "created_at": time.time(),
        }
    return {"ok": True, "session": session, "url": url, "rows": rows,
            "size": len(rows[0]) if rows else 0, "expires_in": SESSION_TTL}


def poll_session(user_id: int, session: str) -> dict:
    """查询扫码状态；确认后返回 stoken / mid / stuid（由调用方入库并续期）。"""
    _cleanup()
    with _lock:
        entry = _sessions.get(session)
    if not entry:
        return {"ok": False, "state": "expired", "msg": "二维码已过期，请重新生成"}
    if entry["user_id"] != user_id:
        return {"ok": False, "state": "expired", "msg": "扫码会话不属于当前账号"}
    if time.time() - entry["created_at"] > SESSION_TTL:
        with _lock:
            _sessions.pop(session, None)
        return {"ok": False, "state": "expired", "msg": "二维码已过期，请重新生成"}

    body = json.dumps({"ticket": entry["ticket"]}, separators=(",", ":"))
    try:
        resp = requests.post(QUERY_URL, data=body.encode("utf-8"),
                             headers=_headers(entry["device_id"], body), timeout=15)
        data = resp.json()
    except Exception as e:
        return {"ok": False, "state": "error", "msg": f"查询扫码状态失败：{e}"}

    if not isinstance(data, dict):
        return {"ok": False, "state": "error", "msg": "米游社接口返回格式异常"}

    retcode = data.get("retcode")
    if retcode == -3501:
        with _lock:
            _sessions.pop(session, None)
        return {"ok": False, "state": "expired", "msg": "二维码已过期，请重新生成"}
    if retcode == -3505:
        with _lock:
            _sessions.pop(session, None)
        return {"ok": False, "state": "canceled", "msg": "已在米游社 App 取消扫码"}
    if retcode != 0:
        return {"ok": False, "state": "error",
                "msg": f"{data.get('message') or '扫码状态查询失败'}（retcode={retcode}）"}

    payload = data.get("data") or {}
    stat = str(payload.get("stat") or payload.get("status") or "").lower()
    if stat == "scanned":
        return {"ok": True, "state": "scanned", "msg": "已扫码，请在米游社 App 里点确认"}
    if stat != "confirmed":
        return {"ok": True, "state": "waiting", "msg": "请用米游社 App 扫描二维码"}

    tokens = payload.get("tokens") or []
    stoken = ""
    if isinstance(tokens, list):
        for token in tokens:
            if isinstance(token, dict) and token.get("token"):
                stoken = str(token["token"])
                break
    info = payload.get("user_info") or {}
    mid = str(info.get("mid") or "")
    stuid = str(info.get("aid") or info.get("stuid") or "")
    if not stoken or not mid:
        return {"ok": False, "state": "error", "msg": "扫码已确认，但米游社未返回 stoken / mid"}

    with _lock:
        _sessions.pop(session, None)
    log.info("米游社扫码成功：user=%s mid=%s", user_id, mid)
    return {"ok": True, "state": "confirmed", "msg": "扫码成功，已获取长期 stoken",
            "stoken": stoken, "mid": mid, "stuid": stuid,
            "device_id": entry["device_id"]}
