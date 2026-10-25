"""云存档模块：游戏存档（zip）的上传 / 下载 / 备注 / 共享。

内置游戏：植物大战僵尸(pvz) / 植物大战僵尸杂交版新版(pvzhybrid) / 我的世界(minecraft)；
用户可自行添加游戏名（存档目录由客户端本地保存，服务端只登记名字）。
- 只接受单个 .zip 文件（服务端强校验扩展名，客户端另有拖拽限制）；
- 存档按创建时间命名（Asia/Shanghai，如 2026年10月11日14:32），可自定义；
- 共享后所有用户可在该游戏的「共享存档」中看到上传者与时间，备注跟随上传者自己的备注。
"""
from __future__ import annotations

import pathlib
import uuid
from datetime import datetime, timedelta, timezone
from typing import Optional

from fastapi import APIRouter, Body, Depends, File, Form, Header, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse

from .. import security
from ..config import DATA_DIR
from ..db import execute, query_all, query_one
from ..deps import get_current_user, resolve_user

router = APIRouter(prefix="/api/save", tags=["save"])

SAVE_DIR = DATA_DIR / "saves"
SAVE_DIR.mkdir(parents=True, exist_ok=True)

BUILTIN_GAMES = {"pvz": "植物大战僵尸", "pvzhybrid": "植物大战僵尸杂交版新版", "minecraft": "我的世界"}
GAMES = BUILTIN_GAMES
_TZ = timezone(timedelta(hours=8))
_seeded = False


def _seed_games() -> None:
    """把内置游戏写进 save_games（幂等），这样列表与校验都只需查表。"""
    global _seeded
    if _seeded:
        return
    now = security.now_s()
    for key, name in BUILTIN_GAMES.items():
        execute(
            "INSERT INTO save_games (game_key, name, visible, created_at) VALUES (?,?,1,?) "
            "ON CONFLICT(game_key) DO UPDATE SET name=excluded.name, visible=1",
            (key, name, now),
        )
    _seeded = True


def game_name_map() -> dict:
    """game_key → 显示名：内置优先，其次用户自定义。"""
    mapping = dict(BUILTIN_GAMES)
    try:
        for row in query_all("SELECT game_key, name FROM save_games"):
            mapping.setdefault(row["game_key"], row["name"])
    except Exception:
        pass
    return mapping


def game_exists(game_key: str) -> bool:
    if not game_key:
        return False
    return game_key in game_name_map()


def _default_name(ms: int) -> str:
    dt = datetime.fromtimestamp(ms / 1000, _TZ)
    return f"{dt.year}年{dt.month}月{dt.day}日{dt.hour:02d}:{dt.minute:02d}"


def _item(row, owner_name: str = "") -> dict:
    return {
        "id": row["id"], "game": row["game"], "name": row["name"], "remark": row["remark"] or "",
        "size": row["file_size"], "fileName": row["file_name"], "shared": bool(row["shared"]),
        "sharedAt": row["shared_at"], "createdAt": row["created_at"],
        "ownerId": row["user_id"], "ownerName": owner_name,
    }


@router.get("/games")
def games(request: Request = None, authorization: Optional[str] = Header(default=None)):
    """可见游戏：内置 + 已被别人上传过（visible=1）的自定义游戏 + 自己创建但还没公开的。

    自定义游戏会带上创建者填的 save_dir，客户端还原时直接用它做目标路径。
    """
    _seed_games()
    try:
        user = resolve_user(request, authorization)
    except Exception:
        # 未登录/请求对象缺失时不该让列表 500——按匿名处理，只返回已公开的游戏。
        user = None
    uid = user["id"] if user else 0

    rows = query_all(
        "SELECT game_key, name, save_dir, visible, created_by FROM save_games "
        "WHERE visible=1 OR created_by=? ORDER BY created_at",
        (uid,),
    )

    builtin = set(BUILTIN_GAMES)
    games_out = []
    seen = set()
    for key, name in BUILTIN_GAMES.items():
        games_out.append({"key": key, "name": name, "save_dir": "", "builtin": True,
                          "visible": True, "mine": False})
        seen.add(key)

    for r in rows:
        if r["game_key"] in seen or r["game_key"] in builtin:
            continue
        games_out.append({
            "key": r["game_key"], "name": r["name"], "save_dir": r["save_dir"] or "",
            "builtin": False, "visible": bool(r["visible"]), "mine": r["created_by"] == uid,
        })

    return {"ok": True, "games": games_out}


@router.post("/games")
def create_game(payload: dict = Body(...), request: Request = None,
                authorization: Optional[str] = Header(default=None)):
    """新增自定义游戏：只登记名字，存档目录是客户端本地的事。

    同名直接复用已有 key（包括内置游戏名），避免同一款游戏被建出多个条目、
    共享存档因此分散在几个 key 下互相看不见。
    """
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}

    name = (payload.get("name") or "").strip()[:40]
    # 存档目录会随游戏下发给所有人，是「大家约定同一个路径」，所以原样保存不做路径改写。
    save_dir = (payload.get("save_dir") or "").strip()[:400]
    if not name:
        return {"ok": False, "msg": "游戏名不能为空"}

    _seed_games()

    for key, builtin_name in BUILTIN_GAMES.items():
        if builtin_name == name:
            return {"ok": True, "key": key, "name": builtin_name, "existing": True, "builtin": True}

    existing = query_one("SELECT game_key, name, save_dir FROM save_games WHERE name=?", (name,))
    if existing:
        # 之前没填过路径（或换了路径）就补上，方便首个人上传后别人也能还原到同一位置。
        if save_dir and save_dir != (existing["save_dir"] or ""):
            execute("UPDATE save_games SET save_dir=? WHERE game_key=?", (save_dir, existing["game_key"]))
        return {"ok": True, "key": existing["game_key"], "name": existing["name"], "existing": True,
                "save_dir": save_dir or existing["save_dir"] or ""}

    game_key = "save_" + uuid.uuid4().hex[:10]
    execute(
        "INSERT INTO save_games (game_key, name, save_dir, created_by, created_at) VALUES (?,?,?,?,?)",
        (game_key, name, save_dir, user["id"], security.now_s()),
    )
    return {"ok": True, "key": game_key, "name": name, "save_dir": save_dir, "existing": False}


