"""「小火人」AI 助手核心。

- 配置全部存 settings 表的 `ai_config`（后台可填 API Key / 模型 / URL / 人设 / 主动发言开关）；
- 私聊：任意用户与小火人的私聊房间（pm:me:ai）独立上下文，互不串；
- 群聊：被 @ 到才回复（不再需要 / 指令）；
- 主动发言：由后台开关控制，空闲且近期有活跃时低频抛话题；
- 调用的是 OpenAI 兼容的 /chat/completions，只发标准字段，兼容 DeepSeek 及各类第三方中转。
"""
from __future__ import annotations

import asyncio
import base64
import datetime
import json
import logging
import mimetypes
import threading
import time
import uuid
from typing import Optional

import requests

from . import security
from . import chat_logic as CL
from .config import UPLOAD_DIR
from .db import execute, query_all, query_one
from .hub import hub

log = logging.getLogger("gateway.ai")

AI_USERNAME = "xiaohuoren"
DEFAULT_NAME = "小火人"

DEFAULT_CONFIG: dict = {
    "enabled": True,
    "name": DEFAULT_NAME,
    "base_url": "https://api.deepseek.com",
    "api_key": "",
    "model": "deepseek-flash",
    "persona_private": (
        "你是「小火人」，用户的贴心私聊小助手。用中文、口语化、简短自然地聊天，"
        "像一个熟悉的朋友；回答尽量控制在几句话内，不要长篇大论，也不要罗列条条框框。"
    ),
    "persona_group": (
        "你是群聊里的「小火人」小助手。有人在群里 @ 你时参与聊天：可以接梗、答疑、给建议。"
        "语气活泼，回复 1~3 句为宜，别太长，也别复述别人的话。"
    ),
    "active_speak": False,        # 主动发言开关（后台可改）
    "active_gap_sec": 600,        # 群聊静默多久后可以考虑主动发言
    "active_daily_limit": 20,     # 每天主动发言上限
    "max_context": 20,            # 带多少条历史作为上下文
    "max_tokens": 700,
    "temperature": 0.8,
    "cooldown_sec": 3,            # 同一会话最小回复间隔
    "group_daily_limit": 300,     # 群聊被 @ 回复的每日上限
}

_ALLOWED_PATCH = set(DEFAULT_CONFIG.keys())

_loop: Optional[asyncio.AbstractEventLoop] = None
_lock = threading.Lock()
_last_reply_at: dict[str, float] = {}
_active_thread_started = False


# ---------------------------------------------------------------- 配置
def get_config() -> dict:
    cfg = dict(DEFAULT_CONFIG)
    row = query_one("SELECT value FROM settings WHERE key = 'ai_config'")
    if row and row["value"]:
        try:
            cfg.update(json.loads(row["value"]))
        except Exception:
            pass
    return cfg


def save_config(patch: dict) -> dict:
    cfg = get_config()
    for k, v in (patch or {}).items():
        if k in _ALLOWED_PATCH:
            cfg[k] = v
    execute(
        "INSERT INTO settings (key, value, updated_at) VALUES ('ai_config', ?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
        (json.dumps(cfg, ensure_ascii=False), security.now_ms()),
    )
    # 改了名字就同步到 AI 账号的昵称
    name = str(cfg.get("name") or DEFAULT_NAME).strip() or DEFAULT_NAME
    execute("UPDATE users SET nickname = ? WHERE is_ai = 1", (name[:20],))
    return cfg


# ---------------------------------------------------------------- AI 账号
def ai_user():
    return query_one("SELECT * FROM users WHERE is_ai = 1 ORDER BY id ASC LIMIT 1")


def ai_user_id() -> int:
    row = ai_user()
    return int(row["id"]) if row else 0


def display_name() -> str:
    u = ai_user()
    if u and u["nickname"]:
        return u["nickname"]
    return str(get_config().get("name") or DEFAULT_NAME)


# ---------------------------------------------------------------- 调用模型
def _day_start_ms() -> int:
    now = datetime.datetime.now()
    start = now.replace(hour=0, minute=0, second=0, microsecond=0)
    return int(start.timestamp() * 1000)


