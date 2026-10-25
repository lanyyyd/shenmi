"""群红包接口。

用户侧（get_current_user）：
  POST /api/redpacket/send        发群红包 {asset: points|exp, mode: random|fixed, total|per, count, remark}
  GET  /api/redpacket/{id}        红包详情（含领取名单、我的领取额、是否可领）
  POST /api/redpacket/{id}/grab   领取红包

约束：经验红包仅管理员可发；管理员发红包不扣积分余额，其他人发积分红包从余额扣除。
"""
from __future__ import annotations

from fastapi import APIRouter, Body, Depends, HTTPException

from .. import chat_logic as CL
from .. import redpacket as RP
from .. import wallet
from ..deps import get_current_user
from ..hub import hub

router = APIRouter(prefix="/api", tags=["redpacket"])


@router.post("/redpacket/send")
async def send_packet(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    res = RP.create(user, str(payload.get("asset") or ""), str(payload.get("mode") or ""),
                    payload.get("total") or 0, payload.get("count") or 0,
                    payload.get("per") or 0, str(payload.get("remark") or ""))
    if res.get("error"):
        raise HTTPException(400, res["error"])
    await CL.broadcast_chat(res["payload"])
    return {"ok": True, "id": res["id"], "packet": res["packet"], "balance": wallet.balance(user["id"])}


@router.get("/redpacket/{pid}")
def packet_detail(pid: int, user: dict = Depends(get_current_user)):
    d = RP.detail(pid, user["id"])
    if not d:
        raise HTTPException(404, "红包不存在")
    return {"ok": True, "packet": d}


@router.post("/redpacket/{pid}/grab")
async def grab_packet(pid: int, user: dict = Depends(get_current_user)):
    res = RP.grab(pid, user)
    if res.get("error"):
        raise HTTPException(400, res["error"])
    try:
        pk = res.get("packet") or {}
        await hub.broadcast_all({"type": "redpacket", "packetId": pid,
                                 "grabbedCount": pk.get("grabbedCount", 0),
                                 "status": pk.get("status", "active")})
    except Exception:
        pass
    return {"ok": True, **res, "balance": wallet.balance(user["id"])}