@router.post("/upload")
async def upload(game: str = Form(...), file: UploadFile = File(...),
                 name: str = Form(""), remark: str = Form(""), source: str = Form("auto"),
                 save_dir: str = Form(""),
                 request: Request = None, authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    if not game_exists(game):
        return {"ok": False, "msg": "未知游戏"}
    fname = file.filename or ""
    if pathlib.Path(fname).suffix.lower() != ".zip":
        return {"ok": False, "msg": "仅支持单个 .zip 压缩包"}
    data = await file.read()
    if not data:
        return {"ok": False, "msg": "文件为空"}
    now = security.now_ms()
    item_name = (name or "").strip()[:60] or _default_name(now)
    safe_id = uuid.uuid4().hex
    rel = f"{user['id']}/{game}/{safe_id}.zip"
    abs_path = SAVE_DIR / rel
    abs_path.parent.mkdir(parents=True, exist_ok=True)
    abs_path.write_bytes(data)
    sid = execute(
        "INSERT INTO cloud_saves (user_id, game, name, remark, file_path, file_name, file_size, "
        "shared, source, created_at) VALUES (?,?,?,?,?,?,?,0,?,?)",
        (user["id"], game, item_name, remark[:200], str(abs_path), fname[:200], len(data),
         "drag" if source == "drag" else "auto", now),
    )
    # 首个成功上传的人让这款游戏对所有人可见，并把存档目录记下来（别人还原时用同一路径）。
    execute("UPDATE save_games SET visible=1 WHERE game_key=? AND visible=0", (game,))
    clean_dir = (save_dir or "").strip()[:400]
    if clean_dir:
        execute(
            "UPDATE save_games SET save_dir=? WHERE game_key=? AND (save_dir IS NULL OR save_dir='')",
            (clean_dir, game),
        )

    row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
    return {"ok": True, "save": _item(row, user["nickname"] or user["username"])}


@router.get("/list")
def list_saves(game: str = "pvz", request: Request = None,
               authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    rows = query_all("SELECT * FROM cloud_saves WHERE user_id=? AND game=? ORDER BY created_at DESC",
                     (user["id"], game))
    return {"ok": True, "saves": [_item(r) for r in rows]}


@router.get("/shared")
def list_shared(game: str = "pvz", request: Request = None,
                authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    rows = query_all(
        "SELECT s.*, COALESCE(NULLIF(u.nickname,''), u.username) AS owner_name "
        "FROM cloud_saves s JOIN users u ON u.id = s.user_id "
        "WHERE s.game=? AND s.shared=1 ORDER BY s.shared_at DESC, s.created_at DESC", (game,))
    return {"ok": True, "saves": [_item(r, r["owner_name"]) for r in rows]}


@router.get("/download")
def download(id: int, request: Request = None, authorization: Optional[str] = Header(default=None)):
    user = resolve_user(request, authorization)
    if not user:
        return {"ok": False, "msg": "未登录"}
    row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (id,))
    if not row:
        return {"ok": False, "msg": "存档不存在"}
    if row["user_id"] != user["id"] and not row["shared"] and not user.get("is_admin"):
        return {"ok": False, "msg": "无权下载该存档"}
    p = pathlib.Path(row["file_path"])
    if not p.exists():
        return {"ok": False, "msg": "存档文件已丢失"}
    return FileResponse(str(p), media_type="application/zip", filename=f"{row['name']}.zip")


@router.post("/remark")
def set_remark(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    sid = int(payload.get("id") or 0)
    row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
    if not row or row["user_id"] != user["id"]:
        return {"ok": False, "msg": "存档不存在或无权操作"}
    execute("UPDATE cloud_saves SET remark = ? WHERE id = ?", (str(payload.get("remark") or "")[:200], sid))
    return {"ok": True}


@router.post("/share")
def share(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    ids = [int(i) for i in (payload.get("ids") or [])]
    shared = bool(payload.get("shared", True))
    if not ids:
        return {"ok": False, "msg": "未选择存档"}
    now = security.now_ms()
    changed = 0
    for sid in ids:
        row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
        if not row or row["user_id"] != user["id"]:
            continue
        execute("UPDATE cloud_saves SET shared = ?, shared_at = ? WHERE id = ?",
                (1 if shared else 0, now if shared else None, sid))
        changed += 1
    return {"ok": True, "changed": changed}


@router.post("/delete")
def delete(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    ids = [int(i) for i in (payload.get("ids") or [])]
    removed = 0
    for sid in ids:
        row = query_one("SELECT * FROM cloud_saves WHERE id = ?", (sid,))
        if not row or row["user_id"] != user["id"]:
            continue
        try:
            pathlib.Path(row["file_path"]).unlink(missing_ok=True)
        except Exception:
            pass
        execute("DELETE FROM cloud_saves WHERE id = ?", (sid,))
        removed += 1
    return {"ok": True, "removed": removed}