"""统一数据库层（SQLite）。

一个共享连接 + 锁，兼容 FastAPI 线程池与 WebSocket 的使用方式。
表结构合并自三套旧服务：Steam 分发、云游戏 Cookie、聊天室；另加离线同步表。
"""
from __future__ import annotations

import sqlite3
import threading
from contextlib import contextmanager
from typing import Any, Iterable, Optional

from .config import DB_PATH

_lock = threading.RLock()
_conn: Optional[sqlite3.Connection] = None


def get_conn() -> sqlite3.Connection:
    global _conn
    if _conn is None:
        _conn = sqlite3.connect(str(DB_PATH), check_same_thread=False)
        _conn.row_factory = sqlite3.Row
        _conn.executescript(
            "PRAGMA journal_mode=WAL;"
            "PRAGMA synchronous=NORMAL;"
            "PRAGMA foreign_keys=ON;"
            "PRAGMA busy_timeout=5000;"
        )
        _conn.commit()
    return _conn


@contextmanager
def cursor(commit: bool = True):
    """线程安全的游标上下文，默认提交。"""
    with _lock:
        conn = get_conn()
        cur = conn.cursor()
        try:
            yield cur
            if commit:
                conn.commit()
        finally:
            cur.close()


def query_one(sql: str, params: Iterable[Any] = ()) -> Optional[sqlite3.Row]:
    with cursor(commit=False) as cur:
        cur.execute(sql, tuple(params))
        return cur.fetchone()


def query_all(sql: str, params: Iterable[Any] = ()) -> list[sqlite3.Row]:
    with cursor(commit=False) as cur:
        cur.execute(sql, tuple(params))
        return cur.fetchall()


def execute(sql: str, params: Iterable[Any] = ()) -> int:
    """执行写语句，返回 lastrowid（无则 0）。"""
    with cursor() as cur:
        cur.execute(sql, tuple(params))
        return cur.lastrowid or 0


def execute_update(sql: str, params: Iterable[Any] = ()) -> int:
    """执行写语句，返回受影响行数（用于 UPDATE/DELETE）。"""
    with cursor() as cur:
        cur.execute(sql, tuple(params))
        return cur.rowcount


