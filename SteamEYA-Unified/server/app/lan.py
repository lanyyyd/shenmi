"""局域网发现：UDP 广播自动发现同网段实例 + 手动兜底。

离线（局域网）模式下，客户端在本机跑同一个网关（数据落在非 C 盘目录），
各实例通过 UDP 广播互相发现，进而直连对方网关进行聊天与文件互传。
"""
from __future__ import annotations

import hashlib
import json
import os
import socket
import threading
import time
import uuid
from typing import Dict, Optional

import requests

DISCOVERY_PORT = 8899
BEACON_INTERVAL = 3.0
PEER_TIMEOUT = 12.0


class LanDiscovery:
    def __init__(self, port: int, name: str, discovery_port: int = DISCOVERY_PORT) -> None:
        self.port = port
        self.name = name
        self.discovery_port = discovery_port
        self.peers: Dict[str, dict] = {}   # key = ip
        self._stop = threading.Event()
        self._sock: socket.socket | None = None
        self._thread: threading.Thread | None = None

    # ---------------- 生命周期 ----------------
    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(target=self._run, name="lan-discovery", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._sock is not None:
            try:
                self._sock.close()
            except Exception:
                pass

    # ---------------- 主循环 ----------------
    def _run(self) -> None:
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.bind(("", self.discovery_port))
            sock.settimeout(0.5)
            self._sock = sock
        except Exception:
            return

        # assistPort / assistEnabled：远程协助端口随发现一起广播，对端双击即可直连，无需手工配置。
        beacon = json.dumps({
            "app": "steameya",
            "name": self.name,
            "port": self.port,
            "assistPort": int(os.getenv("EYA_ASSIST_PORT", "8900")),
            "assistEnabled": os.getenv("EYA_ASSIST_ENABLED", "1") == "1",
            "uid": os.getenv("EYA_USER_ID", ""),
        }).encode("utf-8")
        last_beacon = 0.0
        while not self._stop.is_set():
            now = time.time()
            if now - last_beacon >= BEACON_INTERVAL:
                last_beacon = now
                for addr in ("255.255.255.255", "127.0.0.1"):
                    try:
                        sock.sendto(beacon, (addr, self.discovery_port))
                    except Exception:
                        pass
            try:
                data, (ip, _) = sock.recvfrom(2048)
                info = json.loads(data.decode("utf-8", "ignore"))
            except socket.timeout:
                self._prune()
                continue
            except Exception:
                continue
            if info.get("app") != "steameya":
                continue
            # 跳过自己：IP 为本机，或 name+port 与本机一致
            if ip == self._local_ip():
                continue
            if info.get("name") == self.name and int(info.get("port") or 0) == self.port:
                continue
            self.peers[ip] = {
                "ip": ip,
                "name": info.get("name") or ip,
                "port": int(info.get("port") or self.port),
                "assistPort": int(info.get("assistPort") or 0),
                "assistEnabled": bool(info.get("assistEnabled", False)),
                "uid": str(info.get("uid") or ""),
                "lastSeen": int(now * 1000),
            }
        self._prune()

    def _prune(self) -> None:
        cutoff = time.time() - PEER_TIMEOUT
        for ip in [k for k, v in self.peers.items() if v["lastSeen"] / 1000 < cutoff]:
            self.peers.pop(ip, None)

    @staticmethod
    def _local_ip() -> str:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("8.8.8.8", 80))
            ip = s.getsockname()[0]
            s.close()
            return ip
        except Exception:
            return "127.0.0.1"

    # ---------------- 对外 ----------------
    def snapshot(self) -> list[dict]:
        self._prune()
        return list(self.peers.values())

    def add_manual(self, ip: str, port: int, name: str = "", assist_port: int = 8900) -> dict:
        ip = ip.strip()
        self.peers[ip] = {
            "ip": ip, "name": name or ip, "port": int(port),
            "assistPort": int(assist_port), "assistEnabled": True, "uid": "",
            "lastSeen": int(time.time() * 1000),
        }
        return self.peers[ip]

    # ---------------- 本机身份（供互传时标识来源） ----------------
    @property
    def instance_id(self) -> str:
        """本实例唯一标识：优先用客户端注入的安装标识，否则用「设备名@IP:端口」。"""
        from .config import INSTALL_ID

        return INSTALL_ID or f"{self.name}@{self._local_ip()}:{self.port}"

    def identity(self) -> dict:
        return {"installId": self.instance_id, "name": self.name,
                "port": self.port, "ip": self._local_ip()}


