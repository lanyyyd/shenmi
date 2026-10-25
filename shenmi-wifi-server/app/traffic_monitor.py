"""
神秘 WiFi - 流量采集守护进程
定时读取 wg show，记录每个用户的流量，检查过期/超量
"""
import time
import threading
import subprocess
from config import TRAFFIC_CHECK_INTERVAL
from app.database import Database
from app.wireguard import get_traffic_stats, remove_peer_from_server

db = Database()
_stop_flag = threading.Event()
_thread = None

# ---- 实时网速数据 ----
_speed_lock = threading.Lock()
_prev_total_rx = 0   # 上次采集时的总下载（所有用户合计）
_prev_total_tx = 0   # 上次采集时的总上传（所有用户合计）
_prev_ts = 0         # 上次采集时间戳
_current_speed = {
    "download_bps": 0,   # 下载速度 bytes/s
    "upload_bps": 0,     # 上传速度 bytes/s
    "total_bps": 0,      # 合计速度 bytes/s
    "online_peers": 0,   # 有流量的 peer 数
    "timestamp": 0       # 本次采集时间戳
}


def check_user_limits():
    """检查用户是否过期或流量超标，自动断开"""
    import time as _time
    now = _time.time()
    users = db.get_all_users()

    for user in users:
        if not user["is_active"] or not user["wg_public_key"]:
            continue

        pub_key = user["wg_public_key"]

        # 检查过期
        if user["expire_at"] and now > user["expire_at"]:
            print(f"[限流] 用户 {user['username']} 已过期，断开连接")
            remove_peer_from_server(pub_key)
            db.update_user(user["id"], is_active=0)
            continue

        # 检查流量（使用数据库中的累计值，而非 wg show 原始值）
        if user["traffic_limit_bytes"] > 0:
            used = user.get("traffic_rx_total", 0) + user.get("traffic_tx_total", 0)
            if used > user["traffic_limit_bytes"]:
                print(f"[限流] 用户 {user['username']} 流量超标 ({used}/{user['traffic_limit_bytes']})，断开连接")
                remove_peer_from_server(pub_key)
                db.update_user(user["id"], is_active=0)


def collect_traffic():
    """采集一次流量数据（增量累加，重启不丢失），同时计算实时网速"""
    global _prev_total_rx, _prev_total_tx, _prev_ts

    traffic = get_traffic_stats()
    users = db.get_all_users()

    now = time.time()
    cur_total_rx = 0
    cur_total_tx = 0
    active_peers = 0

    for user in users:
        pub_key = user.get("wg_public_key")
        if pub_key and pub_key in traffic:
            stats = traffic[pub_key]
            # 使用增量累加方式更新（处理重启清零）
            db.update_cumulative_traffic(user["id"], stats["rx"], stats["tx"])

            cur_total_rx += stats["rx"]
            cur_total_tx += stats["tx"]
            # 有流量数据的算活跃 peer
            if stats["rx"] > 0 or stats["tx"] > 0:
                active_peers += 1

    # ---- 计算实时网速 ----
    with _speed_lock:
        if _prev_ts > 0:
            elapsed = now - _prev_ts
            if elapsed > 0:
                # wg show 的计数器是累计的，差值即为这段时间内新增流量
                delta_rx = max(0, cur_total_rx - _prev_total_rx)
                delta_tx = max(0, cur_total_tx - _prev_total_tx)
                _current_speed["download_bps"] = int(delta_rx / elapsed)
                _current_speed["upload_bps"] = int(delta_tx / elapsed)
                _current_speed["total_bps"] = int((delta_rx + delta_tx) / elapsed)
                _current_speed["online_peers"] = active_peers
                _current_speed["timestamp"] = now
        else:
            # 第一次采集，无法计算速度
            _current_speed["download_bps"] = 0
            _current_speed["upload_bps"] = 0
            _current_speed["total_bps"] = 0
            _current_speed["online_peers"] = active_peers
            _current_speed["timestamp"] = now

        _prev_total_rx = cur_total_rx
        _prev_total_tx = cur_total_tx
        _prev_ts = now

    return len(traffic)


def get_realtime_speed() -> dict:
    """获取当前实时网速数据"""
    with _speed_lock:
        return dict(_current_speed)


def monitor_loop():
    """流量监控主循环"""
    print(f"[流量监控] 启动，每 {TRAFFIC_CHECK_INTERVAL} 秒采集一次")
    while not _stop_flag.is_set():
        try:
            count = collect_traffic()
            check_user_limits()
        except Exception as e:
            print(f"[流量监控] 采集出错: {e}")

        _stop_flag.wait(TRAFFIC_CHECK_INTERVAL)

    print("[流量监控] 已停止")


def start_monitor():
    """启动流量监控线程"""
    global _thread, _stop_flag
    _stop_flag = threading.Event()
    _thread = threading.Thread(target=monitor_loop, daemon=True)
    _thread.start()


def stop_monitor():
    """停止流量监控线程"""
    global _stop_flag
    _stop_flag.set()
