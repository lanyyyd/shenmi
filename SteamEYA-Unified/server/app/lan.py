"""局域网发现：UDP 广播自动发现同网段实例 + 手动兜底。

离线（局域网）模式下，客户端在本机跑同一个网关（数据落在非 C 盘目录），
各实例通过 UDP 广播互相发现，进而直连对方网关进行聊天与文件互传。
"""
from __future__ import annotations

import hashlib
import json
import os
import queue
import socket
import threading
import time
import uuid
from typing import Dict, Optional
from urllib.parse import urlparse

import requests

from .db import query_one

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

        beacon = json.dumps({"app": "steameya", "name": self.name, "port": self.port}).encode("utf-8")
        last_beacon = 0.0
        while not self._stop.is_set():
            now = time.time()
            if now - last_beacon >= BEACON_INTERVAL:
                last_beacon = now
                beacon = self._beacon()
                for addr in ("255.255.255.255", "127.0.0.1"):
                    try:
                        sock.sendto(beacon, (addr, self.discovery_port))
                    except Exception:
                        pass
            try:
                data, (ip, _) = sock.recvfrom(2048)
                info = json.loads(data.decode("utf-8", "ignore"))
            except socket.timeout:
                if self._prune():
                    _broadcast_online()
                continue
            except Exception:
                continue
            if info.get("app") != "steameya":
                continue
            # 跳过自己：IP 为本机，或 name+port 与本机一致，或 install_id 相同
            if ip == self._local_ip():
                continue
            if info.get("name") == self.name and int(info.get("port") or 0) == self.port:
                continue
            install_id = str(info.get("installId") or "")[:64]
            if install_id and install_id == self.instance_id:
                continue
            peer = {
                "ip": ip,
                "name": info.get("name") or ip,
                "port": int(info.get("port") or self.port),
                "installId": install_id,
                "user": str(info.get("user") or "")[:20],
                "lastSeen": int(now * 1000),
            }
            old = self.peers.get(ip)
            self.peers[ip] = peer
            changed = old is None or (old.get("user") != peer["user"] or old.get("name") != peer["name"])
            # 把对端登记为本机在线用户，离线模式的在线列表才能显示局域网里的其他人。
            if _mark_peer_online(peer):
                changed = True
            if changed:
                _broadcast_online()
                # 新对端（或换人）出现：从它那里补齐本机缺失的历史消息。
                ensure_history_sync(peer)
        self._prune()
        _broadcast_online()

    def _beacon(self) -> bytes:
        """每轮重新生成：带上本机当前登录用户，对端列表才能显示昵称而不是设备名。"""
        payload = {
            "app": "steameya", "name": self.name, "port": self.port,
            "installId": self.instance_id, "user": _local_user_name(),
        }
        return json.dumps(payload).encode("utf-8")

    def _prune(self) -> bool:
        cutoff = time.time() - PEER_TIMEOUT
        stale = [k for k, v in self.peers.items() if v["lastSeen"] / 1000 < cutoff]
        for ip in stale:
            peer = self.peers.pop(ip, None)
            if peer and peer.get("installId"):
                _drop_peer_online(peer["installId"])
        return bool(stale)

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

    def add_manual(self, ip: str, port: int, name: str = "") -> dict:
        ip = ip.strip()
        self.peers[ip] = {"ip": ip, "name": name or ip, "port": int(port), "lastSeen": int(time.time() * 1000)}
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


def _local_user_name() -> str:
    """本机网关上最近活跃的登录用户昵称，写进 UDP 广播供对端显示。"""
    try:
        from .db import query_one
        from .hub import ONLINE_TTL_MS
        from .security import now_ms

        row = query_one(
            "SELECT u.nickname, u.username FROM online o JOIN users u ON u.id = o.user_id "
            "WHERE o.conn_id NOT LIKE 'lan:%' AND o.last_seen >= ? "
            "ORDER BY o.last_seen DESC LIMIT 1",
            (now_ms() - ONLINE_TTL_MS,),
        )
        if row:
            return (row["nickname"] or row["username"] or "").strip()
    except Exception:
        pass
    return ""


def _broadcast_online() -> None:
    try:
        from .hub import hub

        hub.broadcast_online_threadsafe()
    except Exception:
        pass


def _mark_peer_online(peer: dict) -> bool:
    """把发现的局域网对端登记为在线用户；返回是否新增/变更，供广播去重。

    只在离线（局域网）模式生效，避免在线服务器把内网设备混进公网在线列表。
    """
    if os.getenv("EYA_LAN_MODE") != "1":
        return False
    install_id = str(peer.get("installId") or "").strip()
    if not install_id:
        return False

    from . import security
    from .db import execute, query_one

    user = ensure_peer_user(install_id, str(peer.get("user") or peer.get("name") or ""),
                            str(peer.get("ip") or ""))
    name = str(peer.get("user") or "").strip()[:20]
    if name and (user.get("nickname") or "") != name:
        execute("UPDATE users SET nickname = ? WHERE id = ?", (name, user["id"]))

    conn_id = "lan:" + install_id
    now = security.now_ms()
    row = query_one("SELECT conn_id FROM online WHERE conn_id = ?", (conn_id,))
    if row:
        execute("UPDATE online SET last_seen = ?, ip = ? WHERE conn_id = ?",
                (now, str(peer.get("ip") or "")[:64], conn_id))
        return False
    execute(
        "INSERT INTO online (conn_id, user_id, device_id, ip, ua, connected_at, last_seen) "
        "VALUES (?, ?, ?, ?, ?, ?, ?)",
        (conn_id, user["id"], "lan", str(peer.get("ip") or "")[:64], "", now, now),
    )
    return True


