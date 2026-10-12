"""局域网发现 + 互传接口（离线/局域网模式下启用，在线模式可显式开启）。

- 发现：`/api/lan/peers`（UDP 广播自动发现）+ `/api/lan/peers/add`（手动填 IP 兜底）；
- 互传：`/api/lan/relay`、`/api/lan/relay/file` 接收同网段对端转来的消息/文件，
  落库后在本机聊天室广播，从而形成跨实例的局域网群聊与文件互传（不经公网）。
  本机产生的新消息由 app/lan.py 的 forward_to_peers 自动转发给对端。
"""
from __future__ import annotations

import os
import pathlib
from typing import Optional

from fastapi import APIRouter, Body, File, Form, UploadFile

from .. import chat_logic as CL
from .. import security
from ..config import MAX_UPLOAD, STICKER_DIR, UPLOAD_DIR
from ..lan import LanDiscovery, get_discovery, inbound_message, set_discovery as _set_discovery

router = APIRouter(prefix="/api/lan", tags=["lan"])


def set_discovery(d: LanDiscovery) -> None:
    """由 main.py 启动时注入发现实例。"""
    _set_discovery(d)


def _enabled() -> bool:
    return get_discovery() is not None


def _fix_name(name: str) -> str:
    """修复 multipart 文件名的编码错乱。

    转发方用 requests 上传文件时，非 ASCII（中文）文件名可能被当成 latin-1 字节写入
    multipart 头，接收端再按 latin-1 解出来就是乱码。这里尝试按 UTF-8 还原；
    若本来就是正常字符串（encode 不出 latin-1），原样返回。
    """
    try:
        return name.encode("latin-1").decode("utf-8")
    except (UnicodeEncodeError, UnicodeDecodeError):
        return name


@router.get("/info")
def info():
    d = get_discovery()
    return {
        "ok": True,
        "enabled": _enabled(),
        "lan_mode": os.getenv("EYA_LAN_MODE") == "1",
        "port": d.port if d else None,
        "name": d.name if d else "",
    }


@router.get("/identity")
def identity():
    """本机在局域网内的身份，供客户端/对端标识消息来源。"""
    d = get_discovery()
    if not d:
        return {"ok": True, "enabled": False}
    return {"ok": True, "enabled": True, **d.identity()}


@router.get("/peers")
def peers():
    d = get_discovery()
    if not d:
        return {"ok": True, "enabled": False, "peers": []}
    return {"ok": True, "enabled": True, "peers": d.snapshot()}


@router.post("/peers/add")
def add_peer(payload: dict = Body(...)):
    """手动兜底：把无法自动发现的对端加进来。"""
    d = get_discovery()
    if not d:
        return {"ok": False, "msg": "当前未启用局域网模式"}
    ip = str(payload.get("ip") or "").strip()
    port = int(payload.get("port") or 0)
    if not ip or port <= 0:
        return {"ok": False, "msg": "请填写正确的 IP 与端口"}
    peer = d.add_manual(ip, port, str(payload.get("name") or ""))
    return {"ok": True, "peer": peer}


# ---------------- 接收对端转来的消息 / 文件 ----------------
@router.post("/relay")
async def relay(payload: dict = Body(...)):
    """接收对端实例转来的文本/表情/文件引用类消息。"""
    if not _enabled():
        return {"ok": False, "msg": "当前未启用局域网互传"}
    out = inbound_message(payload)
    if out is None:
        return {"ok": True, "dedup": True}
    await CL.broadcast_chat(out)
    return {"ok": True, "id": out["id"]}


@router.post("/relay/file")
async def relay_file(origin: str = Form(...), clientMsgId: str = Form(...), installId: str = Form(""),
                     fromName: str = Form(""), fromIp: str = Form(""), type: str = Form("file"),
                     content: str = Form(""), createdAt: int = Form(0), fileName: str = Form(""),
                     file: UploadFile = File(...)):
    """接收对端实例推送的文件/图片/视频/表情：落盘到本机后再落库广播。"""
    if not _enabled():
        return {"ok": False, "msg": "当前未启用局域网互传"}
    data = await file.read()
    if not data:
        return {"ok": False, "msg": "空文件"}
    if len(data) > MAX_UPLOAD:
        return {"ok": False, "msg": "文件超过 50MB 限制", "code": 413}
    kind = type if type in ("image", "video", "sticker") else "file"
    sub = {"image": "images", "video": "videos", "sticker": "stickers"}.get(kind, "files")
    raw_name = _fix_name(fileName or file.filename or "")
    ext = pathlib.Path(raw_name).suffix.lower()[:12]
    name = f"{security.now_ms()}-{security.gen_token(6)}{ext}"
    dest = STICKER_DIR if sub == "stickers" else (UPLOAD_DIR / sub)
    (dest / name).write_bytes(data)
    url = f"/uploads/{sub}/{name}"
    payload = {
        "origin": origin, "installId": installId, "clientMsgId": clientMsgId,
        "fromName": fromName, "fromIp": fromIp, "type": kind, "content": content,
        "createdAt": createdAt, "file": {"url": url, "name": raw_name or name, "size": len(data)},
    }
    out = inbound_message(payload)
    if out is None:
        return {"ok": True, "dedup": True, "url": url}
    await CL.broadcast_chat(out)
    return {"ok": True, "id": out["id"], "url": url}