def _call_model(messages: list[dict]) -> tuple[bool, str]:
    cfg = get_config()
    key = str(cfg.get("api_key") or "").strip()
    if not key:
        return False, "（小火人还没配置 API Key，请管理员在后台「AI 设置」里填写～）"
    base = str(cfg.get("base_url") or "").strip().rstrip("/")
    if not base:
        return False, "（小火人没配置接口地址）"
    url = base + "/chat/completions"
    body: dict = {
        "model": str(cfg.get("model") or "deepseek-flash"),
        "messages": messages,
        "stream": False,
        "max_tokens": int(cfg.get("max_tokens") or 700),
    }
    try:
        temp = float(cfg.get("temperature") or 0)
        if temp > 0:
            body["temperature"] = temp
    except Exception:
        pass
    try:
        resp = requests.post(url, json=body, timeout=90,
                             headers={"Authorization": f"Bearer {key}",
                                      "Content-Type": "application/json"})
        if resp.status_code != 200:
            log.warning("AI 调用失败 HTTP %s: %s", resp.status_code, resp.text[:200])
            return False, f"（小火人调用失败：HTTP {resp.status_code}）"
        data = resp.json()
        content = ""
        try:
            content = (data.get("choices") or [{}])[0].get("message", {}).get("content") or ""
        except Exception:
            content = ""
        content = str(content).strip()
        if not content:
            return False, "（小火人没返回内容）"
        return True, content
    except Exception as exc:
        log.warning("AI 调用异常：%s", exc)
        return False, f"（小火人开小差了：{exc}）"


def _call_model_stream(messages: list[dict]):
    """流式调用模型。

    逐段产出 ("delta", 文本)；出错产出 ("error", 提示)；最后产出 ("done", "")。
    只转发正文 content，跳过 reasoning/思考过程。
    """
    cfg = get_config()
    key = str(cfg.get("api_key") or "").strip()
    if not key:
        yield "error", "（小火人还没配置 API Key，请管理员在后台「AI 设置」里填写～）"
        yield "done", ""
        return
    base = str(cfg.get("base_url") or "").strip().rstrip("/")
    if not base:
        yield "error", "（小火人没配置接口地址）"
        yield "done", ""
        return
    url = base + "/chat/completions"
    body: dict = {
        "model": str(cfg.get("model") or "deepseek-flash"),
        "messages": messages,
        "stream": True,
        "max_tokens": int(cfg.get("max_tokens") or 700),
    }
    try:
        temp = float(cfg.get("temperature") or 0)
        if temp > 0:
            body["temperature"] = temp
    except Exception:
        pass
    resp = None
    try:
        resp = requests.post(url, json=body, timeout=(10, 300), stream=True,
                             headers={"Authorization": f"Bearer {key}",
                                      "Content-Type": "application/json"})
        if resp.status_code != 200:
            log.warning("AI 流式调用失败 HTTP %s: %s", resp.status_code, resp.text[:200])
            yield "error", f"（小火人调用失败：HTTP {resp.status_code}）"
            yield "done", ""
            return
        ctype = (resp.headers.get("content-type") or "").lower()
        if "json" in ctype and "event-stream" not in ctype:
            # 中转忽略了 stream，直接返回完整 JSON：当作一次 delta
            try:
                data = resp.json()
                content = str((data.get("choices") or [{}])[0].get("message", {}).get("content") or "")
            except Exception:
                content = ""
            if content:
                yield "delta", content
            yield "done", ""
            return
        for raw in resp.iter_lines(decode_unicode=False):
            if not raw:
                continue
            # SSE 未声明 charset 时 requests 会按 latin-1 解码，中文会变乱码，这里统一按 UTF-8 解码
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            data = line[5:].strip()
            if not data or data == "[DONE]":
                if data == "[DONE]":
                    break
                continue
            try:
                obj = json.loads(data)
            except (ValueError, TypeError):
                continue
            for ch in obj.get("choices") or []:
                delta = ch.get("delta") or {}
                piece = delta.get("content")
                if piece is None:
                    piece = ch.get("text")
                if piece:
                    yield "delta", str(piece)
        yield "done", ""
    except Exception as exc:
        log.warning("AI 流式调用异常：%s", exc)
        yield "error", f"（小火人开小差了：{exc}）"
        yield "done", ""
    finally:
        if resp is not None:
            try:
                resp.close()
            except Exception:
                pass


