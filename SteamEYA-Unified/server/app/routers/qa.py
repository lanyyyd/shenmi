"""QuickAnswer 题库接口。

部署位置：shenmi-server/server/app/routers/qa.py
依赖文件：shenmi-server/server/app/qa_core.py

全部接口走现有的 Bearer JWT 鉴权（deps.get_current_user），
不信任客户端传来的 userId —— 否则任何人都能伪造别人的贡献记录。
"""
from __future__ import annotations

import json
import logging
import time
from typing import Any, Optional

import requests
from fastapi import APIRouter, Body, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse

from .. import qa_core as QA
from ..deps import get_current_user, require_admin

log = logging.getLogger("gateway.qa")

router = APIRouter(prefix="/api/v1/qa", tags=["qa"])

# 单次 AI 调用的上限。超过就直接报错，不要挂着让用户干等。
_AI_CONNECT_TIMEOUT = 4
_AI_READ_TIMEOUT = 40


# ============================================================ 检索

@router.post("/search")
def search(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    stem = str(payload.get("stem") or "").strip()
    options = payload.get("options") or []
    qtype = str(payload.get("type") or "choice")

    if not stem:
        raise HTTPException(400, "题干为空")

    if not isinstance(options, list):
        options = []

    try:
        return QA.search(stem, options, qtype)
    except Exception as exc:
        log.warning("题库检索失败：%s", exc)
        return {"hit": False, "reason": f"检索异常：{exc}"}


# ============================================================ AI 求解（SSE）

@router.post("/solve")
def solve(payload: dict = Body(...), request: Request = None,
          user: dict = Depends(get_current_user)):
    stem = str(payload.get("stem") or "").strip()
    options = payload.get("options") or []
    qtype = str(payload.get("type") or "choice")
    model_id = payload.get("model_id")

    if not stem:
        raise HTTPException(400, "题干为空")

    model = QA.get_model(model_id)
    if not model:
        raise HTTPException(503, "后台还没有配置可用的 AI 模型，请联系管理员")

    prompt = QA.build_prompt(qtype, stem, options if isinstance(options, list) else [])
    messages = [{"role": "user", "content": prompt}]

    # 诊断日志：只记结构和长度，不落题干原文（题目内容属于用户数据）。
    _opt0 = options[0] if isinstance(options, list) and options else None
    _ua = (request.headers.get("user-agent") if request else "") or ""
    _accept = (request.headers.get("accept") if request else "") or ""
    log.info("qa solve: model=%s type=%s stem_len=%d options=%s opt0_type=%s opt0_keys=%s ua=%r accept=%r",
             model.get("id"), qtype, len(stem),
             len(options) if isinstance(options, list) else "not-list",
             type(_opt0).__name__,
             sorted(_opt0.keys()) if isinstance(_opt0, dict) else "",
             _ua[:80], _accept[:80])

    def event(payload_obj: dict) -> str:
        return "data: " + json.dumps(payload_obj, ensure_ascii=False) + "\n\n"

    def generate():
        started = time.time()
        acc: list[str] = []
        error = ""
        reasoning_len = 0
        finish = ""

        try:
            for kind, piece in _call_model_stream(model, messages):
                if kind == "delta":
                    acc.append(piece)
                    yield event({"delta": "".join(acc)})
                elif kind == "reasoning":
                    reasoning_len += len(piece)
                elif kind == "finish":
                    finish = piece
                elif kind == "error":
                    error = piece
                    break
        except Exception as exc:                       # noqa: BLE001
            error = f"调用失败：{exc}"

        raw = "".join(acc).strip()
        parsed = QA.parse_ai_json(raw)

        if parsed is None:
            log.warning("qa solve 无法解析：raw_len=%d reasoning_len=%d finish=%r error=%r head=%r",
                        len(raw), reasoning_len, finish, error, raw[:160])
            yield event({
                "done": True,
                "result": None,
                "model": model.get("model_name"),
                "elapsed_ms": int((time.time() - started) * 1000),
                "error": error or ("模型没有返回可解析的 JSON" if raw else "模型无输出"),
            })
            return

        # 校验：字母必须在选项里，且与 answer_text 对得上
        # 不通过就让客户端走「失败」分支，宁可不给答案，也不给一个对不上的字母。
        result = _sanitize(parsed, options if isinstance(options, list) else [])
        log.info("qa solve done: ok=%s elapsed_ms=%d letter=%r text_len=%d",
                 bool(result), int((time.time() - started) * 1000),
                 (result or {}).get("answer_letter"), len((result or {}).get("answer_text") or ""))
        if result is None:
            log.warning("qa solve 答案与选项不匹配：letter=%r text=%r raw_len=%d head=%r",
                        parsed.get("answer_letter"), parsed.get("answer_text"), len(raw), raw[:120])
            yield event({
                "done": True,
                "result": None,
                "model": model.get("model_name"),
                "elapsed_ms": int((time.time() - started) * 1000),
                "error": "模型返回的答案与选项不匹配",
            })
            return

        # AI 成功作答：直接进题库并置为 verified（不等人审，立刻可被检索）。
        saved = None
        try:
            saved = QA.contribute(
                stem,
                options if isinstance(options, list) else [],
                qtype,
                result.get("answer_letter") or None,
                result.get("answer_text") or "",
                source="ai",
                model=str(model.get("model_name") or ""),
                confidence=float(result.get("confidence") or 0),
                user_id=(user or {}).get("id"),
                force_verified=True,
            )
            log.info("qa solve 入库：qid=%s merged=%s status=%s",
                     (saved or {}).get("question_id"), (saved or {}).get("merged"),
                     (saved or {}).get("status"))
        except Exception as exc:                       # noqa: BLE001
            log.warning("qa solve 入库失败：%s", exc)

        yield event({
            "done": True,
            "result": result,
            "saved": saved,
            "model": model.get("model_name"),
            "elapsed_ms": int((time.time() - started) * 1000),
        })

    return StreamingResponse(generate(), media_type="text/event-stream",
                             headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})