SCHEMA = """
-- ============ 统一账号 ============
CREATE TABLE IF NOT EXISTS users (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT UNIQUE NOT NULL,
    password_hash TEXT NOT NULL,
    nickname      TEXT DEFAULT '',
    avatar        TEXT DEFAULT '',
    email         TEXT DEFAULT '',
    is_admin      INTEGER DEFAULT 0,
    is_active     INTEGER DEFAULT 1,
    created_at    INTEGER NOT NULL,
    last_login_at INTEGER,
    reg_ip        TEXT DEFAULT ''
);

-- 会话（供聊天 Web 前端 cookie 登录；客户端走 JWT）
CREATE TABLE IF NOT EXISTS sessions (
    token      TEXT PRIMARY KEY,
    user_id    INTEGER NOT NULL,
    device_id  TEXT NOT NULL,
    ua         TEXT DEFAULT '',
    ip         TEXT DEFAULT '',
    created_at INTEGER NOT NULL,
    last_seen  INTEGER NOT NULL
);

-- ============ Steam 账号分发 ============
CREATE TABLE IF NOT EXISTS accounts (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    account_name TEXT NOT NULL,
    eya_token    TEXT NOT NULL,
    steam_id     TEXT,
    status       TEXT DEFAULT 'available',   -- available / in_use / expired
    assigned_to  TEXT,
    persona_name TEXT,
    checked_at   TEXT,
    check_result TEXT,
    created_at   TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at   TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS logs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER,
    username   TEXT,
    action     TEXT NOT NULL,
    detail     TEXT,
    ip_address TEXT,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP
);

-- ============ 云游戏 Cookie ============
CREATE TABLE IF NOT EXISTS cloud_cookies (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id       INTEGER NOT NULL,
    game          TEXT NOT NULL,
    data          BLOB NOT NULL,
    combo_token   TEXT,
    updated_at    INTEGER,
    valid         INTEGER,
    checked_at    INTEGER,
    game_uid      TEXT,
    nickname      TEXT,
    free_duration INTEGER,
    total_duration INTEGER,
    UNIQUE(user_id, game)
);

CREATE TABLE IF NOT EXISTS cloud_login_logs (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id   INTEGER,
    game      TEXT,
    login_at  INTEGER,
    client_ip TEXT
);

-- ============ 聊天室 ============
CREATE TABLE IF NOT EXISTS messages (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    room         TEXT NOT NULL,
    from_id      INTEGER NOT NULL,
    to_id        INTEGER,
    type         TEXT NOT NULL,
    content      TEXT DEFAULT '',
    file_path    TEXT,
    file_name    TEXT,
    file_size    INTEGER,
    mentions     TEXT DEFAULT '',
    recalled     INTEGER DEFAULT 0,
    recalled_at  INTEGER,
    created_at   INTEGER NOT NULL,
    -- 离线同步：来源机器标识 + 本机消息号 + 是否已同步
    install_id   TEXT DEFAULT '',
    client_msg_id TEXT DEFAULT '',
    synced       INTEGER DEFAULT 1
);
CREATE INDEX IF NOT EXISTS idx_msg_room ON messages(room, id);
CREATE INDEX IF NOT EXISTS idx_msg_created ON messages(created_at);
-- 幂等去重：同一台机器的同一条本机消息只入库一次
CREATE UNIQUE INDEX IF NOT EXISTS uq_msg_client ON messages(install_id, client_msg_id)
    WHERE install_id <> '' AND client_msg_id <> '';

CREATE TABLE IF NOT EXISTS online (
    conn_id      TEXT PRIMARY KEY,
    user_id      INTEGER NOT NULL,
    device_id    TEXT NOT NULL,
    ip           TEXT DEFAULT '',
    ua           TEXT DEFAULT '',
    connected_at INTEGER NOT NULL,
    last_seen    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_online_user ON online(user_id);

CREATE TABLE IF NOT EXISTS read_state (
    user_id      INTEGER NOT NULL,
    room         TEXT NOT NULL,
    last_read_id INTEGER DEFAULT 0,
    updated_at   INTEGER NOT NULL,
    PRIMARY KEY (user_id, room)
);

CREATE TABLE IF NOT EXISTS stickers (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path   TEXT NOT NULL,
    file_name   TEXT DEFAULT '',
    file_size   INTEGER DEFAULT 0,
    hash        TEXT UNIQUE,
    uploader_id INTEGER,
    send_count  INTEGER DEFAULT 0,
    active      INTEGER DEFAULT 1,
    created_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS invite_codes (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    code       TEXT UNIQUE NOT NULL,
    max_uses   INTEGER NOT NULL DEFAULT 1,
    used_count INTEGER NOT NULL DEFAULT 0,
    note       TEXT DEFAULT '',
    active     INTEGER DEFAULT 1,
    created_by INTEGER,
    created_at INTEGER NOT NULL
);

-- ============ 云存档 ============
CREATE TABLE IF NOT EXISTS cloud_saves (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL,
    game       TEXT NOT NULL,                 -- pvz / minecraft
    name       TEXT NOT NULL,                 -- 显示名（默认按创建时间，如 2026年10月11日14:32）
    remark     TEXT DEFAULT '',               -- 备注（共享时跟随此字段）
    file_path  TEXT NOT NULL,
    file_name  TEXT DEFAULT '',
    file_size  INTEGER DEFAULT 0,
    shared     INTEGER DEFAULT 0,
    shared_at  INTEGER,
    source     TEXT DEFAULT 'auto',           -- auto / drag
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_save_user ON cloud_saves(user_id, game);
CREATE INDEX IF NOT EXISTS idx_save_shared ON cloud_saves(game, shared);

-- ============ 系统配置 ============
CREATE TABLE IF NOT EXISTS settings (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at INTEGER
);
"""


def init_db() -> None:
    """建表 + 默认管理员。"""
    from .security import hash_password

    with cursor() as cur:
        cur.executescript(SCHEMA)

    from .config import ADMIN_USERNAME, ADMIN_PASSWORD, INVITE_REQUIRED_DEFAULT
    from . import security

    row = query_one("SELECT id FROM users WHERE username = ?", (ADMIN_USERNAME,))
    if not row:
        execute(
            "INSERT INTO users (username, password_hash, nickname, is_admin, created_at) "
            "VALUES (?, ?, ?, 1, ?)",
            (ADMIN_USERNAME, security.hash_password(ADMIN_PASSWORD), ADMIN_USERNAME, security.now_ms()),
        )
    # 默认邀请码配置
    if not query_one("SELECT key FROM settings WHERE key = 'invite_config'"):
        execute(
            "INSERT INTO settings (key, value, updated_at) VALUES ('invite_config', ?, ?)",
            (f'{{"required": {"true" if INVITE_REQUIRED_DEFAULT else "false"}}}', security.now_ms()),
        )