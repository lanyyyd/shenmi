"""云游戏账号每日定时有效性检测。

后台「云游戏管理」页可以设置每天几点几分执行一次；到点后逐条调用
cloud.check_account_row（与手动「检测有效性」同一套逻辑），顺序执行、
每条间隔 1 秒，避免集中请求触发米哈游风控。
"""
from __future__ import annotations

import logging
import threading
import time
from datetime import datetime

from . import security
from .db import execute, query_all, query_one

log = logging.getLogger("cloud-schedule")

_started = False


def _setting(key: str, default: str = "") -> str:
    row = query_one("SELECT value FROM settings WHERE key=?", (key,))
    return row["value"] if row else default


def _set_setting(key: str, value: str) -> None:
    execute(
        "INSERT INTO settings (key, value, updated_at) VALUES (?,?,?) "
        "ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at",
        (key, value, security.now_ms()),
    )


def run_once() -> None:
    """检测全部云端 Cookie，结果写回 cloud_cookies。"""
    from .routers import cloud

    rows = query_all("SELECT * FROM cloud_cookies ORDER BY id")
    ok = bad = unknown = 0
    for row in rows:
        try:
            val = cloud.check_account_row(row).get("valid_state")
            if val == 1:
                ok += 1
            elif val == 0:
                bad += 1
            else:
                unknown += 1
        except Exception as e:
            unknown += 1
            log.warning("定时检测 %s 失败：%s", row["id"], e)
        time.sleep(1)
    _set_setting("cloud_daily_check_last", datetime.now().strftime("%Y-%m-%d %H:%M"))
    log.info("云游戏定时检测完成：共 %d 条，有效 %d，失效 %d，未判定 %d", len(rows), ok, bad, unknown)


def _loop() -> None:
    while True:
        try:
            if _setting("cloud_daily_check_enabled", "0") == "1":
                at = _setting("cloud_daily_check_at", "04:30")
                try:
                    hh, mm = [int(x) for x in at.split(":")]
                except Exception:
                    hh, mm = 4, 30
                now = datetime.now()
                target = now.replace(hour=hh, minute=mm, second=0, microsecond=0)
                today = now.strftime("%Y-%m-%d")
                # 用日期前缀判断今天是否已经跑过；先写占位，避免执行期间被重复触发
                if now >= target and not _setting("cloud_daily_check_last", "").startswith(today):
                    _set_setting("cloud_daily_check_last", today + " 进行中")
                    run_once()
        except Exception as e:
            log.warning("云游戏定时检测调度异常：%s", e)
        time.sleep(30)


def start_background() -> None:
    global _started
    if _started:
        return
    _started = True
    threading.Thread(target=_loop, name="cloud-schedule", daemon=True).start()
