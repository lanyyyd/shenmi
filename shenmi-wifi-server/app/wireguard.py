"""
神秘 WiFi - WireGuard 管理模块
负责生成/回收客户端配置，读取流量统计
"""
import subprocess
import re
import os
import secrets
from config import (WG_INTERFACE, WG_CONFIG_PATH, WG_CLIENT_IP_PREFIX,
                    WG_CLIENT_IP_START, WG_SERVER_PRIVATE_KEY,
                    WG_PRESHARED_KEY, SERVER_IP, SERVER_PORT, WG_DNS)


def _run_wg(*args) -> str:
    """执行 wg 命令，返回输出"""
    cmd = ["wg"] + list(args)
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(f"wg command failed: {result.stderr}")
    return result.stdout.strip()


def _run_sudo_wg(*args) -> str:
    """用 sudo 执行 wg 命令"""
    cmd = ["sudo", "wg"] + list(args)
    result = subprocess.run(cmd, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(f"sudo wg command failed: {result.stderr}")
    return result.stdout.strip()


def gen_keypair() -> tuple:
    """生成 WireGuard 密钥对，返回 (private_key, public_key)"""
    priv = _run_wg("genkey")
    pub = subprocess.run(["wg", "pubkey"], input=priv, capture_output=True, text=True).stdout.strip()
    return priv, pub


def get_server_public_key() -> str:
    """获取服务器公钥"""
    try:
        return _run_sudo_wg("show", WG_INTERFACE, "public-key")
    except Exception:
        # 从私钥推导
        priv = WG_SERVER_PRIVATE_KEY
        result = subprocess.run(["wg", "pubkey"], input=priv, capture_output=True, text=True)
        return result.stdout.strip()


def get_used_ip_indices_from_server() -> set:
    """从 WireGuard 服务器读取实际在用的 IP 索引"""
    used = set()
    try:
        output = _run_sudo_wg("show", WG_INTERFACE, "allowed-ips")
        for line in output.strip().split('\n'):
            if not line.strip():
                continue
            parts = line.strip().split('\t')
            if len(parts) >= 2:
                ip_cidr = parts[1].strip()
                # 提取 IP 最后一段数字，如 10.7.0.5/32 -> 5
                m = re.search(r'(\d+)\.(\d+)\.(\d+)\.(\d+)/32', ip_cidr)
                if m:
                    idx = int(m.group(4))
                    if idx >= WG_CLIENT_IP_START:
                        used.add(idx)
    except Exception:
        pass
    return used


def get_next_ip_index(db) -> int:
    """获取下一个可用的 IP 索引（同时检查数据库和 WireGuard 服务器）"""
    conn = db._get_conn()
    c = conn.cursor()
    c.execute("SELECT ip_index FROM ip_allocations ORDER BY ip_index")
    db_used = set(row["ip_index"] for row in c.fetchall())
    conn.close()

    # 同时检查 WireGuard 服务器上实际在用的 IP
    server_used = get_used_ip_indices_from_server()

    used = db_used | server_used

    index = WG_CLIENT_IP_START
    while index in used:
        index += 1
    return index


def allocate_ip(db, user_id: int) -> str:
    """为用户分配 IP"""
    index = get_next_ip_index(db)
    ip = f"{WG_CLIENT_IP_PREFIX}{index}"
    conn = db._get_conn()
    conn.execute(
        "INSERT OR REPLACE INTO ip_allocations (ip_index, user_id, allocated_at) VALUES (?, ?, ?)",
        (index, user_id, __import__('time').time())
    )
    conn.commit()
    conn.close()
    return ip


def release_ip(db, user_id: int):
    """释放用户的 IP 分配"""
    conn = db._get_conn()
    conn.execute("DELETE FROM ip_allocations WHERE user_id = ?", (user_id,))
    conn.commit()
    conn.close()


def generate_client_config(private_key: str, client_ip: str) -> str:
    """生成客户端配置文本"""
    server_pub = get_server_public_key()
    return f"""[Interface]
PrivateKey = {private_key}
Address = {client_ip}/24
DNS = {WG_DNS}

[Peer]
PublicKey = {server_pub}
PresharedKey = {WG_PRESHARED_KEY}
AllowedIPs = 0.0.0.0/0
Endpoint = {SERVER_IP}:{SERVER_PORT}
PersistentKeepalive = 25
"""


def add_peer_to_server(public_key: str, client_ip: str, preshared_key: str = None):
    """在服务器上添加 WireGuard peer"""
    psk = preshared_key or WG_PRESHARED_KEY
    # 写入临时文件
    import tempfile
    with tempfile.NamedTemporaryFile(mode='w', suffix='.psk', delete=False) as f:
        f.write(psk)
        psk_path = f.name

    try:
        _run_sudo_wg("set", WG_INTERFACE, "peer", public_key,
                     "preshared-key", psk_path,
                     "allowed-ips", f"{client_ip}/32")
    finally:
        os.unlink(psk_path)

    # 持久化到配置文件
    try:
        subprocess.run(["sudo", "wg-quick", "save", WG_INTERFACE],
                       capture_output=True, text=True, check=False)
    except Exception:
        pass


def remove_peer_from_server(public_key: str):
    """从服务器移除 WireGuard peer"""
    _run_sudo_wg("set", WG_INTERFACE, "peer", public_key, "remove")
    try:
        subprocess.run(["sudo", "wg-quick", "save", WG_INTERFACE],
                       capture_output=True, text=True, check=False)
    except Exception:
        pass


def get_traffic_stats() -> dict:
    """
    读取 WireGuard 各 peer 的流量统计
    返回 {public_key: {rx: bytes, tx: bytes, endpoint: str, handshake: str}}
    """
    try:
        output = _run_sudo_wg("show", WG_INTERFACE, "transfer")
    except Exception:
        return {}

    stats = {}
    for line in output.strip().split('\n'):
        if not line.strip():
            continue
        parts = line.strip().split('\t')
        if len(parts) == 3:
            pubkey, rx, tx = parts
            # wg show 的 rx = 服务器收到 = 客户端上传
            # wg show 的 tx = 服务器发出 = 客户端下载
            # 转换为客户端视角：rx = 下载，tx = 上传
            stats[pubkey] = {
                "rx": int(tx),   # 客户端下载 = 服务器发出
                "tx": int(rx),   # 客户端上传 = 服务器收到
                "total": int(rx) + int(tx)
            }
    return stats


def get_peer_handshakes() -> dict:
    """获取各 peer 的最后握手时间"""
    try:
        output = _run_sudo_wg("show", WG_INTERFACE, "latest-handshakes")
    except Exception:
        return {}

    result = {}
    for line in output.strip().split('\n'):
        if not line.strip():
            continue
        parts = line.strip().split('\t')
        if len(parts) == 2:
            pubkey, ts = parts
            result[pubkey] = int(ts)
    return result
