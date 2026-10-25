"""积分账户公共操作（供积分 / 抽卡 / 红包等多处复用）。

保证「余额 + point_logs 明细」始终成对写入，口径与积分系统一致。
"""
from __future__ import annotations

from . import security
from .db import execute, execute_update, query_one


def balance(user_id: int) -> int:
    row = query_one("SELECT balance FROM point_accounts WHERE user_id = ?", (user_id,))
    return int(row["balance"]) if row else 0


def add_points(user_id: int, delta: int, type_: str, ref: str = "", remark: str = "") -> int:
    """加/减积分并记一条明细，返回变动后余额。"""
    now = security.now_ms()
    if query_one("SELECT user_id FROM point_accounts WHERE user_id = ?", (user_id,)):
        execute_update("UPDATE point_accounts SET balance = balance + ?, updated_at = ? WHERE user_id = ?",
                       (delta, now, user_id))
    else:
        execute("INSERT INTO point_accounts (user_id, balance, updated_at) VALUES (?, ?, ?)",
                (user_id, delta, now))
    bal = balance(user_id)
    execute("INSERT INTO point_logs (user_id, delta, balance_after, type, ref, remark, created_at) "
            "VALUES (?,?,?,?,?,?,?)",
            (user_id, delta, bal, type_, ref or "", remark or "", now))
    return bal