def _drop_peer_online(install_id: str) -> None:
    if os.getenv("EYA_LAN_MODE") != "1" or not install_id:
        return
    from .db import execute

    try:
        execute("DELETE FROM online WHERE conn_id = ?", ("lan:" + install_id,))
    except Exception:
        pass


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


# ---------------- 局域网控制指令（保队友 / 全员切换） ----------------
_control_seen: Dict[str, float] = {}
_control_lock = threading.Lock()
CONTROL_TTL = 60.0


def mark_control(control_id: str) -> bool:
    """记录控制指令；首次返回 True，重复或超过 60 秒后再次出现返回 False。"""
    if not control_id:
        return False
    now = time.time()
    with _control_lock:
        for k in [k for k, t in _control_seen.items() if now - t > CONTROL_TTL]:
            _control_seen.pop(k, None)
        if control_id in _control_seen:
            return False
        _control_seen[control_id] = now
        return True


def forward_control(action: str, mode: str, control_id: str, visited: list) -> None:
    """把控制指令异步转发给同网段对端；visited 用于阻断环形转发。"""
    d = get_discovery()
    if d is None:
        return
    peers = d.snapshot()
    if not peers:
        return
    seen = list(dict.fromkeys([*(visited or []), d.instance_id]))
    threading.Thread(target=_control_worker,
                     args=(peers, action, mode, control_id, seen), daemon=True).start()


def _control_worker(peers: list, action: str, mode: str, control_id: str, visited: list) -> None:
    body = {"action": action, "mode": mode, "controlId": control_id, "visited": visited}
    for p in peers:
        try:
            requests.post(f"http://{p['ip']}:{int(p['port'])}/api/lan/control",
                          json=body, timeout=(2, 5))
        except Exception:
            continue


_forward_queue: "queue.Queue[dict]" = queue.Queue()
_forward_lock = threading.Lock()
_forward_started = False


def _ensure_forward_worker() -> None:
    global _forward_started
    with _forward_lock:
        if _forward_started:
            return
        _forward_started = True
        threading.Thread(target=_forward_loop, name="lan-forward", daemon=True).start()


def forward_to_peers(payload: dict) -> None:
    """本机新消息广播后调用：入队后由单个后台线程按顺序转发给对端。

    串行队列保证同一实例的消息到达顺序与发送顺序一致；入队时对端可能还没被
    UDP 发现，worker 会短暂等待后再发，避免启动初期丢消息。
    """
    d = get_discovery()
    if d is None or not payload.get("id") or payload.get("toId"):
        return
    _ensure_forward_worker()
    _forward_queue.put(dict(payload))


def _forward_loop() -> None:
    while True:
        payload = _forward_queue.get()
        try:
            _forward_one(payload)
        except Exception:
            pass
        finally:
            _forward_queue.task_done()


def _forward_one(payload: dict) -> None:
    from .db import query_one

    d = get_discovery()
    if d is None:
        return

    deadline = time.time() + 5
    peers = d.snapshot()
    while not peers and time.time() < deadline:
        time.sleep(0.25)
        peers = d.snapshot()
    if not peers:
        return

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
        for attempt in range(2):
            try:
                if local is not None and local.exists():
                    fname = payload.get("fileName") or local.name
                    with local.open("rb") as fh:
                        requests.post(base + "/relay/file",
                                      data={**common, "fileName": fname},
                                      files={"file": (fname, fh)}, timeout=(2, 60))
                else:
                    requests.post(base + "/relay", json=common, timeout=(2, 10))
                break
            except Exception:
                if attempt == 0:
                    time.sleep(0.2)


# ================================================================
# 局域网历史补齐：
#   离线模式下每台机器只有自己的本地库，实时转发只覆盖「当时在线」的人。
#   后加入的设备看不到之前的聊天记录，所以发现对端后主动从对端网关拉取历史，
#   用 (install_id, client_msg_id) 幂等合并到本机库，再推给本机在线客户端。
#   全程只走局域网，不经过公网服务端。
# ================================================================

HISTORY_INTERVAL = 20.0
HISTORY_LIMIT = 200

_history_lock = threading.Lock()
_history_since: Dict[str, int] = {}
_history_started = False


def ensure_history_sync(peer: dict | None = None) -> None:
    """启动历史补拉线程；peer 不为空时立即对该对端拉一次。"""
    global _history_started
    with _history_lock:
        first = not _history_started
        _history_started = True
    if first:
        threading.Thread(target=_history_loop, name="lan-history", daemon=True).start()
    if peer:
        threading.Thread(target=_history_worker_one, args=(dict(peer),),
                         name="lan-history-pull", daemon=True).start()


