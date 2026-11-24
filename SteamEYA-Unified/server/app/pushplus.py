"""PushPlus 个人好友绑定与云游戏失效提醒。

绑定流程：
  1. 客户端请求个人二维码，服务端生成一次性 nonce 写入 push_bind_nonces；
  2. 调 pushplus 开放接口「获取个人二维码」，把 nonce 作为 content 参数；
  3. 用户微信扫码后添加服务端 pushplus 账号为好友，pushplus 回调 add_friend，
     带回同一个 nonce 和好友令牌 token；
  4. 服务端把好友令牌加密后绑到 user_id，之后按好友维度推送。

配置复用 settings 表的 push_config（管理后台填写）：
  token     —— pushplus 用户 token（发送消息用）
  secretKey —— pushplus 开发设置里的 secretKey（开放接口换 AccessKey 用）
开放接口默认关闭，需要在 pushplus 后台开启，并把本服务器 IP 加入安全 IP 列表。
"""
from __future__ import annotations

import html
import json
import logging
import threading
import time
import uuid

import requests

from . import security
from .db import execute, execute_update, query_one

log = logging.getLogger("pushplus")

OPEN_API = "https://www.pushplus.plus/api/open"
# 注意：换取 AccessKey 的地址不在 /api/open 下，官方文档为 /api/common/openApi/getAccessKey。
ACCESS_KEY_API = "https://www.pushplus.plus/api/common/openApi/getAccessKey"
SEND_API = "https://www.pushplus.plus/send"

# 二维码有效期（秒）与扫码次数：一次性绑定，扫过就失效。
QR_TTL_SECONDS = 600
QR_SCAN_COUNT = 1

# AccessKey 有效期 2 小时，提前 5 分钟刷新。
ACCESS_SAFE_SECONDS = 300

_lock = threading.Lock()
_access_key = ""
_access_expire_at = 0.0


def config() -> dict:
    row = query_one("SELECT value FROM settings WHERE key = 'push_config'")
    if not row:
        return {}
    try:
        cfg = json.loads(row["value"])
        return cfg if isinstance(cfg, dict) else {}
    except Exception:
        return {}


def configured() -> bool:
    """发送消息和开放接口都配置好才允许生成二维码。"""
    cfg = config()
    return bool(str(cfg.get("token") or "").strip() and str(cfg.get("secretKey") or "").strip())


def _access_key_value(force: bool = False) -> str:
    global _access_key, _access_expire_at
    cfg = config()
    token = str(cfg.get("token") or "").strip()
    secret = str(cfg.get("secretKey") or "").strip()
    if not token or not secret:
        raise RuntimeError("管理员尚未配置 PushPlus Token / SecretKey")

    now = time.time()
    with _lock:
        if not force and _access_key and now < _access_expire_at - ACCESS_SAFE_SECONDS:
            return _access_key
        try:
            resp = requests.post(
                ACCESS_KEY_API,
                json={"token": token, "secretKey": secret},
                timeout=12,
            ).json()
        except Exception as e:
            raise RuntimeError(f"获取 AccessKey 失败：{e}") from e
        if int(resp.get("code") or 0) != 200:
            msg = str(resp.get("msg") or "获取 AccessKey 失败")
            detail = str(resp.get("data") or "").strip()
            if detail and detail not in msg:
                msg += f"（{detail}）"
            raise RuntimeError(msg + "；请确认已开启开放接口，且服务器 IP 在安全 IP 列表中")
        data = resp.get("data") or {}
        key = str(data.get("accessKey") or "").strip()
        if not key:
            raise RuntimeError("PushPlus 未返回 AccessKey")
        _access_key = key
        _access_expire_at = now + int(data.get("expiresIn") or 7200)
        return key


def create_bind_qr(user_id: int) -> dict:
    """生成一次性绑定二维码，返回 {nonce, url, expiresIn}。"""
    if not configured():
        raise RuntimeError("管理员尚未启用 PushPlus 推送（需要 Token + SecretKey）")

    nonce = uuid.uuid4().hex
    now = int(time.time())
    # 只保留当前用户最新的一张二维码，避免旧码一直有效。
    execute_update("DELETE FROM push_bind_nonces WHERE user_id = ? OR expires_at < ?", (user_id, now))
    execute(
        "INSERT INTO push_bind_nonces (nonce, user_id, created_at, expires_at) VALUES (?,?,?,?)",
        (nonce, user_id, now, now + QR_TTL_SECONDS),
    )

    try:
        key = _access_key_value()
        resp = requests.get(
            f"{OPEN_API}/friend/getQrCode",
            params={"content": nonce, "second": QR_TTL_SECONDS, "scanCount": QR_SCAN_COUNT},
            headers={"access-key": key},
            timeout=12,
        ).json()
        if int(resp.get("code") or 0) != 200:
            raise RuntimeError(str(resp.get("msg") or "生成二维码失败"))
        url = str((resp.get("data") or {}).get("qrCodeImgUrl") or "").strip()
        if not url:
            raise RuntimeError("PushPlus 未返回二维码图片地址")
    except Exception:
        execute_update("DELETE FROM push_bind_nonces WHERE nonce = ?", (nonce,))
        raise

    return {"nonce": nonce, "url": url, "expiresIn": QR_TTL_SECONDS}