# ---------------------------------------------------------------- 上下文
# 单次最多带多少条历史消息（后台 max_context 可调，上限按此值钳制）
MAX_HISTORY_MESSAGES = 200
# 单次请求最多真正附带多少张图片（更早的图片退化为文字占位，避免撑爆上下文）
MAX_IMAGES_IN_CONTEXT = 4
MAX_IMAGE_BYTES = 8 * 1024 * 1024
MAX_IMAGE_TOTAL_CHARS = 8 * 1024 * 1024  # 一次请求里图片 data URL 的总预算


def _recent(room: str, limit: int):
    rows = query_all(
        CL._MSG_SELECT + "WHERE m.room = ? AND m.recalled = 0 ORDER BY m.id DESC LIMIT ?",
        (room, max(1, min(int(limit or 20), MAX_HISTORY_MESSAGES))),
    )
    return list(reversed(rows))


def _image_data_url(file_path: str) -> str:
    """把 /uploads/ 下的本地图片转成 data URL，供多模态模型直接读取。"""
    p = str(file_path or "")
    if not p.startswith("/uploads/"):
        return ""
    fp = UPLOAD_DIR / p[len("/uploads/"):]
    try:
        if not fp.is_file() or fp.stat().st_size > MAX_IMAGE_BYTES:
            return ""
        raw = fp.read_bytes()
    except OSError:
        return ""
    mime = mimetypes.guess_type(fp.name)[0] or "image/jpeg"
    if not mime.startswith("image/"):
        return ""
    return f"data:{mime};base64," + base64.b64encode(raw).decode()


def _hist_messages(room: str, limit: int, is_group: bool) -> list[dict]:
    out: list[dict] = []
    aid = ai_user_id()
    rows = _recent(room, limit)
    # 只把最近几张图片真正发给模型；更早的图片仍用文字占位
    image_ids = set()
    for r in reversed(rows):
        if r["type"] == "image" and len(image_ids) < MAX_IMAGES_IN_CONTEXT:
            image_ids.add(r["id"])
    image_chars = 0
    for r in rows:
        is_ai = r["from_id"] == aid
        prefix = f"{r['from_name']}: " if (is_group and not is_ai) else ""
        if r["type"] == "image" and r["id"] in image_ids:
            data_url = _image_data_url(r["file_path"])
            if data_url and image_chars + len(data_url) <= MAX_IMAGE_TOTAL_CHARS:
                image_chars += len(data_url)
                out.append({
                    "role": "assistant" if is_ai else "user",
                    "content": [
                        {"type": "text", "text": prefix + "[图片]"},
                        {"type": "image_url", "image_url": {"url": data_url}},
                    ],
                })
                continue
        if r["type"] == "sticker":
            text = "[表情]"
        elif r["type"] != "text":
            text = f"[{r['type']}] {r['file_name'] or r['file_path'] or ''}".strip()
        else:
            text = r["content"] or ""
        out.append({"role": "assistant" if is_ai else "user", "content": prefix + text})
    return out


def _cooldown_ok(room: str) -> bool:
    cfg = get_config()
    gap = max(0, int(cfg.get("cooldown_sec") or 0))
    now = time.time()
    with _lock:
        last = _last_reply_at.get(room, 0)
        if gap and (now - last) < gap:
            return False
        _last_reply_at[room] = now
    return True


def _daily_count(room: str, from_id: int) -> int:
    row = query_one(
        "SELECT COUNT(*) AS c FROM messages WHERE room = ? AND from_id = ? AND created_at >= ?",
        (room, from_id, _day_start_ms()))
    return int(row["c"] or 0) if row else 0