def _history_loop() -> None:
    while True:
        time.sleep(HISTORY_INTERVAL)
        d = get_discovery()
        if d is None:
            continue
        for p in d.snapshot():
            try:
                _pull_history(p)
            except Exception:
                pass


def _history_worker_one(peer: dict) -> None:
    try:
        _pull_history(peer)
    except Exception:
        pass


def _pull_history(peer: dict) -> None:
    ip = str(peer.get("ip") or "").strip()
    try:
        port = int(peer.get("port") or 0)
    except Exception:
        return
    if not ip or not port:
        return
    base = f"http://{ip}:{port}"
    with _history_lock:
        since = _history_since.get(base, 0)
    resp = requests.get(base + "/api/lan/history",
                        params={"since": since, "limit": HISTORY_LIMIT},
                        timeout=(2, 8))
    if resp.status_code != 200:
        return
    data = resp.json() or {}
    messages = data.get("messages") or []
    newest = since
    for m in messages:
        try:
            created = int(m.get("createdAt") or 0)
        except Exception:
            created = 0
        newest = max(newest, created)
        payload = _history_payload(m, ip, port)
        if payload is None:
            continue
        # 已有就直接跳过，别为了幂等重复下载文件。
        if query_one("SELECT id FROM messages WHERE install_id=? AND client_msg_id=?",
                     (payload["installId"], payload["clientMsgId"])):
            continue
        _materialize_history_file(payload, base)
        out = inbound_message(payload)
        if out is None:
            continue
        try:
            from .hub import hub

            hub.broadcast_threadsafe(out)
        except Exception:
            pass
    if newest > since:
        with _history_lock:
            # 回退 1 秒做重叠，避免同一毫秒多条时漏拉；重复消息由幂等去重挡住。
            _history_since[base] = max(newest - 1000, since)


def _history_payload(m: dict, ip: str, port: int) -> Optional[dict]:
    if not isinstance(m, dict) or m.get("recalled"):
        return None
    name = str(m.get("fromName") or "").strip()[:20]
    install_id = str(m.get("installId") or "").strip()[:64]
    cid = str(m.get("clientMsgId") or "").strip()[:64]
    if not cid:
        seed = f"{name}|{m.get('createdAt')}|{m.get('type')}|{m.get('content')}|{m.get('fileName')}"
        cid = "hist-" + hashlib.sha1(seed.encode("utf-8", "ignore")).hexdigest()[:24]
    if not install_id:
        # 没有安装标识的老消息用「昵称哈希」当来源，保证从不同对端拉到同一条时能去重。
        install_id = "hist"
        origin = "hist-" + hashlib.sha1(name.encode("utf-8", "ignore")).hexdigest()[:12]
    else:
        origin = install_id
    file = m.get("file") if isinstance(m.get("file"), dict) else None
    if file and file.get("url"):
        url = str(file["url"])
        if url.startswith("/uploads/"):
            file = {**file, "url": f"http://{ip}:{port}{url}"}
    try:
        created_at = int(m.get("createdAt") or 0)
    except Exception:
        created_at = 0
    return {
        "origin": origin, "installId": install_id, "clientMsgId": cid,
        "fromName": name, "fromIp": str(m.get("fromIp") or ip),
        "type": m.get("type") or "text", "content": str(m.get("content") or "")[:4000],
        "createdAt": created_at, "file": file,
    }


def _materialize_history_file(payload: dict, base: str) -> None:
    """把历史消息里指向对端的文件下载到本机，避免对端离线后图片裂掉。"""
    file = payload.get("file")
    if not isinstance(file, dict):
        return
    url = str(file.get("url") or "")
    if not url.startswith("http://"):
        return
    try:
        from .config import MAX_UPLOAD, UPLOAD_DIR

        resp = requests.get(url, timeout=(2, 15), stream=True)
        if resp.status_code != 200:
            return
        length = int(resp.headers.get("Content-Length") or 0)
        if length and length > MAX_UPLOAD:
            return
        buf = bytearray()
        for chunk in resp.iter_content(65536):
            if not chunk:
                continue
            buf.extend(chunk)
            if len(buf) > MAX_UPLOAD:
                return
        if not buf:
            return
        suffix = urlparse(url).path.rsplit(".", 1)
        suffix = ("." + suffix[-1].lower()[:10]) if len(suffix) == 2 else ""
        name = "lan-" + hashlib.sha1(url.encode("utf-8", "ignore")).hexdigest()[:24] + suffix
        kind = payload.get("type")
        sub = {"image": "images", "video": "videos", "sticker": "stickers"}.get(kind, "files")
        dest_dir = UPLOAD_DIR / sub
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest = dest_dir / name
        if not dest.exists():
            dest.write_bytes(bytes(buf))
        file["url"] = f"/uploads/{sub}/{name}"
        if kind == "image":
            try:
                from . import media

                file["thumb"] = media.make_thumbnail(dest)
            except Exception:
                file["thumb"] = None
    except Exception:
        # 下载失败就保留对端绝对地址，对方在线时仍能正常显示。
        return