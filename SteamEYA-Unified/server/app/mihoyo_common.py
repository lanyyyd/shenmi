"""米哈游 Cookie 解析工具（抽卡链接等功能共用）。

云游戏 Cookie 的自动续期已经移除，这里只保留纯解析函数。
"""
from __future__ import annotations

import hashlib

# 只有这两个内置游戏走米哈游账号体系。
MIHOYO_GAMES = ("genshin", "starrail")


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
