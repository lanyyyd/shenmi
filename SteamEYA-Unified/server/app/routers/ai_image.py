"""AI 自动配图：后台配置 + 测试 + 队列运维。

- GET  /api/admin/ai-image-config        读配置（Key 掩码）+ 队列统计
- POST /api/admin/ai-image-config        保存配置（Key 留空/掩码表示不修改）
- POST /api/admin/ai-image/test-prompt   只跑文本模型，返回示例提示词
- POST /api/admin/ai-image/test-generate 生成一张预览图（不写业务表）
- POST /api/admin/ai-image/scan          扫描未配图，可 dry-run / 批量入队
- GET  /api/admin/ai-image/jobs          任务列表
- POST /api/admin/ai-image/jobs/{id}/retry   重试
- POST /api/admin/ai-image/jobs/{id}/cancel  取消（排队中）
"""
from __future__ import annotations

from fastapi import APIRouter, Body, Depends, HTTPException

from .. import aigen
from ..db import execute_update, query_all, query_one
from ..deps import require_admin

router = APIRouter(prefix="/api", tags=["ai-image"])


@router.get("/admin/ai-image-config")
def get_config(user: dict = Depends(require_admin)):
    return {"ok": True, "config": aigen.masked_config(), "stats": aigen.stats(),
            "ready": aigen.is_ready()}


@router.post("/admin/ai-image-config")
def save_config(payload: dict = Body(...), user: dict = Depends(require_admin)):
    patch = dict(payload or {})
    # 掩码（****开头）或空值默认表示「不修改」；要清空需显式传 clear_xxx=true
    for k in ("text_api_key", "image_api_key"):
        v = str(patch.get(k) or "")
        if patch.pop("clear_" + k, False):
            patch[k] = ""
            continue
        if v.startswith("****") or v == "":
            patch.pop(k, None)
    aigen.save_config(patch)
    return {"ok": True, "config": aigen.masked_config(),
            "stats": aigen.stats(), "ready": aigen.is_ready()}


@router.post("/admin/ai-image/test-prompt")
def test_prompt(payload: dict = Body(...), user: dict = Depends(require_admin)):
    kind = "store" if str(payload.get("kind")) == "store" else "dish"
    name = str(payload.get("name") or "").strip()
    if not name:
        raise HTTPException(400, "请填写名称")
    out = aigen.preview_prompt(kind, name, str(payload.get("store") or ""),
                               str(payload.get("location") or ""), str(payload.get("siblings") or ""))
    return {"ok": True, **out}


@router.post("/admin/ai-image/test-generate")
def test_generate(payload: dict = Body(...), user: dict = Depends(require_admin)):
    kind = "store" if str(payload.get("kind")) == "store" else "dish"
    prompt = str(payload.get("prompt") or "").strip()
    if not prompt:
        name = str(payload.get("name") or "").strip()
        if not name:
            raise HTTPException(400, "请填写提示词或名称")
        prompt = aigen.preview_prompt(kind, name, str(payload.get("store") or ""),
                                      str(payload.get("location") or ""))["prompt"]
    cfg = aigen.get_config()
    size = str(payload.get("size") or (cfg.get("store_size") if kind == "store" else cfg.get("dish_size")))
    try:
        raw = aigen.generate_image(prompt, size)
        url = aigen._save_image(raw)
    except Exception as exc:
        raise HTTPException(400, str(exc)[:200])
    return {"ok": True, "url": url, "prompt": prompt, "size": size}


@router.post("/admin/ai-image/scan")
def scan(payload: dict = Body(...), user: dict = Depends(require_admin)):
    scope = str(payload.get("scope") or "all")
    if scope not in ("all", "stores", "dishes"):
        raise HTTPException(400, "scope 只能是 all / stores / dishes")
    limit = max(1, min(int(payload.get("limit") or 200), 500))
    dry = bool(payload.get("dryRun", True))
    return {"ok": True, "dryRun": dry, **aigen.scan(scope, limit, dry)}


@router.get("/admin/ai-image/jobs")
def jobs(status: str = "", limit: int = 50, offset: int = 0, user: dict = Depends(require_admin)):
    limit = max(1, min(int(limit or 50), 200))
    offset = max(0, int(offset or 0))
    if status:
        rows = query_all(
            "SELECT * FROM ai_image_jobs WHERE status = ? ORDER BY id DESC LIMIT ? OFFSET ?",
            (status, limit, offset))
        total = query_one("SELECT COUNT(*) AS c FROM ai_image_jobs WHERE status = ?", (status,))
    else:
        rows = query_all("SELECT * FROM ai_image_jobs ORDER BY id DESC LIMIT ? OFFSET ?", (limit, offset))
        total = query_one("SELECT COUNT(*) AS c FROM ai_image_jobs")
    return {"ok": True, "jobs": [dict(r) for r in rows],
            "total": int(total["c"] or 0) if total else 0, "stats": aigen.stats()}


@router.post("/admin/ai-image/jobs/{job_id}/retry")
def retry(job_id: int, user: dict = Depends(require_admin)):
    job = query_one("SELECT * FROM ai_image_jobs WHERE id = ?", (job_id,))
    if not job:
        raise HTTPException(404, "任务不存在")
    execute_update(
        "UPDATE ai_image_jobs SET status = 'queued', error = '', attempts = 0, updated_at = ? WHERE id = ?",
        (aigen._now(), job_id))
    aigen._set_target_status(str(job["kind"]), int(job["target_id"]), "queued")
    return {"ok": True}


@router.post("/admin/ai-image/jobs/{job_id}/cancel")
def cancel(job_id: int, user: dict = Depends(require_admin)):
    job = query_one("SELECT * FROM ai_image_jobs WHERE id = ?", (job_id,))
    if not job:
        raise HTTPException(404, "任务不存在")
    if str(job["status"]) not in ("queued", "running"):
        raise HTTPException(400, "只有排队中/生成中的任务可以取消")
    execute_update(
        "UPDATE ai_image_jobs SET status = 'canceled', error = '已手动取消', updated_at = ?, finished_at = ? "
        "WHERE id = ?", (aigen._now(), aigen._now(), job_id))
    aigen._set_target_status(str(job["kind"]), int(job["target_id"]), "")
    return {"ok": True}
