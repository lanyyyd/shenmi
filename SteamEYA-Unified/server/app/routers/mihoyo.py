"""米哈游账号工具：用扫码得到的 stoken 生成抽卡记录链接。

  GET  /api/mihoyo/gacha/roles   当前用户所有米游社账号绑定的游戏角色
  POST /api/mihoyo/gacha/link    生成某个角色的抽卡链接 {game, game_uid, region, mid?}
"""
from __future__ import annotations

from fastapi import APIRouter, Body, Depends, HTTPException

from .. import gacha_link
from ..deps import get_current_user

router = APIRouter(prefix="/api/mihoyo", tags=["mihoyo"])


@router.get("/gacha/roles")
def gacha_roles(user: dict = Depends(get_current_user)):
    try:
        roles, errors = gacha_link.roles_for_user(user["id"])
    except ValueError as e:
        raise HTTPException(400, str(e))
    return {"ok": True, "roles": roles, "errors": errors}


@router.post("/gacha/link")
def gacha_link_create(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    game = str(payload.get("game") or "").strip()
    game_uid = str(payload.get("game_uid") or "").strip()
    region = str(payload.get("region") or "").strip()
    mid = str(payload.get("mid") or "").strip()
    try:
        result = gacha_link.link_for_user(user["id"], game, game_uid, region, mid)
    except ValueError as e:
        raise HTTPException(400, str(e))
    return {"ok": True, **result}
