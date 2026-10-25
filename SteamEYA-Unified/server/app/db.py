"""统一数据库层（SQLite）。

一个共享连接 + 锁，兼容 FastAPI 线程池与 WebSocket 的使用方式。
表结构合并自三套旧服务：Steam 分发、云游戏 Cookie、聊天室；另加离线同步表。
"""
from __future__ import annotations

import json
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
    is_ai         INTEGER DEFAULT 0,          -- 1 = 「小火人」AI 助手账号
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
-- 云游戏列表：内置 4 个由服务端 seed，用户可自行添加（上传成功后才对所有人可见）
CREATE TABLE IF NOT EXISTS cloud_games (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    game_key   TEXT UNIQUE NOT NULL,
    name       TEXT NOT NULL,
    url        TEXT NOT NULL DEFAULT '',
    vendor     TEXT NOT NULL DEFAULT 'custom',
    biz        TEXT NOT NULL DEFAULT '',
    builtin    INTEGER NOT NULL DEFAULT 0,
    visible    INTEGER NOT NULL DEFAULT 0,   -- 0=仅创建者可见，1=所有人可见
    created_by INTEGER,
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_cloud_games_visible ON cloud_games(visible, id);

CREATE TABLE IF NOT EXISTS cloud_cookies (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id       INTEGER NOT NULL,
    game          TEXT NOT NULL,
    game_uid      TEXT NOT NULL DEFAULT '',
    data          BLOB NOT NULL,
    combo_token   TEXT,
    updated_at    INTEGER,
    valid         INTEGER,
    checked_at    INTEGER,
    nickname      TEXT,
    free_duration INTEGER,
    total_duration INTEGER,
    UNIQUE(user_id, game, game_uid)
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

-- 云存档的游戏目录：内置 3 个由服务端 seed，用户可自行添加（同名自动复用同一个 key，
-- 这样不同用户给同一款游戏建的条目会合并，共享存档才能互相看见）。
CREATE TABLE IF NOT EXISTS save_games (
    game_key   TEXT PRIMARY KEY,
    name       TEXT NOT NULL,
    save_dir   TEXT NOT NULL DEFAULT '',   -- 创建者填的本机存档目录，随存档一起下发
    visible    INTEGER NOT NULL DEFAULT 0, -- 0=仅创建者可见，1=首个上传成功后对所有人可见
    created_by INTEGER,
    created_at INTEGER NOT NULL
);

-- ============ 客户端更新管理 ============
-- 管理员在后台「更新管理」上传的安装包；active=1 的那条即当前下发版本。
CREATE TABLE IF NOT EXISTS app_releases (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    version        TEXT NOT NULL,             -- 版本号，如 0.1.42
    notes          TEXT DEFAULT '',           -- 更新内容（多行文本）
    file_path      TEXT NOT NULL,             -- /uploads/releases/xxx.exe
    file_name      TEXT DEFAULT '',
    file_size      INTEGER DEFAULT 0,
    sha256         TEXT DEFAULT '',
    platform       TEXT DEFAULT 'windows',    -- windows / android（各自只有一个 active）
    active         INTEGER DEFAULT 1,
    download_count INTEGER DEFAULT 0,
    created_at     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_release_active ON app_releases(active, id);

-- ============ 导航 tips 气泡 ============
-- 后台手动添加的短句：weight 越大被抽中的概率越高（默认 10）；
-- 一言 API 作为一个虚拟条目单独计权重（存 settings，默认 1），因此手动内容天然占多数。
CREATE TABLE IF NOT EXISTS tips (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    content    TEXT NOT NULL,
    weight     INTEGER NOT NULL DEFAULT 10,
    active     INTEGER NOT NULL DEFAULT 1,
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tips_active ON tips(active, id);

-- ============ 积分系统 ============
CREATE TABLE IF NOT EXISTS point_accounts (
    user_id    INTEGER PRIMARY KEY,
    balance    INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL
);

-- 积分明细（每次变动一条；balance_after 便于对账）
CREATE TABLE IF NOT EXISTS point_logs (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id       INTEGER NOT NULL,
    delta         INTEGER NOT NULL,           -- 变动：+收入 / -支出
    balance_after INTEGER NOT NULL,
    type          TEXT NOT NULL,              -- signin / redeem / transfer_out / transfer_in / admin
    ref           TEXT DEFAULT '',            -- 商品名 / 对方用户名 / 签到日期
    remark        TEXT DEFAULT '',
    created_at    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_plog_user ON point_logs(user_id, id);

-- 积分商城商品（后台可增删改，支持图片）
CREATE TABLE IF NOT EXISTS point_items (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    description TEXT DEFAULT '',
    image_path  TEXT DEFAULT '',
    price       INTEGER NOT NULL DEFAULT 0,
    stock       INTEGER NOT NULL DEFAULT -1,  -- -1 表示不限量
    active      INTEGER DEFAULT 1,
    created_at  INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS point_redemptions (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL,
    item_id    INTEGER NOT NULL,
    item_name  TEXT DEFAULT '',
    price      INTEGER NOT NULL,
    status     TEXT DEFAULT 'redeemed',       -- redeemed / issued
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_redeem_user ON point_redemptions(user_id, id);

-- 每日签到（user_id + 北京时间日期 唯一，保证「每天首次」只发一次）
CREATE TABLE IF NOT EXISTS daily_signin (
    user_id    INTEGER NOT NULL,
    day        TEXT NOT NULL,                 -- 北京时间 YYYY-MM-DD
    points     INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, day)
);

-- 订单兑现内容：管理员在后台对某条兑换订单点「已兑现」时写入的文字说明
-- （每个订单一条，重复提交为覆盖更新；用户端「我的订单」随时可看）
CREATE TABLE IF NOT EXISTS point_order_replies (
    redemption_id INTEGER PRIMARY KEY,
    content       TEXT DEFAULT '',
    admin_id      INTEGER,
    created_at    INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
);

-- 订单兑现时随订单一起下发给用户的图片 / 视频（一个订单可多条，可单独删除）
CREATE TABLE IF NOT EXISTS point_order_files (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    redemption_id INTEGER NOT NULL,
    kind          TEXT NOT NULL,              -- image / video
    file_path     TEXT NOT NULL,              -- /uploads/orders/xxx
    file_name     TEXT DEFAULT '',
    file_size     INTEGER DEFAULT 0,
    created_at    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_orderfile_rid ON point_order_files(redemption_id, id);

-- ============ 等级 / 经验系统 ============
-- 用户经验余额：等级由经验按阈值推导，不单独存等级。
CREATE TABLE IF NOT EXISTS user_exp (
    user_id    INTEGER PRIMARY KEY,
    exp        INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL
);

-- 经验明细（每次变动一条；exp_after 便于对账）
CREATE TABLE IF NOT EXISTS exp_logs (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id   INTEGER NOT NULL,
    delta     INTEGER NOT NULL,
    exp_after INTEGER NOT NULL,
    source    TEXT NOT NULL,              -- signin / daily_login / group_msg / pm_msg / upload / redeem / transfer / admin / init
    ref       TEXT DEFAULT '',            -- 签到日期 / 商品名 / 对方用户名 / 文件名等
    remark    TEXT DEFAULT '',
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_explog_user ON exp_logs(user_id, id);

-- 每日经验累计（按来源）：用于「每天最多获得多少经验」的上限判断
CREATE TABLE IF NOT EXISTS daily_exp_gain (
    user_id INTEGER NOT NULL,
    day     TEXT NOT NULL,                -- 北京时间 YYYY-MM-DD
    source  TEXT NOT NULL,
    gained  INTEGER NOT NULL DEFAULT 0,   -- 当天该来源已获得经验
    cnt     INTEGER NOT NULL DEFAULT 0,   -- 当天该来源已发放次数
    PRIMARY KEY (user_id, day, source)
);

-- ============ 抽卡 / 扭蛋 ============
-- 卡池（名字与抽卡价格后台可改；保底按卡池分开累计）
CREATE TABLE IF NOT EXISTS gacha_pools (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT NOT NULL,
    cost1      INTEGER NOT NULL DEFAULT 0,    -- 单抽消耗积分
    cost10     INTEGER NOT NULL DEFAULT 0,    -- 十连消耗积分
    sort       INTEGER NOT NULL DEFAULT 0,
    active     INTEGER NOT NULL DEFAULT 1,
    created_at INTEGER NOT NULL
);

-- 卡池奖品：类型决定奖励内容（积分/经验/实物），稀有度决定概率档位
CREATE TABLE IF NOT EXISTS gacha_prizes (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    pool_id     INTEGER NOT NULL,
    kind        TEXT NOT NULL,                -- points / exp / physical
    rarity      TEXT NOT NULL,                -- normal / premium / rare / limited
    name        TEXT DEFAULT '',
    description TEXT DEFAULT '',
    image_path  TEXT DEFAULT '',
    value       INTEGER NOT NULL DEFAULT 0,   -- 积分 / 经验数量
    stock       INTEGER NOT NULL DEFAULT -1,  -- 实物库存，-1 表示不限
    weight      INTEGER NOT NULL DEFAULT 1,   -- 同档内权重
    active      INTEGER NOT NULL DEFAULT 1,
    created_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_gacha_prize ON gacha_prizes(pool_id, rarity, active);

-- 每个用户在每个卡池的保底状态（永久累计、按卡池分开）
CREATE TABLE IF NOT EXISTS gacha_state (
    user_id       INTEGER NOT NULL,
    pool_id       INTEGER NOT NULL,
    pity5         INTEGER NOT NULL DEFAULT 0,  -- 距上次稀有已抽数
    pity4         INTEGER NOT NULL DEFAULT 0,  -- 距上次精品以上已抽数
    guaranteed    INTEGER NOT NULL DEFAULT 0,  -- 大保底：下次稀有必为限定
    total_draws   INTEGER NOT NULL DEFAULT 0,
    rare_total    INTEGER NOT NULL DEFAULT 0,
    limited_total INTEGER NOT NULL DEFAULT 0,
    updated_at    INTEGER NOT NULL,
    PRIMARY KEY (user_id, pool_id)
);

-- 抽卡记录
CREATE TABLE IF NOT EXISTS gacha_logs (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id           INTEGER NOT NULL,
    pool_id           INTEGER NOT NULL,
    prize_id          INTEGER,
    kind              TEXT DEFAULT '',
    rarity            TEXT DEFAULT '',
    name              TEXT DEFAULT '',
    value             INTEGER DEFAULT 0,
    image_path        TEXT DEFAULT '',
    is_limited        INTEGER DEFAULT 0,
    pity5             INTEGER DEFAULT 0,       -- 本次结算后的 pity5
    pull_no           INTEGER DEFAULT 0,       -- 若出稀有，是第几抽出的（1..90）
    guaranteed_before INTEGER DEFAULT 0,
    created_at        INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_gacha_log_user ON gacha_logs(user_id, id);
CREATE INDEX IF NOT EXISTS idx_gacha_log_pool ON gacha_logs(pool_id, id);

-- ============ 群红包 ============
-- 资产 asset：points（积分）/ exp（经验，仅管理员可发）
-- 模式 mode：random（总数量+个数，随机拆分）/ fixed（单个数量×个数）
-- 管理员发红包不扣余额（admin_free=1）；其他人发积分红包从余额扣除。
CREATE TABLE IF NOT EXISTS red_packets (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    room           TEXT NOT NULL DEFAULT 'group',
    sender_id      INTEGER NOT NULL,
    asset          TEXT NOT NULL,
    mode           TEXT NOT NULL,
    total          INTEGER NOT NULL,
    count          INTEGER NOT NULL,
    per            INTEGER NOT NULL DEFAULT 0,   -- 固定模式的单个数量
    remark         TEXT DEFAULT '',
    admin_free     INTEGER NOT NULL DEFAULT 0,
    grabbed_count  INTEGER NOT NULL DEFAULT 0,
    grabbed_amount INTEGER NOT NULL DEFAULT 0,
    refunded       INTEGER NOT NULL DEFAULT 0,   -- 过期剩余是否已退回发包者
    msg_id         INTEGER,
    expire_at      INTEGER NOT NULL,
    created_at     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_rp_sender ON red_packets(sender_id, id);
CREATE INDEX IF NOT EXISTS idx_rp_expire ON red_packets(refunded, expire_at);

-- 领取记录（每人每个红包限 1 次）
CREATE TABLE IF NOT EXISTS red_packet_grabs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    packet_id  INTEGER NOT NULL,
    user_id    INTEGER NOT NULL,
    amount     INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    UNIQUE(packet_id, user_id)
);
CREATE INDEX IF NOT EXISTS idx_rpg_user ON red_packet_grabs(user_id, id);

-- ============ 系统配置 ============
CREATE TABLE IF NOT EXISTS settings (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at INTEGER
);

-- ============ 今天吃点啥（美食） ============
-- 店铺（评分不落库，由该店所有菜品的所有评价实时聚合得到）
CREATE TABLE IF NOT EXISTS eat_stores (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT NOT NULL,
    image      TEXT DEFAULT '',
    location   TEXT DEFAULT '',    -- 位置小字，如「新餐厅 2楼」（添加店铺时填写，后台可改）
    owner_id   INTEGER,
    created_at INTEGER NOT NULL,
    image_status TEXT DEFAULT '',           -- ''/queued/running/ok/failed：AI 配图状态
    image_source TEXT DEFAULT '',           -- user / ai：图片来自用户上传还是 AI 生成
    image_error  TEXT DEFAULT '',           -- 最近一次失败原因（截断）
    image_attempts INTEGER DEFAULT 0,       -- 已重试次数
    image_generated_at INTEGER              -- AI 出图完成时间
);

CREATE TABLE IF NOT EXISTS eat_dishes (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    store_id   INTEGER NOT NULL,
    name       TEXT NOT NULL,
    image      TEXT DEFAULT '',
    price      REAL NOT NULL DEFAULT 0,
    owner_id   INTEGER,
    created_at INTEGER NOT NULL,
    image_status TEXT DEFAULT '',
    image_source TEXT DEFAULT '',
    image_error  TEXT DEFAULT '',
    image_attempts INTEGER DEFAULT 0,
    image_generated_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_eat_dish_store ON eat_dishes(store_id);

-- AI 配图队列：新建店铺/菜品无图时入队，后台线程逐条生成
CREATE TABLE IF NOT EXISTS ai_image_jobs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    kind       TEXT NOT NULL,               -- store / dish
    target_id  INTEGER NOT NULL,
    status     TEXT NOT NULL DEFAULT 'queued',  -- queued / running / ok / failed / canceled
    prompt     TEXT DEFAULT '',             -- 文本模型生成的提示词
    extra_prompt TEXT DEFAULT '',           -- 用户在编辑页补充的提示词（追加在原提示词后）
    mode       TEXT DEFAULT 'auto',         -- auto：新建自动配图；regen：编辑页重新生图
    image_url  TEXT DEFAULT '',             -- 生成后的站内地址
    error      TEXT DEFAULT '',
    attempts   INTEGER DEFAULT 0,
    created_at INTEGER NOT NULL,
    updated_at INTEGER,
    finished_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_ai_img_job_status ON ai_image_jobs(status, id);
CREATE INDEX IF NOT EXISTS idx_ai_img_job_target ON ai_image_jobs(kind, target_id, id);

-- 任务：owner_id 为 NULL 表示「全局共享任务」（所有人同一个转盘结果）
CREATE TABLE IF NOT EXISTS eat_tasks (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id   INTEGER,
    dish_id    INTEGER NOT NULL,
    store_id   INTEGER NOT NULL,
    created_at INTEGER NOT NULL
);

-- 每个用户「我要吃别的」的覆盖标记：置 1 后该用户不再跟随全局任务
CREATE TABLE IF NOT EXISTS eat_optout (
    user_id    INTEGER PRIMARY KEY,
    override   INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER
);

-- 评价：parent_id 为空是顶层评价，非空是「同评」（挂在某条评价下）
CREATE TABLE IF NOT EXISTS eat_reviews (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL,
    store_id   INTEGER,
    dish_id    INTEGER,
    parent_id  INTEGER,
    rating     INTEGER,             -- 1~5，可为空（同评可以什么都不填）
    comment    TEXT DEFAULT '',
    image      TEXT DEFAULT '',               -- 兼容旧数据：等于 images 里的第一张
    images     TEXT DEFAULT '',               -- JSON 数组：多张图片（/uploads/eat/xxx.webp）
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_eat_review_parent ON eat_reviews(parent_id, id);
CREATE INDEX IF NOT EXISTS idx_eat_review_dish ON eat_reviews(dish_id, id);

-- 点赞：一条评价每人只能点一次（再次点击即删除 → 取消点赞）
CREATE TABLE IF NOT EXISTS eat_review_likes (
    review_id  INTEGER NOT NULL,
    user_id    INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (review_id, user_id)
);

-- 「一起吃」：被勾选的小伙伴也计入该评价的月度次数/支出/最常吃等统计
CREATE TABLE IF NOT EXISTS eat_review_guests (
    review_id  INTEGER NOT NULL,
    user_id    INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (review_id, user_id)
);
CREATE INDEX IF NOT EXISTS idx_eat_guest_user ON eat_review_guests(user_id, review_id);

-- 联系人备注 / 置顶：每个用户对自己看到的联系人单独设置（不影响别人）
CREATE TABLE IF NOT EXISTS contact_notes (
    owner_id   INTEGER NOT NULL,
    peer_id    INTEGER NOT NULL,
    note       TEXT DEFAULT '',
    pinned     INTEGER DEFAULT 0,
    updated_at INTEGER,
    PRIMARY KEY (owner_id, peer_id)
);
"""


def init_db() -> None:
    """建表 + 默认管理员。"""
    from .security import hash_password

    with cursor() as cur:
        cur.executescript(SCHEMA)

    # 兼容旧库：save_games 早期只有 game_key/name，补上存档目录与可见性两列。
    cols = {row["name"] for row in query_all("PRAGMA table_info(save_games)")}
    if cols and "save_dir" not in cols:
        with cursor() as cur:
            cur.execute("ALTER TABLE save_games ADD COLUMN save_dir TEXT NOT NULL DEFAULT ''")
    if cols and "visible" not in cols:
        with cursor() as cur:
            cur.execute("ALTER TABLE save_games ADD COLUMN visible INTEGER NOT NULL DEFAULT 0")
        # 内置游戏本来就该对所有人可见。
        with cursor() as cur:
            cur.execute("UPDATE save_games SET visible=1 WHERE game_key IN ('pvz','pvzhybrid','minecraft')")

    # 兼容旧库：cloud_cookies 唯一键 (user_id, game) → (user_id, game, game_uid)，
    # 支持同一用户在同一游戏下保存多个账号（按 UID 区分）。
    trow = query_one("SELECT sql FROM sqlite_master WHERE type='table' AND name='cloud_cookies'")
    if trow and "UNIQUE(user_id, game, game_uid)" not in (trow["sql"] or ""):
        with cursor() as cur:
            cur.executescript("""
                PRAGMA foreign_keys=OFF;
                BEGIN;
                ALTER TABLE cloud_cookies RENAME TO cloud_cookies_old;
                CREATE TABLE cloud_cookies (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    game TEXT NOT NULL,
                    game_uid TEXT NOT NULL DEFAULT '',
                    data BLOB NOT NULL,
                    combo_token TEXT,
                    updated_at INTEGER,
                    valid INTEGER,
                    checked_at INTEGER,
                    nickname TEXT,
                    free_duration INTEGER,
                    total_duration INTEGER,
                    UNIQUE(user_id, game, game_uid)
                );
                INSERT INTO cloud_cookies (id, user_id, game, game_uid, data, combo_token, updated_at,
                                           valid, checked_at, nickname, free_duration, total_duration)
                SELECT id, user_id, game, COALESCE(game_uid, ''), data, combo_token, updated_at,
                       valid, checked_at, nickname, free_duration, total_duration
                FROM cloud_cookies_old;
                DROP TABLE cloud_cookies_old;
                COMMIT;
            """)

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

    # 抽卡：默认三个卡池（名字与价格后台可改）+ 默认概率配置
    if not query_one("SELECT id FROM gacha_pools"):
        for _sort, (_nm, _c1, _c10) in enumerate((("新手池", 3, 30), ("精品池", 18, 180), ("老登池", 52, 520))):
            execute(
                "INSERT INTO gacha_pools (name, cost1, cost10, sort, active, created_at) VALUES (?,?,?,?,1,?)",
                (_nm, _c1, _c10, _sort, security.now_ms()),
            )
    if not query_one("SELECT key FROM settings WHERE key = 'gacha_config'"):
        from . import gachacore
        execute(
            "INSERT INTO settings (key, value, updated_at) VALUES ('gacha_config', ?, ?)",
            (json.dumps(gachacore.DEFAULT_RATES, ensure_ascii=False), security.now_ms()),
        )

    # 兑换订单来源列：抽卡中的实物奖品复用「我的订单」发货流程
    cols = {r["name"] for r in query_all("PRAGMA table_info(point_redemptions)")}
    if "source" not in cols:
        execute("ALTER TABLE point_redemptions ADD COLUMN source TEXT DEFAULT 'mall'")

    # ---- 兼容旧库补列 ----
    # users.is_ai：老库没有这一列，新代码靠它认「小火人」账号
    ucols = {r["name"] for r in query_all("PRAGMA table_info(users)")}
    if "is_ai" not in ucols:
        execute("ALTER TABLE users ADD COLUMN is_ai INTEGER DEFAULT 0")
    # eat_reviews.images：多图（JSON 数组）
    rcols = {r["name"] for r in query_all("PRAGMA table_info(eat_reviews)")}
    if rcols and "images" not in rcols:
        execute("ALTER TABLE eat_reviews ADD COLUMN images TEXT DEFAULT ''")

    # app_releases.platform：更新管理拆「电脑端 / 手机端」，老版本记录一律视为 Windows 包
    acols = {r["name"] for r in query_all("PRAGMA table_info(app_releases)")}
    if acols and "platform" not in acols:
        execute("ALTER TABLE app_releases ADD COLUMN platform TEXT DEFAULT 'windows'")

    # ---- AI 自动配图：给店铺/菜品补状态列（老库平滑升级）----
    for _tbl in ("eat_stores", "eat_dishes"):
        _cols = {r["name"] for r in query_all(f"PRAGMA table_info({_tbl})")}
        if not _cols:
            continue
        if "image_status" not in _cols:
            execute(f"ALTER TABLE {_tbl} ADD COLUMN image_status TEXT DEFAULT ''")
        if "image_source" not in _cols:
            execute(f"ALTER TABLE {_tbl} ADD COLUMN image_source TEXT DEFAULT ''")
        if "image_error" not in _cols:
            execute(f"ALTER TABLE {_tbl} ADD COLUMN image_error TEXT DEFAULT ''")
        if "image_attempts" not in _cols:
            execute(f"ALTER TABLE {_tbl} ADD COLUMN image_attempts INTEGER DEFAULT 0")
        if "image_generated_at" not in _cols:
            execute(f"ALTER TABLE {_tbl} ADD COLUMN image_generated_at INTEGER")

    # ---- 重新生图：任务表补「用户补充提示词」与模式列 ----
    jcols = {r["name"] for r in query_all("PRAGMA table_info(ai_image_jobs)")}
    if jcols:
        if "extra_prompt" not in jcols:
            execute("ALTER TABLE ai_image_jobs ADD COLUMN extra_prompt TEXT DEFAULT ''")
        if "mode" not in jcols:
            execute("ALTER TABLE ai_image_jobs ADD COLUMN mode TEXT DEFAULT 'auto'")

    _ensure_ai_user()
    _ensure_ai_config()
    _ensure_ai_image_config()


def _ensure_ai_user() -> None:
    """建出「小火人」这个虚拟账号（is_ai=1）。

    它不参与注册/登录：密码哈希是一段不可能被校验通过的随机值；
    /api/users 会把它排除在普通联系人之外，前端单独置顶展示。
    """
    from . import security

    if query_one("SELECT id FROM users WHERE is_ai = 1"):
        return
    from . import aicore
    execute(
        "INSERT INTO users (username, password_hash, nickname, is_admin, is_active, is_ai, created_at) "
        "VALUES (?, ?, ?, 0, 1, 1, ?)",
        (aicore.AI_USERNAME, "!ai-no-login!" + security.gen_token(24),
         str(aicore.get_config().get("name") or aicore.DEFAULT_NAME), security.now_ms()),
    )


def _ensure_ai_config() -> None:
    from . import aicore
    from . import security

    if not query_one("SELECT key FROM settings WHERE key = 'ai_config'"):
        execute(
            "INSERT INTO settings (key, value, updated_at) VALUES ('ai_config', ?, ?)",
            (json.dumps(aicore.DEFAULT_CONFIG, ensure_ascii=False), security.now_ms()),
        )


def _ensure_ai_image_config() -> None:
    """AI 生图配置默认值：默认关闭，管理员在后台填好模型后再启用。"""
    from . import aigen
    from . import security

    if not query_one("SELECT key FROM settings WHERE key = 'ai_image_config'"):
        execute(
            "INSERT INTO settings (key, value, updated_at) VALUES ('ai_image_config', ?, ?)",
            (json.dumps(aigen.DEFAULT_CONFIG, ensure_ascii=False), security.now_ms()),
        )