def _sanitize(parsed: dict, options: list[dict]) -> Optional[dict]:
    """把模型输出钉死在题目给定的选项上。"""
    qtype = str(parsed.get("type") or "choice").lower()
    letter = str(parsed.get("answer_letter") or "").strip().upper()
    text = str(parsed.get("answer_text") or "").strip()

    try:
        confidence = float(parsed.get("confidence") or 0)
    except (TypeError, ValueError):
        confidence = 0.0

    # 「AC」这种没分隔符的多选
    if len(letter) > 1 and letter.isalpha() and len(options) > 2:
        letter = ",".join(letter)

    keys = {str(o.get("key", "")).upper(): str(o.get("text", "")) for o in options}

    if keys:
        wanted = QA.split_keys(letter)
        if not wanted or any(k not in keys for k in wanted):
            return None

        if not text:
            text = "、".join(keys[k] for k in wanted)
        else:
            # 文本与字母至少要对得上一个，防止模型给「A」配「打印机」这种错位
            best = max((QA.dice(QA.normalize(text), QA.normalize(keys[k])) for k in wanted), default=0)
            if best < 0.5:
                text = "、".join(keys[k] for k in wanted)

    return {
        "type": qtype,
        "answer_letter": letter,
        "answer_text": text,
        "explain": str(parsed.get("explain") or "")[:200],
        "confidence": max(0.0, min(1.0, confidence)),
    }