# ---------------------------------------------------------------- 生成回复
def private_reply_payload(user_id: int) -> Optional[dict]:
    """用户在私聊里跟小火人说话 → 生成一条回复消息 payload（已入库，待广播）。"""
    cfg = get_config()
    aid = ai_user_id()
    if not cfg.get("enabled") or not aid or user_id == aid:
        return None
    room = CL.pm_room(user_id, aid)
    if not _cooldown_ok(room):
        return None
    msgs = [{"role": "system", "content": str(cfg.get("persona_private") or "")}]
    msgs += _hist_messages(room, int(cfg.get("max_context") or 20), is_group=False)
    ok, text = _call_model(msgs)
    return CL.insert_chat(aid, "text", text, None, to_id=user_id, mentions="")


def group_reply_payload() -> Optional[dict]:
    """群聊里被 @ → 生成一条群回复 payload（已入库，待广播）。失败就安静，不发群。"""
    cfg = get_config()
    aid = ai_user_id()
    if not cfg.get("enabled") or not aid:
        return None
    if not _cooldown_ok("group"):
        return None
    if _daily_count("group", aid) >= int(cfg.get("group_daily_limit") or 300):
        return None
    msgs = [{"role": "system", "content": str(cfg.get("persona_group") or "")}]
    msgs += _hist_messages("group", int(cfg.get("max_context") or 20), is_group=True)
    ok, text = _call_model(msgs)
    if not ok:
        return None
    return CL.insert_chat(aid, "text", text, None, to_id=None, mentions="")


_stream_tasks: set = set()


async def _stream_send(obj: dict, target_user: Optional[int]) -> None:
    """私聊只发给对话用户；群聊广播。"""
    if target_user:
        await hub.send_user(int(target_user), obj)
    else:
        await hub.broadcast_all(obj)


async def _stream_reply(user: dict, payload: dict, to_id: Optional[int]) -> None:
    """流式生成并推送 AI 回复；结束后落库一次并广播正式消息。"""
    stream_id = uuid.uuid4().hex
    started = False
    target = int(user["id"])
    is_private = to_id is not None
    try:
        cfg = get_config()
        if not cfg.get("enabled"):
            return
        aid = ai_user_id()
        if not aid or target == aid:
            return
        room = CL.pm_room(target, aid) if is_private else "group"
        if not _cooldown_ok(room):
            return
        if not is_private and _daily_count("group", aid) >= int(cfg.get("group_daily_limit") or 300):
            return
        ai = query_one("SELECT id, nickname, avatar FROM users WHERE id = ?", (aid,))
        persona = cfg.get("persona_private") if is_private else cfg.get("persona_group")
        msgs = [{"role": "system", "content": str(persona or "")}]
        msgs += _hist_messages(room, int(cfg.get("max_context") or 20), is_group=not is_private)

        await _stream_send({
            "type": "ai_stream", "phase": "start", "streamId": stream_id,
            "room": room, "fromId": aid,
            "fromName": (ai["nickname"] if ai and ai["nickname"] else display_name()),
            "fromAvatar": (ai["avatar"] if ai else "") or "",
            "toId": target if is_private else None,
            "createdAt": security.now_ms(),
        }, target if is_private else None)
        started = True

        loop = asyncio.get_running_loop()
        q: asyncio.Queue = asyncio.Queue()

        def worker():
            try:
                for kind, text in _call_model_stream(msgs):
                    loop.call_soon_threadsafe(q.put_nowait, (kind, text))
            except Exception as exc:
                loop.call_soon_threadsafe(q.put_nowait, ("error", f"（小火人开小差了：{exc}）"))
            finally:
                loop.call_soon_threadsafe(q.put_nowait, ("done", ""))

        threading.Thread(target=worker, name="ai-stream", daemon=True).start()

        full: list[str] = []
        buf: list[str] = []
        err = ""
        last_sent = 0.0
        while True:
            kind, text = await q.get()
            if kind == "delta":
                full.append(text)
                buf.append(text)
                now = time.monotonic()
                if now - last_sent >= 0.06:
                    await _stream_send({"type": "ai_stream", "phase": "delta",
                                        "streamId": stream_id, "text": "".join(buf)},
                                       target if is_private else None)
                    buf.clear()
                    last_sent = now
            elif kind == "error":
                err = text
                if not full:
                    await _stream_send({"type": "ai_stream", "phase": "error",
                                        "streamId": stream_id, "text": text},
                                       target if is_private else None)
            elif kind == "done":
                break
        if buf:
            await _stream_send({"type": "ai_stream", "phase": "delta",
                                "streamId": stream_id, "text": "".join(buf)},
                               target if is_private else None)

        final_text = "".join(full).strip()
        if err:
            final_text = (final_text + "\n\n" + err).strip() if final_text else err
        if not final_text:
            final_text = "（小火人没返回内容）"
        rep = CL.insert_chat(aid, "text", final_text, None,
                             to_id=target if is_private else None, mentions="")
        rep["streamId"] = stream_id
        await CL.broadcast_chat(rep)
    except Exception as exc:
        log.warning("流式回复异常：%s", exc)
        if started:
            try:
                await _stream_send({"type": "ai_stream", "phase": "error",
                                    "streamId": stream_id,
                                    "text": f"（小火人开小差了：{exc}）"},
                                   target if is_private else None)
            except Exception:
                pass