# ================================================================
# 局域网互传：把本机消息/文件转发给同网段已发现实例，并接收对端转来的内容。
# 设计：
#   - 每个实例在本机跑一个完整网关（聊天室 + 上传），互传时不经过公网；
#   - 本机产生的「群聊」消息在广播后异步转发给所有已发现对端；
#   - 对端通过 /api/lan/relay(/file) 落库并本地广播，形成跨实例的局域网群聊；
#   - 以 (install_id, client_msg_id) 幂等去重，重复转发不会产生重复消息。
# ================================================================

_discovery: Optional[LanDiscovery] = None


def set_discovery(d: LanDiscovery) -> None:
    global _discovery
    _discovery = d


def get_discovery() -> Optional[LanDiscovery]:
    return _discovery


def _peer_username(origin: str) -> str:
    return "pt_" + hashlib.sha1(origin.encode("utf-8", "ignore")).hexdigest()[:16]


def ensure_peer_user(origin: str, name: str = "", ip: str = "") -> dict:
    """为对端实例落一个本地「影子用户」，让转来的消息在聊天室里能正常归属与展示。"""
    from . import security
    from .db import execute, query_one

    username = _peer_username(origin)
    row = query_one("SELECT * FROM users WHERE username = ?", (username,))
    if row:
        return dict(row)
    disp = (name or "局域网用户").strip()[:20] or "局域网用户"
    uid = execute(
        "INSERT INTO users (username, password_hash, nickname, created_at, reg_ip) VALUES (?,?,?,?,?)",
        (username, security.gen_token(16), disp, security.now_ms(), (ip or "")[:64]),
    )
    return dict(query_one("SELECT * FROM users WHERE id = ?", (uid,)))


def inbound_message(payload: dict) -> Optional[dict]:
    """把对端转来的消息落库并返回广播 payload；重复消息返回 None。"""
    from . import chat_logic as CL
    from .db import query_one

    origin = str(payload.get("origin") or "").strip()
    cid = str(payload.get("clientMsgId") or "").strip()[:64]
    if not origin or not cid:
        return None
    install_id = (str(payload.get("installId") or origin).strip() or origin)[:64]
    # 幂等：同一来源实例的同一条消息只落库一次（与 messages 唯一索引一致）
    if query_one("SELECT id FROM messages WHERE install_id=? AND client_msg_id=?", (install_id, cid)):
        return None
    user = ensure_peer_user(origin, str(payload.get("fromName") or ""), str(payload.get("fromIp") or ""))
    msg_type = payload.get("type")
    if msg_type not in ("text", "image", "file", "video", "sticker"):
        msg_type = "text"
    content = str(payload.get("content") or "")[:4000]
    file = payload.get("file") if isinstance(payload.get("file"), dict) else None
    try:
        created_at = int(payload.get("createdAt") or 0)
    except Exception:
        created_at = 0
    return CL.insert_chat(user["id"], msg_type, content, file, None, "",
                          created_at or None, install_id, cid)


def _local_upload_path(file_path: str):
    from .config import UPLOAD_DIR

    if not file_path or not file_path.startswith("/uploads/"):
        return None
    try:
        return UPLOAD_DIR / file_path[len("/uploads/"):]
    except Exception:
        return None


def forward_to_peers(payload: dict) -> None:
    """本机新消息广播后调用：异步转发给同网段对端（仅群聊，尽力而为，不阻塞调用方）。"""
    d = get_discovery()
    if d is None or not payload.get("id") or payload.get("toId"):
        return
    peers = d.snapshot()
    if not peers:
        return
    threading.Thread(target=_forward_worker, args=(d, peers, payload), daemon=True).start()


def _forward_worker(d: LanDiscovery, peers: list, payload: dict) -> None:
    from .db import query_one

    row = query_one("SELECT install_id, client_msg_id FROM messages WHERE id = ?", (payload["id"],))
    install_id = ((row["install_id"] if row else "") or d.instance_id)[:64]
    cid = ((row["client_msg_id"] if row else "") or uuid.uuid4().hex)[:64]
    ident = d.identity()
    common = {
        "origin": ident["installId"], "installId": install_id, "clientMsgId": cid,
        "fromName": payload.get("fromName") or "", "fromIp": ident["ip"],
        "type": payload.get("msgType") or "text", "content": payload.get("content") or "",
        "createdAt": payload.get("createdAt") or 0,
    }
    local = _local_upload_path(payload.get("filePath") or "")
    for p in peers:
        base = f"http://{p['ip']}:{int(p['port'])}/api/lan"
        try:
            if local is not None and local.exists():
                fname = payload.get("fileName") or local.name
                with local.open("rb") as fh:
                    requests.post(base + "/relay/file",
                                  data={**common, "fileName": fname},
                                  files={"file": (fname, fh)}, timeout=(2, 60))
            else:
                requests.post(base + "/relay", json=common, timeout=(2, 10))
        except Exception:
            continue