def handle_callback(payload: dict) -> None:
    """处理 pushplus 回调：add_friend 时把好友令牌绑定到发起扫码的账号。"""
    if not isinstance(payload, dict) or payload.get("event") != "add_friend":
        return
    nonce = str(payload.get("qrCode") or "").strip()
    info = payload.get("friendInfo") or {}
    token = str(info.get("token") or "").strip()
    if not nonce or not token:
        return

    row = query_one("SELECT * FROM push_bind_nonces WHERE nonce = ?", (nonce,))
    now = int(time.time())
    if not row or int(row["expires_at"] or 0) < now:
        log.warning("PushPlus 绑定回调忽略：票据不存在或已过期")
        return

    execute_update("DELETE FROM push_bind_nonces WHERE nonce = ?", (nonce,))
    blob = security.encrypt_blob(token)
    nickname = str(info.get("nickName") or "").strip()[:64]
    execute(
        "INSERT INTO push_bindings (user_id, friend_token, nickname, enabled, bound_at, updated_at) "
        "VALUES (?,?,?,?,?,?) "
        "ON CONFLICT(user_id) DO UPDATE SET friend_token=excluded.friend_token, "
        "nickname=excluded.nickname, enabled=1, updated_at=excluded.updated_at",
        (row["user_id"], blob, nickname, 1, now, now),
    )
    log.info("用户 %s 已绑定 PushPlus 推送", row["user_id"])


def status(user_id: int) -> dict:
    row = query_one(
        "SELECT nickname, enabled, bound_at FROM push_bindings WHERE user_id = ?",
        (user_id,),
    )
    return {
        "configured": configured(),
        "bound": bool(row),
        "enabled": bool(row["enabled"]) if row else False,
        "nickname": (row["nickname"] or "") if row else "",
        "boundAt": int(row["bound_at"] or 0) if row else 0,
    }


def set_enabled(user_id: int, enabled: bool) -> bool:
    return execute_update(
        "UPDATE push_bindings SET enabled = ?, updated_at = ? WHERE user_id = ?",
        (1 if enabled else 0, int(time.time()), user_id),
    ) > 0


def unbind(user_id: int) -> None:
    execute_update("DELETE FROM push_bindings WHERE user_id = ?", (user_id,))


def _friend_token(user_id: int) -> str:
    row = query_one("SELECT friend_token FROM push_bindings WHERE user_id = ? AND enabled = 1", (user_id,))
    if not row:
        return ""
    try:
        return str(security.decrypt_blob(row["friend_token"]) or "")
    except Exception:
        return ""


def send_to_user(user_id: int, title: str, content_html: str) -> bool:
    """给已绑定的用户发一条好友消息；未绑定/未启用/未配置时静默返回 False。"""
    cfg = config()
    token = str(cfg.get("token") or "").strip()
    friend = _friend_token(user_id)
    if not token or not friend:
        return False
    try:
        resp = requests.post(
            SEND_API,
            json={
                "token": token,
                "to": friend,
                "title": title,
                "content": content_html,
                "template": "html",
            },
            timeout=12,
        ).json()
        if int(resp.get("code") or 0) != 200:
            log.warning("PushPlus 推送失败：%s", resp.get("msg"))
            return False
        return True
    except Exception as e:
        log.warning("PushPlus 推送异常：%s", e)
        return False


def notify_expired(user_id: int, game_name: str, account: str) -> None:
    """云游戏 Cookie 失效提醒（后台线程发送，不阻塞检测）。"""
    title = "云游戏账号失效提醒"
    content = (
        "<b>云游戏 Cookie 已失效</b><br>"
        f"游戏：{html.escape(game_name or '未知游戏')}<br>"
        f"账号：{html.escape(account or '未命名账号')}<br><br>"
        "请打开 SteamEYA 客户端 → 云游戏，重新更新 Cookie 后再上号。"
    )
    send_to_user(user_id, title, content)


def notify_expired_async(user_id: int, game_name: str, account: str) -> None:
    threading.Thread(
        target=notify_expired,
        args=(user_id, game_name, account),
        name="pushplus-expired",
        daemon=True,
    ).start()


def send_test(user_id: int) -> bool:
    content = (
        "<b>绑定成功</b><br>"
        "这是一条测试消息。之后云游戏 Cookie 被每日检测判定为失效时，会通过微信提醒你。"
    )
    return send_to_user(user_id, "推送绑定测试", content)
