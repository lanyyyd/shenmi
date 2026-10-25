"""
神秘 WiFi - 数据库模型
使用 SQLite，无需额外数据库服务
"""
import sqlite3
import os
import time
import hashlib
import secrets
from config import DATABASE_PATH, DEFAULT_EXPIRE_DAYS, DEFAULT_TRAFFIC_LIMIT_GB


def get_db_path():
    os.makedirs(os.path.dirname(DATABASE_PATH), exist_ok=True)
    return DATABASE_PATH


def init_db():
    """初始化数据库，创建所有表"""
    db_path = get_db_path()
    conn = sqlite3.connect(db_path)
    c = conn.cursor()

    # 用户表
    c.execute("""
        CREATE TABLE IF NOT EXISTS users (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            username TEXT UNIQUE NOT NULL,
            password_hash TEXT NOT NULL,
            salt TEXT NOT NULL,
            created_at REAL NOT NULL,
            expire_at REAL NOT NULL,
            traffic_limit_bytes INTEGER NOT NULL DEFAULT 0,
            is_active INTEGER NOT NULL DEFAULT 1,
            is_admin INTEGER NOT NULL DEFAULT 0,
            wg_private_key TEXT,
            wg_public_key TEXT,
            wg_client_ip TEXT,
            wg_address TEXT,
            allocated_ip_index INTEGER DEFAULT 0,
            traffic_rx_total INTEGER NOT NULL DEFAULT 0,
            traffic_tx_total INTEGER NOT NULL DEFAULT 0,
            last_wg_rx INTEGER NOT NULL DEFAULT 0,
            last_wg_tx INTEGER NOT NULL DEFAULT 0
        )
    """)

    # 流量记录表（每次采集的快照）
    c.execute("""
        CREATE TABLE IF NOT EXISTS traffic_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id INTEGER NOT NULL,
            timestamp REAL NOT NULL,
            rx_bytes INTEGER NOT NULL,
            tx_bytes INTEGER NOT NULL,
            FOREIGN KEY (user_id) REFERENCES users(id)
        )
    """)

    # IP 分配表
    c.execute("""
        CREATE TABLE IF NOT EXISTS ip_allocations (
            ip_index INTEGER PRIMARY KEY,
            user_id INTEGER,
            allocated_at REAL
        )
    """)

    conn.commit()
    conn.close()

    # 迁移：为旧数据库添加新列
    _migrate_db(db_path)

    print(f"[DB] 数据库已初始化: {db_path}")


def _migrate_db(db_path):
    """为已存在的数据库添加新列（兼容旧版本）"""
    conn = sqlite3.connect(db_path)
    c = conn.cursor()

    # 检查 users 表是否需要添加新列
    c.execute("PRAGMA table_info(users)")
    existing_cols = {row[1] for row in c.fetchall()}

    new_cols = {
        "traffic_rx_total": "INTEGER NOT NULL DEFAULT 0",
        "traffic_tx_total": "INTEGER NOT NULL DEFAULT 0",
        "last_wg_rx": "INTEGER NOT NULL DEFAULT 0",
        "last_wg_tx": "INTEGER NOT NULL DEFAULT 0",
    }

    for col, col_def in new_cols.items():
        if col not in existing_cols:
            c.execute(f"ALTER TABLE users ADD COLUMN {col} {col_def}")
            print(f"[DB] 迁移：添加列 {col}")

    conn.commit()
    conn.close()


def hash_password(password: str, salt: str = None) -> tuple:
    """密码加盐哈希"""
    if salt is None:
        salt = secrets.token_hex(16)
    hashed = hashlib.pbkdf2_hmac('sha256', password.encode(), salt.encode(), 100000)
    return hashed.hex(), salt


def verify_password(password: str, password_hash: str, salt: str) -> bool:
    """验证密码"""
    hashed, _ = hash_password(password, salt)
    return hashed == password_hash