async def maybe_reply(user: dict, payload: dict, to_id: Optional[int]) -> None:
    """ws.py 在每条聊天消息落库后调用：判断是否该让小火人回话（后台流式任务）。"""
    global _loop
    cfg = get_config()
    if not cfg.get("enabled"):
        return
    aid = ai_user_id()
    if not aid or payload.get("fromId") == aid:
        return
    try:
        _loop = asyncio.get_running_loop()
    except Exception:
        pass
    task = None
    if to_id == aid:
        task = asyncio.create_task(_stream_reply(user, payload, aid))
    elif payload.get("room") == "group" and aid in (payload.get("mentions") or []):
        task = asyncio.create_task(_stream_reply(user, payload, None))
    if task is not None:
        _stream_tasks.add(task)
        task.add_done_callback(_stream_tasks.discard)


# ---------------------------------------------------------------- 主动发言
def _broadcast_from_thread(payload: dict) -> None:
    if not payload or _loop is None:
        return
    try:
        asyncio.run_coroutine_threadsafe(CL.broadcast_chat(payload), _loop)
    except Exception as exc:
        log.warning("主动发言广播失败：%s", exc)


def _active_speak_once() -> None:
    cfg = get_config()
    if not cfg.get("enabled") or not cfg.get("active_speak"):
        return
    aid = ai_user_id()
    if not aid:
        return
    now = security.now_ms()
    last = query_one("SELECT created_at FROM messages WHERE room = 'group' ORDER BY id DESC LIMIT 1")
    if not last:
        return
    gap_sec = int(cfg.get("active_gap_sec") or 600)
    idle = (now - int(last["created_at"])) / 1000.0
    # 群要"刚热闹过又冷下来"才值得抛话题：静默 >= gap，且这个 gap 之前 30 分钟内有聊天
    if idle < gap_sec or idle > gap_sec + 1800:
        return
    if _daily_count("group", aid) >= int(cfg.get("active_daily_limit") or 20):
        return
    if not _cooldown_ok("group"):
        return
    msgs = [{"role": "system", "content": str(cfg.get("persona_group") or "")},
            {"role": "system",
             "content": "现在群里安静下来了，主动抛一个轻松的话题或问一句，简短（不超过 2 句），别 @ 所有人。"}]
    msgs += _hist_messages("group", int(cfg.get("max_context") or 20), is_group=True)
    ok, text = _call_model(msgs)
    if ok:
        _broadcast_from_thread(CL.insert_chat(aid, "text", text, None, to_id=None, mentions=""))


def _active_speak_loop() -> None:
    while True:
        time.sleep(30)
        try:
            _active_speak_once()
        except Exception as exc:
            log.warning("主动发言循环异常：%s", exc)


def start_active_speak() -> None:
    global _active_thread_started
    if _active_thread_started:
        return
    _active_thread_started = True
    threading.Thread(target=_active_speak_loop, name="ai-active-speak", daemon=True).start()