def _call_model_stream(model: dict, messages: list[dict]):
    """OpenAI 兼容的流式调用。产出：
    ("delta", text)      正文增量（拼起来是最终答案）
    ("reasoning", text)  思考过程增量（只用于诊断/计数，不进答案）
    ("finish", reason)   上游 finish_reason
    ("error", msg)       失败原因

    这段 SSE 解析与 aicore._call_model_stream 同源：关键在于必须按 UTF-8 解码，
    否则某些中转不声明 charset 时 requests 会按 latin-1 解，中文直接变乱码。
    """
    base = str(model.get("base_url") or "").rstrip("/")
    key = str(model.get("api_key") or "").strip()
    name = str(model.get("model_name") or "")

    if not base or not key:
        yield "error", "模型未配置 base_url 或 api_key"
        return

    body = {
        "model": name,
        "messages": messages,
        "stream": True,
        # deepseek 这类带思考过程的模型：额度太小会被思考耗光、正文为空。
        # 按需求放到 60000（上游上限 65536）；max_tokens 只是上限，正常答题不会真跑满。
        "max_tokens": 60000,
        "temperature": 0.2,
    }

    resp = None
    try:
        resp = requests.post(
            base + "/chat/completions",
            json=body,
            timeout=(_AI_CONNECT_TIMEOUT, _AI_READ_TIMEOUT),
            stream=True,
            headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
        )

        if resp.status_code != 200:
            log.warning("AI 调用失败 HTTP %s: %s", resp.status_code, resp.text[:200])
            yield "error", f"AI 接口返回 HTTP {resp.status_code}"
            return

        ctype = (resp.headers.get("content-type") or "").lower()
        if "json" in ctype and "event-stream" not in ctype:
            # 部分中转忽略 stream，直接返回整包
            try:
                data = resp.json()
                content = str(((data.get("choices") or [{}])[0].get("message") or {}).get("content") or "")
            except (ValueError, TypeError):
                content = ""
            if content:
                yield "delta", content
            return

        got_content = False
        last_finish = ""
        reasoning_chars = 0
        for raw in resp.iter_lines(decode_unicode=False):
            if not raw:
                continue

            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue

            data = line[5:].strip()
            if not data:
                continue
            if data == "[DONE]":
                break

            try:
                obj = json.loads(data)
            except (ValueError, TypeError):
                continue

            # 有的中转把错误塞在 200 的流里
            if obj.get("error"):
                yield "error", str(obj.get("error"))[:200]
                return

            for ch in obj.get("choices") or []:
                delta = ch.get("delta") or {}
                rc = delta.get("reasoning_content")
                if rc:
                    reasoning_chars += len(str(rc))
                    yield "reasoning", str(rc)
                piece = delta.get("content") or ch.get("text")
                if piece:
                    got_content = True
                    yield "delta", str(piece)
                if ch.get("finish_reason"):
                    last_finish = str(ch["finish_reason"])
                    yield "finish", last_finish

        if not got_content:
            yield "error", (f"上游只返回了思考过程，没有正文（finish_reason={last_finish or '无'}，"
                            f"思考 {reasoning_chars} 字）")

    except requests.Timeout:
        yield "error", "AI 接口超时"
    except Exception as exc:                           # noqa: BLE001
        log.warning("AI 流式调用异常：%s", exc)
        yield "error", f"AI 调用异常：{exc}"
    finally:
        if resp is not None:
            try:
                resp.close()
            except Exception:                          # noqa: BLE001
                pass


# ============================================================ 上报题库

@router.post("/contribute")
def contribute(payload: dict = Body(...), user: dict = Depends(get_current_user)):
    stem = str(payload.get("stem") or "").strip()
    if not stem:
        raise HTTPException(400, "题干为空")

    options = payload.get("options") or []
    if not isinstance(options, list):
        options = []

    try:
        confidence = float(payload.get("confidence") or 0)
    except (TypeError, ValueError):
        confidence = 0.0

    answer_letter = payload.get("answer_letter")
    answer_text = str(payload.get("answer_text") or "")
    source = str(payload.get("source") or "ai")

    result = QA.contribute(
        stem=stem,
        options=options,
        qtype=str(payload.get("type") or "choice"),
        answer_letter=answer_letter,
        answer_text=answer_text,
        source=source,
        model=str(payload.get("model") or ""),
        confidence=confidence,
        raw_ocr=str(payload.get("raw_ocr") or ""),
        user_id=int(user["id"]),
        # AI 已给出答案的上报直接通过，不进人工审核队列（立即对所有人生效）。
        force_verified=(source == "ai" and bool(answer_letter or answer_text)),
    )

    return result


# ============================================================ 模型列表

@router.get("/models")
def models(user: dict = Depends(get_current_user)):
    return [m for m in QA.list_models() if m.get("enabled")]


# ============================================================ 管理接口（网页后台）

@router.get("/admin/models")
def admin_list_models(user: dict = Depends(require_admin)):
    return {"ok": True, "items": QA.list_models(include_secret=True)}


@router.post("/admin/models")
def admin_save_model(payload: dict = Body(...), user: dict = Depends(require_admin)):
    model_id = str(payload.get("id") or "").strip()
    if not model_id:
        raise HTTPException(400, "缺少 id")

    QA.save_model(
        model_id=model_id,
        name=str(payload.get("name") or model_id),
        base_url=str(payload.get("base_url") or ""),
        api_key=str(payload.get("api_key") or ""),
        model_name=str(payload.get("model_name") or model_id),
        role=str(payload.get("role") or "fast"),
        enabled=bool(payload.get("enabled", True)),
        sort=int(payload.get("sort") or 0),
    )
    return {"ok": True}