class Database:
    """数据库操作封装"""

    def __init__(self):
        self.db_path = get_db_path()

    def _get_conn(self):
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    # ==================== 用户管理 ====================

    def create_user(self, username: str, password: str, is_admin: bool = False) -> dict:
        """创建用户"""
        password_hash, salt = hash_password(password)
        now = time.time()
        expire_at = now + DEFAULT_EXPIRE_DAYS * 86400
        traffic_limit = DEFAULT_TRAFFIC_LIMIT_GB * 1024 * 1024 * 1024

        conn = self._get_conn()
        c = conn.cursor()
        try:
            c.execute(
                """INSERT INTO users 
                   (username, password_hash, salt, created_at, expire_at, 
                    traffic_limit_bytes, is_active, is_admin)
                   VALUES (?, ?, ?, ?, ?, ?, 1, ?)""",
                (username, password_hash, salt, now, expire_at, traffic_limit, 1 if is_admin else 0)
            )
            conn.commit()
            user_id = c.lastrowid
            conn.close()
            return self.get_user_by_id(user_id)
        except sqlite3.IntegrityError:
            conn.close()
            return None

    def get_user_by_id(self, user_id: int) -> dict:
        conn = self._get_conn()
        c = conn.cursor()
        c.execute("SELECT * FROM users WHERE id = ?", (user_id,))
        row = c.fetchone()
        conn.close()
        return dict(row) if row else None

    def get_user_by_username(self, username: str) -> dict:
        conn = self._get_conn()
        c = conn.cursor()
        c.execute("SELECT * FROM users WHERE username = ?", (username,))
        row = c.fetchone()
        conn.close()
        return dict(row) if row else None

    def get_all_users(self) -> list:
        conn = self._get_conn()
        c = conn.cursor()
        c.execute("SELECT * FROM users ORDER BY created_at DESC")
        rows = c.fetchall()
        conn.close()
        return [dict(r) for r in rows]

    def update_user(self, user_id: int, **kwargs):
        """更新用户字段"""
        allowed = ['expire_at', 'traffic_limit_bytes', 'is_active',
                   'wg_private_key', 'wg_public_key', 'wg_client_ip', 'wg_address',
                   'allocated_ip_index', 'traffic_rx_total', 'traffic_tx_total',
                   'last_wg_rx', 'last_wg_tx']
        sets = []
        vals = []
        for k, v in kwargs.items():
            if k in allowed:
                sets.append(f"{k} = ?")
                vals.append(v)
        if not sets:
            return
        vals.append(user_id)
        conn = self._get_conn()
        conn.execute(f"UPDATE users SET {', '.join(sets)} WHERE id = ?", vals)
        conn.commit()
        conn.close()

    def delete_user(self, user_id: int):
        conn = self._get_conn()
        conn.execute("DELETE FROM users WHERE id = ?", (user_id,))
        conn.execute("DELETE FROM traffic_logs WHERE user_id = ?", (user_id,))
        conn.commit()
        conn.close()

    # ==================== 流量记录 ====================

    def log_traffic(self, user_id: int, rx_bytes: int, tx_bytes: int):
        """记录流量快照到日志表（保留历史记录）"""
        conn = self._get_conn()
        conn.execute(
            "INSERT INTO traffic_logs (user_id, timestamp, rx_bytes, tx_bytes) VALUES (?, ?, ?, ?)",
            (user_id, time.time(), rx_bytes, tx_bytes)
        )
        conn.commit()
        conn.close()

    def update_cumulative_traffic(self, user_id: int, wg_rx: int, wg_tx: int) -> dict:
        """
        增量累加流量，处理服务器重启导致的计数器归零。
        
        wg_rx/wg_tx 是 wg show 的原始值（客户端视角：rx=下载，tx=上传）
        
        返回更新后的累计 {rx, tx, total}
        """
        user = self.get_user_by_id(user_id)
        if not user:
            return {"rx": 0, "tx": 0, "total": 0}

        last_rx = user.get("last_wg_rx", 0)
        last_tx = user.get("last_wg_tx", 0)
        cum_rx = user.get("traffic_rx_total", 0)
        cum_tx = user.get("traffic_tx_total", 0)

        # 计算增量
        # 如果当前值 >= 上次值：正常增量
        # 如果当前值 < 上次值：说明 WireGuard 重启，当前值就是重启后的新流量
        if wg_rx >= last_rx:
            delta_rx = wg_rx - last_rx
        else:
            delta_rx = wg_rx  # 重启后从头开始

        if wg_tx >= last_tx:
            delta_tx = wg_tx - last_tx
        else:
            delta_tx = wg_tx  # 重启后从头开始

        # 累加到持久化总量
        new_cum_rx = cum_rx + delta_rx
        new_cum_tx = cum_tx + delta_tx

        # 更新数据库
        self.update_user(user_id,
                         traffic_rx_total=new_cum_rx,
                         traffic_tx_total=new_cum_tx,
                         last_wg_rx=wg_rx,
                         last_wg_tx=wg_tx)

        # 同时记录日志
        self.log_traffic(user_id, new_cum_rx, new_cum_tx)

        return {"rx": new_cum_rx, "tx": new_cum_tx, "total": new_cum_rx + new_cum_tx}

    def get_traffic_summary(self, user_id: int) -> dict:
        """获取用户累计流量统计（从 users 表读取，重启不丢失）"""
        user = self.get_user_by_id(user_id)
        if not user:
            return {"rx_bytes": 0, "tx_bytes": 0, "total": 0}

        rx = user.get("traffic_rx_total", 0)
        tx = user.get("traffic_tx_total", 0)
        return {"rx_bytes": rx, "tx_bytes": tx, "total": rx + tx}

    def get_all_traffic(self) -> list:
        """获取所有用户最新流量（从 users 表读取累计值）"""
        conn = self._get_conn()
        c = conn.cursor()
        c.execute("""
            SELECT u.id, u.username, u.wg_public_key, u.is_active,
                   u.traffic_rx_total as rx_bytes,
                   u.traffic_tx_total as tx_bytes,
                   (u.traffic_rx_total + u.traffic_tx_total) as total_bytes
            FROM users u
            ORDER BY u.created_at DESC
        """)
        rows = c.fetchall()
        conn.close()
        return [dict(r) for r in rows]

    def get_today_traffic(self) -> dict:
        """获取今日所有用户的流量统计（通过 traffic_logs 差值计算）"""
        import datetime
        today = datetime.date.today()
        today_start = int(datetime.datetime(today.year, today.month, today.day).timestamp())

        conn = self._get_conn()
        c = conn.cursor()

        # 查询所有用户
        c.execute("SELECT id, username FROM users WHERE is_admin = 0 ORDER BY id")
        users = c.fetchall()

        result = {
            "total_rx": 0, "total_tx": 0, "total_all": 0,
            "leaderboard": []
        }

        for u in users:
            # 获取今天最早一条记录
            c.execute(
                "SELECT rx_bytes, tx_bytes FROM traffic_logs WHERE user_id = ? AND timestamp >= ? ORDER BY timestamp ASC LIMIT 1",
                (u["id"], today_start)
            )
            first_today = c.fetchone()

            # 获取昨天最后一条记录（作为基线）
            c.execute(
                "SELECT rx_bytes, tx_bytes FROM traffic_logs WHERE user_id = ? AND timestamp < ? ORDER BY timestamp DESC LIMIT 1",
                (u["id"], today_start)
            )
            last_yesterday = c.fetchone()

            # 获取最新一条记录
            c.execute(
                "SELECT rx_bytes, tx_bytes FROM traffic_logs WHERE user_id = ? ORDER BY timestamp DESC LIMIT 1",
                (u["id"],)
            )
            latest = c.fetchone()

            if latest:
                if first_today:
                    # 今日增量 = 最新 - 今天第一条
                    today_rx = latest["rx_bytes"] - first_today["rx_bytes"]
                    today_tx = latest["tx_bytes"] - first_today["tx_bytes"]
                elif last_yesterday:
                    # 跨天：最新 - 昨天最后一条
                    today_rx = latest["rx_bytes"] - last_yesterday["rx_bytes"]
                    today_tx = latest["tx_bytes"] - last_yesterday["tx_bytes"]
                else:
                    today_rx = latest["rx_bytes"]
                    today_tx = latest["tx_bytes"]
            else:
                today_rx = 0
                today_tx = 0

            today_total = today_rx + today_tx
            result["total_rx"] += today_rx
            result["total_tx"] += today_tx
            result["total_all"] += today_total

            result["leaderboard"].append({
                "user_id": u["id"],
                "username": u["username"],
                "today_rx": today_rx,
                "today_tx": today_tx,
                "today_total": today_total
            })

        # 排行榜按今日总量降序
        result["leaderboard"].sort(key=lambda x: x["today_total"], reverse=True)

        conn.close()
        return result

    def get_traffic_trend(self, days: int = 7) -> list:
        """获取最近 N 天的每日流量趋势"""
        import datetime
        trend = []

        for i in range(days - 1, -1, -1):
            day = datetime.date.today() - datetime.timedelta(days=i)
            day_start = int(datetime.datetime(day.year, day.month, day.day).timestamp())
            day_end = day_start + 86400

            conn = self._get_conn()
            c = conn.cursor()

            # 该天第一条记录（作为基线）
            c.execute(
                "SELECT rx_bytes, tx_bytes FROM traffic_logs WHERE timestamp >= ? AND timestamp < ? ORDER BY timestamp ASC LIMIT 1",
                (day_start, day_end)
            )
            first = c.fetchone()

            # 前一天最后一条记录（跨天基线）
            c.execute(
                "SELECT rx_bytes, tx_bytes FROM traffic_logs WHERE timestamp < ? ORDER BY timestamp DESC LIMIT 1",
                (day_start,)
            )
            prev_last = c.fetchone()

            # 该天最后一条记录
            c.execute(
                "SELECT rx_bytes, tx_bytes FROM traffic_logs WHERE timestamp < ? ORDER BY timestamp DESC LIMIT 1",
                (day_end,)
            )
            last = c.fetchone()

            conn.close()

            day_rx = 0
            day_tx = 0

            if last:
                if first:
                    day_rx = last["rx_bytes"] - first["rx_bytes"]
                    day_tx = last["tx_bytes"] - first["tx_bytes"]
                elif prev_last:
                    day_rx = last["rx_bytes"] - prev_last["rx_bytes"]
                    day_tx = last["tx_bytes"] - prev_last["tx_bytes"]
                else:
                    day_rx = last["rx_bytes"]
                    day_tx = last["tx_bytes"]

            # 处理负值（重启等情况）
            day_rx = max(day_rx, 0)
            day_tx = max(day_tx, 0)

            trend.append({
                "date": day.strftime("%m-%d"),
                "rx": day_rx,
                "tx": day_tx,
                "total": day_rx + day_tx
            })

        return trend
