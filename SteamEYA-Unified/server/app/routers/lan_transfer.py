"""局域网点对点直传：任意大小、多文件并发、单文件多连接分段。

发送端（对端客户端）直接连本机网关（默认 8898），走**原始字节流**：不经过 multipart、
不把整文件读进内存，因此没有大小限制，也最容易把千兆网跑满——即局域网内最快的做法。

  POST /api/lan/tr/begin               建立一次传输（先落一个 .part 占位文件）
  PUT  /api/lan/tr/{tid}/data?offset=N 把一段字节写到文件 offset 处（每段一个连接，可并发）
  POST /api/lan/tr/{tid}/finish        收齐后改名转正并登记接收记录
  POST /api/lan/tr/{tid}/abort         放弃传输并清理 .part
  GET  /api/lan/tr/inbox               接收页轮询：进行中的传输 + 最近接收记录 + 接收目录

并发模型：每个 data 请求各自 open/seek/write 到互不重叠的字节区间，天然并发安全；
只有 finish 才把 .part 转正，避免半成品被误当成完整文件。
"""
from __future__ import annotations

import threading
import time
from pathlib import Path

from fastapi import APIRouter, Body, HTTPException, Request

from ..config import DATA_DIR

router = APIRouter(prefix="/api/lan/tr", tags=["lan-transfer"])

# 接收目录：随网关数据目录（由客户端 EYA_DATA_DIR 指向非 C 盘）
RECV_DIR = DATA_DIR / "lan_received"
RECV_DIR.mkdir(parents=True, exist_ok=True)

_lock = threading.RLock()
_transfers: dict[str, dict] = {}
_received: list[dict] = []
_MAX_RECEIVED = 200
_BAD_CHARS = '<>:"/\\|?*'


def _safe_name(name: str) -> str:
    """只取文件名部分并去掉非法字符，避免对端用 ../ 之类越权写盘。"""
    name = (name or "").replace("\\", "/").split("/")[-1].strip()
    for ch in _BAD_CHARS:
        name = name.replace(ch, "_")
    name = name.strip(" .")
    return name[:180] or "unnamed"


def _unique_path(path: Path) -> Path:
    if not path.exists():
        return path
    for i in range(1, 100000):
        cand = path.with_name(f"{path.stem} ({i}){path.suffix}")
        if not cand.exists():
            return cand
    return path.with_name(f"{path.stem}-{int(time.time())}{path.suffix}")


@router.post("/begin")
def begin(payload: dict = Body(...)):
    tid = str(payload.get("transferId") or "").strip()[:64]
    if not tid:
        raise HTTPException(400, "缺少 transferId")
    name = _safe_name(str(payload.get("fileName") or ""))
    try:
        size = int(payload.get("size") or 0)
    except (TypeError, ValueError):
        size = 0
    with _lock:
        st = _transfers.get(tid)
        if st is None:
            part = RECV_DIR / f".{tid}.part"
            try:
                part.write_bytes(b"")
            except OSError as exc:
                raise HTTPException(500, f"无法创建接收文件：{exc}") from exc
            st = {
                "id": tid,
                "fileName": name,
                "size": max(size, 0),
                "from": str(payload.get("fromName") or payload.get("fromIp") or ""),
                "fromIp": str(payload.get("fromIp") or ""),
                "part": str(part),
                "written": 0,
                "status": "active",
                "createdAt": int(time.time() * 1000),
            }
            _transfers[tid] = st
        else:
            if size > 0:
                st["size"] = size
            if name:
                st["fileName"] = name
    return {"ok": True, "id": tid}


@router.put("/{tid}/data")
async def data(tid: str, request: Request, offset: int = 0):
    with _lock:
        st = _transfers.get(tid)
        if st is None:
            raise HTTPException(404, "传输不存在")
        part = Path(st["part"])
    if offset < 0:
        offset = 0

    written = 0
    try:
        handle = open(part, "r+b")
    except OSError as exc:
        raise HTTPException(500, f"无法写入接收文件：{exc}") from exc
    try:
        handle.seek(offset)
        async for chunk in request.stream():
            if not chunk:
                continue
            handle.write(chunk)
            written += len(chunk)
        handle.flush()
    finally:
        handle.close()

    with _lock:
        cur = _transfers.get(tid)
        if cur is not None:
            cur["written"] = int(cur.get("written") or 0) + written
    return {"ok": True, "written": written}


@router.post("/{tid}/finish")
def finish(tid: str):
    with _lock:
        st = _transfers.get(tid)
        if st is None:
            raise HTTPException(404, "传输不存在")
        part = Path(st["part"])
        expected = int(st.get("size") or 0)
        name = st["fileName"]

    actual = part.stat().st_size if part.exists() else 0
    if expected > 0 and actual < expected:
        return {"ok": False, "msg": f"文件不完整（{actual}/{expected} 字节）"}

    dest = _unique_path(RECV_DIR / name)
    try:
        part.replace(dest)
    except OSError as exc:
        return {"ok": False, "msg": f"落盘失败：{exc}"}

    record = {
        "id": tid,
        "name": dest.name,
        "size": actual,
        "from": st.get("from") or "",
        "fromIp": st.get("fromIp") or "",
        "path": str(dest),
        "finishedAt": int(time.time() * 1000),
    }
    with _lock:
        _received.insert(0, record)
        del _received[_MAX_RECEIVED:]
        _transfers.pop(tid, None)
    return {"ok": True, "name": dest.name, "path": str(dest), "dir": str(RECV_DIR)}


@router.post("/{tid}/abort")
def abort(tid: str):
    with _lock:
        st = _transfers.pop(tid, None)
    if st:
        try:
            Path(st["part"]).unlink(missing_ok=True)
        except OSError:
            pass
    return {"ok": True}


@router.get("/inbox")
def inbox(limit: int = 100):
    limit = max(1, min(limit, _MAX_RECEIVED))
    with _lock:
        active = [
            {
                "id": s["id"],
                "fileName": s["fileName"],
                "size": int(s.get("size") or 0),
                "written": int(s.get("written") or 0),
                "from": s.get("from") or "",
                "fromIp": s.get("fromIp") or "",
                "createdAt": s.get("createdAt") or 0,
                "status": "active",
            }
            for s in _transfers.values()
            if s.get("status") == "active"
        ]
        received = list(_received[:limit])
    return {"ok": True, "enabled": True, "dir": str(RECV_DIR), "active": active, "received": received}