@router.delete("/admin/models/{model_id}")
def admin_delete_model(model_id: str, user: dict = Depends(require_admin)):
    QA.delete_model(model_id)
    return {"ok": True}


@router.get("/admin/questions")
def admin_list_questions(
    status: Optional[str] = None,
    limit: int = 100,
    user: dict = Depends(require_admin),
):
    QA.ensure_schema()
    from ..db import query_all

    if status:
        rows = query_all(
            "SELECT * FROM qa_questions WHERE status = ? ORDER BY id DESC LIMIT ?",
            (status, max(1, min(limit, 500))),
        )
    else:
        rows = query_all("SELECT * FROM qa_questions ORDER BY id DESC LIMIT ?",
                         (max(1, min(limit, 500)),))

    items = []
    for r in rows:
        items.append({
            "id": r["id"],
            "type": r["type"],
            "stem": r["stem"],
            "options": json.loads(r["options"] or "[]"),
            "answer_letter": r["answer_letter"],
            "answer_text": r["answer_text"],
            "status": r["status"],
            "quality": r["quality"],
            "hit_count": r["hit_count"],
            "created_at": r["created_at"],
        })

    return {"ok": True, "items": items}


@router.post("/admin/questions/{qid}/status")
def admin_set_status(qid: int, payload: dict = Body(...), user: dict = Depends(require_admin)):
    status = str(payload.get("status") or "").strip()
    if status not in ("pending", "verified", "conflict", "rejected"):
        raise HTTPException(400, "非法状态")

    QA.ensure_schema()
    from ..db import execute
    execute("UPDATE qa_questions SET status = ? WHERE id = ?", (status, qid))
    return {"ok": True}


@router.delete("/admin/questions/{qid}")
def admin_delete_question(qid: int, user: dict = Depends(require_admin)):
    QA.ensure_schema()
    from ..db import execute
    execute("DELETE FROM qa_questions WHERE id = ?", (qid,))
    execute("DELETE FROM qa_votes WHERE question_id = ?", (qid,))
    execute("DELETE FROM qa_variants WHERE question_id = ?", (qid,))
    return {"ok": True}


@router.get("/admin/prompts")
def admin_list_prompts(user: dict = Depends(require_admin)):
    QA.ensure_schema()
    from ..db import query_all
    rows = query_all("SELECT * FROM qa_prompts ORDER BY type ASC, version DESC")
    return {"ok": True, "items": [dict(r) for r in rows]}


@router.post("/admin/prompts")
def admin_save_prompt(payload: dict = Body(...), user: dict = Depends(require_admin)):
    QA.ensure_schema()
    from ..db import execute

    ptype = str(payload.get("type") or "default").strip()
    template = str(payload.get("template") or "").strip()
    if not template:
        raise HTTPException(400, "模板不能为空")

    row = QA.query_one(
        "SELECT MAX(version) AS v FROM qa_prompts WHERE type = ?", (ptype,)
    )
    version = int(row["v"] or 0) + 1 if row else 1

    execute(
        "INSERT INTO qa_prompts (type, version, template, enabled, created_at) VALUES (?,?,?,1,?)",
        (ptype, version, template, QA.now_ms()),
    )
    return {"ok": True, "version": version}


@router.get("/admin/stats")
def admin_stats(user: dict = Depends(require_admin)):
    QA.ensure_schema()
    from ..db import query_one

    total = query_one("SELECT COUNT(*) AS c FROM qa_questions")["c"]
    verified = query_one("SELECT COUNT(*) AS c FROM qa_questions WHERE status='verified'")["c"]
    pending = query_one("SELECT COUNT(*) AS c FROM qa_questions WHERE status='pending'")["c"]
    conflict = query_one("SELECT COUNT(*) AS c FROM qa_questions WHERE status='conflict'")["c"]
    hits = query_one("SELECT IFNULL(SUM(hit_count),0) AS c FROM qa_questions")["c"]

    return {
        "ok": True,
        "total": total,
        "verified": verified,
        "pending": pending,
        "conflict": conflict,
        "hits": hits,
    }
