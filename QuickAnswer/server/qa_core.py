"""题库核心：归一化、相似度、选项乱序映射、检索、投票聚合。

这个文件是 QuickAnswer 的增量模块，放进 shenmi-server 的 server/app/ 下即可，
不需要改动任何现有文件（除了 main.py 加一行 include_router）。

设计约束（来自实际使用场景：4 个用户、几十道题）：
  - 不引入 PostgreSQL / Redis / FTS，SQLite 全表扫几十条是微秒级；
  - 但检索接口按两段式写好，题目涨到几万条时不用重写（靠 stem_head 索引粗筛）；
  - 答案以「文本」为一等公民，字母只是某一时刻的排序结果。
"""
from __future__ import annotations

import json
import re
import time
import unicodedata
from collections import Counter
from typing import Any, Optional

from .db import cursor, execute, query_all, query_one

# ============================================================ 建表

SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS qa_questions (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    type          TEXT NOT NULL DEFAULT 'choice',
    stem          TEXT NOT NULL,
    stem_norm     TEXT NOT NULL,
    stem_head     TEXT NOT NULL DEFAULT '',
    options       TEXT NOT NULL DEFAULT '[]',
    answer_letter TEXT,
    answer_text   TEXT NOT NULL DEFAULT '',
    subject       TEXT DEFAULT '',
    status        TEXT NOT NULL DEFAULT 'pending',
    quality       REAL NOT NULL DEFAULT 0.5,
    hit_count     INTEGER NOT NULL DEFAULT 0,
    created_by    INTEGER,
    created_at    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_qa_head   ON qa_questions(stem_head, status);
CREATE INDEX IF NOT EXISTS idx_qa_status ON qa_questions(status, id);

CREATE TABLE IF NOT EXISTS qa_variants (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    question_id INTEGER NOT NULL,
    stem_norm   TEXT NOT NULL,
    raw_ocr     TEXT DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_qav_qid ON qa_variants(question_id);

CREATE TABLE IF NOT EXISTS qa_votes (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    question_id   INTEGER NOT NULL,
    answer_letter TEXT,
    answer_text   TEXT NOT NULL DEFAULT '',
    source        TEXT NOT NULL DEFAULT 'ai',
    model         TEXT DEFAULT '',
    confidence    REAL DEFAULT 0,
    weight        REAL NOT NULL DEFAULT 1,
    user_id       INTEGER,
    created_at    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_qav_q ON qa_votes(question_id, id);

CREATE TABLE IF NOT EXISTS qa_models (
    id         TEXT PRIMARY KEY,
    name       TEXT NOT NULL,
    base_url   TEXT NOT NULL,
    api_key    TEXT NOT NULL DEFAULT '',
    model_name TEXT NOT NULL,
    role       TEXT DEFAULT 'fast',
    enabled    INTEGER NOT NULL DEFAULT 1,
    sort       INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS qa_prompts (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    type       TEXT NOT NULL,
    version    INTEGER NOT NULL DEFAULT 1,
    template   TEXT NOT NULL,
    enabled    INTEGER NOT NULL DEFAULT 1,
    created_at INTEGER NOT NULL
);
"""

_schema_ready = False


def ensure_schema() -> None:
    """懒建表。放在首次请求里执行，避免依赖 main.py 的 startup 钩子。"""
    global _schema_ready
    if _schema_ready:
        return
    with cursor() as cur:
        cur.executescript(SCHEMA_SQL)
    _seed_defaults()
    _schema_ready = True


def _seed_defaults() -> None:
    if not query_one("SELECT id FROM qa_prompts LIMIT 1"):
        for t, tpl in DEFAULT_PROMPTS.items():
            execute(
                "INSERT INTO qa_prompts (type, version, template, enabled, created_at) VALUES (?,?,?,1,?)",
                (t, 1, tpl, now_ms()),
            )


def now_ms() -> int:
    return int(time.time() * 1000)


# ============================================================ 文本处理

_PUNCT_CATS = ("P", "S", "Z", "C")


def normalize(text: Optional[str]) -> str:
    """NFKC → 去掉空白/标点/符号/不可见字符 → 小写。

    目的只有一个：让 OCR 读错的字和原题尽量落进同一个形状，
    这样 3-gram Dice 才能把「打即机」和「打印机」判成相似。
    """
    if not text:
        return ""

    s = unicodedata.normalize("NFKC", str(text))
    out: list[str] = []
    for ch in s:
        cat = unicodedata.category(ch)
        if cat[0] in _PUNCT_CATS:
            continue
        out.append(ch.lower())
    return "".join(out)


def _grams(s: str, n: int) -> Counter:
    if len(s) < n:
        n = max(1, len(s))
    c: Counter = Counter()
    for i in range(len(s) - n + 1):
        c[s[i:i + n]] += 1
    return c


def dice(a: str, b: str) -> float:
    """字符 n-gram 的 Dice 系数。对「错一个字」比编辑距离更宽容 —— 但只对长文本成立。"""
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0

    n = 3 if min(len(a), len(b)) >= 3 else 2
    ca, cb = _grams(a, n), _grams(b, n)

    inter = sum((ca & cb).values())
    total = sum(ca.values()) + sum(cb.values())
    return 2.0 * inter / total if total else 0.0


def _lev(a: str, b: str) -> int:
    """编辑距离（滚动数组）。只用于短文本，长度个位数，性能无所谓。"""
    if a == b:
        return 0
    if not a:
        return len(b)
    if not b:
        return len(a)

    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def option_similarity(a: str, b: str) -> float:
    """选项相似度。

    必须按长度分流，否则会翻车：
      选项「打印机」被 OCR 读成「打即机」，3 个字错 1 个 → 两个 3-gram 全不重合，
      n-gram Dice 直接归零，而人工看只觉得「错了一个字」。
    5 字以内改用编辑距离（这里给 0.667），5 字以上仍用 n-gram（对整段错位更鲁棒）。
    """
    if not a or not b:
        return 0.0
    if a == b:
        return 1.0

    if min(len(a), len(b)) <= 5:
        return 1.0 - _lev(a, b) / max(len(a), len(b))

    return dice(a, b)


# ============================================================ 选项乱序映射

FRAGILE_PHRASES = ("以上都", "都不对", "以上均", "全部正确", "全部错误", "以上答案", "全都对", "都不正确")

_SPLIT_KEYS = re.compile(r"[,，;；/、]")


def split_keys(letter: Optional[str]) -> list[str]:
    if not letter:
        return []
    return [k.strip().upper() for k in _SPLIT_KEYS.split(str(letter)) if k.strip()]


def _norm_option(text: str, key: str) -> str:
    n = normalize(text)
    if not n:
        n = normalize(key)

    if n in ("正确", "对", "是", "t", "true", "yes"):
        return "对"
    if n in ("错误", "错", "否", "f", "false", "no"):
        return "错"
    return n


def map_options(local_options: list[dict], db_options: list[dict], db_answer: str) -> tuple[bool, str, str, bool]:
    """把题库答案字母翻译成本地选项字母。

    返回 (成功, 本地答案字母, 失败原因, 是否含无区分度选项)

    这是整个系统里最不能出错的一段：题库答案 C 指的是「打印机」，
    用户屏幕上「打印机」被打乱到了 A，直接照搬 C 就是错误答案。
    """
    if not local_options or not db_options:
        return False, "", "选项为空", False

    if len(local_options) != len(db_options):
        return False, "", f"选项数量不一致（本地 {len(local_options)} / 题库 {len(db_options)}）", False

    ln = [_norm_option(o.get("text", ""), o.get("key", "")) for o in local_options]
    dn = [_norm_option(o.get("text", ""), o.get("key", "")) for o in db_options]

    fragile = any(p in (o.get("text") or "") for o in db_options for p in FRAGILE_PHRASES) \
        or any(p in (o.get("text") or "") for o in local_options for p in FRAGILE_PHRASES)

    # 选项之间高度相似（增大/增加/减少/减小）时提高阈值，宁可失败也不串位
    threshold = 0.65
    for i in range(len(dn)):
        for j in range(i + 1, len(dn)):
            if option_similarity(dn[i], dn[j]) >= 0.85:
                threshold = 0.92
                break
        else:
            continue
        break

    pairs = []
    for i in range(len(db_options)):
        for j in range(len(local_options)):
            pairs.append((option_similarity(dn[i], ln[j]), i, j))
    pairs.sort(reverse=True)

    mapping: dict[str, str] = {}
    used_local: set[int] = set()

    for sim, i, j in pairs:
        if sim < threshold:
            break
        dk = str(db_options[i].get("key", "")).upper()
        if dk in mapping or j in used_local:
            continue
        mapping[dk] = str(local_options[j].get("key", "")).upper()
        used_local.add(j)

    if len(mapping) != len(db_options):
        missing = [str(o.get("text", "")) for o in db_options
                   if str(o.get("key", "")).upper() not in mapping]
        return False, "", f"仅匹配 {len(mapping)}/{len(db_options)} 个选项（缺：{'/'.join(missing)}）", fragile

    out: list[str] = []
    for k in split_keys(db_answer):
        if k not in mapping:
            return False, "", f"题库答案 {k} 不在选项内", fragile
        out.append(mapping[k])

    if not out:
        return False, "", "题库答案为空的映射结果", fragile

    return True, ",".join(dict.fromkeys(out)), "", fragile


# ============================================================ 检索

def _load_options(raw: Any) -> list[dict]:
    try:
        v = json.loads(raw or "[]")
        return v if isinstance(v, list) else []
    except (ValueError, TypeError):
        return []


def search(stem: str, options: list[dict], qtype: str = "choice") -> dict:
    """两段式检索：索引粗筛 → Python 精排 → 选项映射校验。"""
    ensure_schema()

    stem_norm = normalize(stem)
    if len(stem_norm) < 4:
        return {"hit": False, "reason": "题干过短"}

    rows = _candidates(stem_norm)
    if not rows:
        return {"hit": False, "reason": "题库无候选"}

    best: Optional[dict] = None
    best_score = 0.0
    best_reason = "相似度过低"

    for row in rows:
        db_stem = row["stem_norm"] or ""
        stem_sim = dice(stem_norm, db_stem)

        db_opts = _load_options(row["options"])
        opt_sim = 0.0
        if db_opts and options:
            opt_sim = _avg_option_similarity(options, db_opts)

        score = 0.7 * stem_sim + 0.3 * opt_sim if db_opts else stem_sim

        if score <= best_score:
            continue

        best_score = score

        if db_opts:
            ok, local_key, reason, _fragile = map_options(options, db_opts, row["answer_letter"] or "")
            if not ok:
                best_reason = reason
                continue
            answer_letter = local_key
        else:
            answer_letter = row["answer_letter"] or ""

        best = {
            "hit": True,
            "question_id": row["id"],
            "score": round(score, 4),
            "answer_letter": answer_letter,
            "answer_text": row["answer_text"] or "",
            "confidence": "high" if score >= 0.92 else "low",
            "source": "db",
            "status": row["status"],
        }

    if best is None:
        return {"hit": False, "reason": best_reason, "best_score": round(best_score, 4)}

    execute("UPDATE qa_questions SET hit_count = hit_count + 1 WHERE id = ?", (best["question_id"],))
    return best


def _candidates(stem_norm: str) -> list:
    """索引粗筛。题目少的时候直接全表，题目多了靠 stem_head 索引。"""
    head8 = stem_norm[:8]
    head6 = stem_norm[:6]

    rows = query_all(
        "SELECT * FROM qa_questions WHERE status IN ('verified','pending') "
        "AND (stem_head = ? OR stem_head = ?) LIMIT 80",
        (head8, head6),
    )

    if len(rows) >= 5:
        return rows

    # OCR 开头就可能错字，head 对不上。题库还小，扫一遍更稳。
    total = query_one("SELECT COUNT(*) AS c FROM qa_questions")["c"]
    if total and total <= 20000:
        return query_all(
            "SELECT * FROM qa_questions WHERE status IN ('verified','pending') "
            "ORDER BY id DESC LIMIT 3000"
        )

    return rows


def _avg_option_similarity(local: list[dict], db: list[dict]) -> float:
    if not local or not db:
        return 0.0
    n = min(len(local), len(db))
    total = 0.0
    for i in range(n):
        a = _norm_option(local[i].get("text", ""), local[i].get("key", ""))
        b = _norm_option(db[i].get("text", ""), db[i].get("key", ""))
        total += option_similarity(a, b)
    return total / n


# ============================================================ 上报与投票

def contribute(
    stem: str,
    options: list[dict],
    qtype: str,
    answer_letter: Optional[str],
    answer_text: str,
    source: str = "ai",
    model: str = "",
    confidence: float = 0.0,
    raw_ocr: str = "",
    user_id: Optional[int] = None,
) -> dict:
    """写库。同题不新增，转成一条投票。"""
    ensure_schema()

    stem_norm = normalize(stem)
    if len(stem_norm) < 4:
        return {"ok": False, "reason": "题干过短"}

    rows = query_all(
        "SELECT * FROM qa_questions WHERE status IN ('verified','pending') ORDER BY id DESC LIMIT 3000"
    )

    for row in rows:
        if dice(stem_norm, row["stem_norm"] or "") >= 0.95:
            _add_vote(row["id"], answer_letter, answer_text, source, model, confidence, user_id)

            if raw_ocr:
                execute(
                    "INSERT INTO qa_variants (question_id, stem_norm, raw_ocr) VALUES (?,?,?)",
                    (row["id"], stem_norm, raw_ocr[:4000]),
                )

            _aggregate(row["id"])
            return {"question_id": row["id"], "merged": True, "status": _status_of(row["id"])}

    qid = execute(
        "INSERT INTO qa_questions "
        "(type, stem, stem_norm, stem_head, options, answer_letter, answer_text, status, created_by, created_at) "
        "VALUES (?,?,?,?,?,?,?,?,?,?)",
        (qtype, stem, stem_norm, stem_norm[:8], json.dumps(options, ensure_ascii=False),
         answer_letter, answer_text, "pending", user_id, now_ms()),
    )

    if raw_ocr:
        execute("INSERT INTO qa_variants (question_id, stem_norm, raw_ocr) VALUES (?,?,?)",
                (qid, stem_norm, raw_ocr[:4000]))

    _add_vote(qid, answer_letter, answer_text, source, model, confidence, user_id)
    _aggregate(qid)

    return {"question_id": qid, "merged": False, "status": _status_of(qid)}


def _status_of(qid: int) -> str:
    row = query_one("SELECT status FROM qa_questions WHERE id = ?", (qid,))
    return row["status"] if row else "pending"


def _add_vote(qid: int, letter: Optional[str], text: str, source: str, model: str,
              confidence: float, user_id: Optional[int]) -> None:
    # 权重：人工标注 10 / AI 高置信 3 / AI 低置信 1
    if source == "manual":
        weight = 10.0
    elif confidence >= 0.9:
        weight = 3.0
    elif confidence >= 0.6:
        weight = 1.5
    else:
        weight = 1.0

    execute(
        "INSERT INTO qa_votes "
        "(question_id, answer_letter, answer_text, source, model, confidence, weight, user_id, created_at) "
        "VALUES (?,?,?,?,?,?,?,?,?)",
        (qid, letter, text, source, model, confidence, weight, user_id, now_ms()),
    )


def _aggregate(qid: int) -> None:
    """按权重投票。占比高且票数够 → verified；平票 → conflict（检索时剔除）。

    为什么不「上传即生效」：AI 会答错，OCR 也会错。任何一条都直接生效的话，
    第二个人查到的就是错的，错误会自己强化。两阶段是题库质量的下限保障。
    """
    rows = query_all("SELECT answer_letter, answer_text, weight FROM qa_votes WHERE question_id = ?", (qid,))
    if not rows:
        return

    tally: dict[str, float] = {}
    texts: dict[str, str] = {}
    for r in rows:
        key = (r["answer_letter"] or "").strip().upper() or normalize(r["answer_text"])[:20]
        tally[key] = tally.get(key, 0.0) + float(r["weight"] or 1)
        texts.setdefault(key, r["answer_text"] or "")

    total = sum(tally.values())
    if total <= 0:
        return

    ordered = sorted(tally.items(), key=lambda kv: kv[1], reverse=True)
    top_key, top_weight = ordered[0]
    share = top_weight / total

    vote_count = len(rows)
    status = "pending"
    if vote_count >= 3 and share >= 0.85:
        status = "verified"
    elif len(ordered) > 1 and share < 0.65:
        status = "conflict"

    execute(
        "UPDATE qa_questions SET status = ?, answer_text = ?, quality = ? WHERE id = ?",
        (status, texts.get(top_key, ""), round(share, 3), qid),
    )

    if top_key and len(top_key) <= 3:
        execute("UPDATE qa_questions SET answer_letter = ? WHERE id = ?", (top_key, qid))


# ============================================================ AI 提示词

DEFAULT_PROMPTS: dict[str, str] = {
    "default": (
        "你是答题助手。根据题目给出最可能的正确答案。\n\n"
        "题目类型：{{type}}\n题干：{{stem}}\n选项：{{options}}\n\n"
        "要求：\n"
        "1. 只输出 JSON，不要 markdown 代码块，不要任何前后缀解释。\n"
        '2. 格式：{"type":"choice|multi|judge|fill|short","answer_letter":"A","answer_text":"...",'
        '"explain":"不超过40字","confidence":0.0-1.0}\n'
        "3. 选择题：answer_letter 必须是给定选项中的一个字母；answer_text 原样复制该选项文本。\n"
        "4. 多选题：answer_letter 用英文逗号分隔，如 \"A,C\"，按字母升序。\n"
        '5. 判断题：answer_letter 固定为 "T" 或 "F"，answer_text 为 "正确" 或 "错误"。\n'
        "6. 填空题：answer_letter 留空，answer_text 只给答案本身，多个空用「；」分隔。\n"
        "7. 简答题：answer_letter 留空，answer_text 分点作答，每点不超过 40 字，总长不超过 200 字。\n"
        "8. 信息不足以确定时 confidence 给 0.4 以下，不要编造。"
    ),
}


def build_prompt(qtype: str, stem: str, options: list[dict]) -> str:
    ensure_schema()

    row = query_one(
        "SELECT template FROM qa_prompts WHERE type = ? AND enabled = 1 ORDER BY version DESC LIMIT 1",
        (qtype,),
    )
    if not row:
        row = query_one(
            "SELECT template FROM qa_prompts WHERE type = 'default' AND enabled = 1 ORDER BY version DESC LIMIT 1",
            (qtype,),
        )

    template = row["template"] if row else DEFAULT_PROMPTS["default"]

    opt_text = "\n".join(f"{o.get('key', '')}. {o.get('text', '')}" for o in options) or "（无选项）"

    return (template
            .replace("{{type}}", qtype)
            .replace("{{stem}}", stem)
            .replace("{{options}}", opt_text))


# ============================================================ 模型配置

def list_models(include_secret: bool = False) -> list[dict]:
    ensure_schema()
    rows = query_all("SELECT * FROM qa_models ORDER BY sort ASC, id ASC")
    out = []
    for r in rows:
        item = {
            "id": r["id"],
            "name": r["name"],
            "base_url": r["base_url"],
            "model_name": r["model_name"],
            "role": r["role"],
            "enabled": bool(r["enabled"]),
        }
        if include_secret:
            item["api_key"] = r["api_key"]
        out.append(item)
    return out


def get_model(model_id: Optional[str] = None) -> Optional[dict]:
    ensure_schema()

    if model_id:
        row = query_one("SELECT * FROM qa_models WHERE id = ? AND enabled = 1", (model_id,))
        if row:
            return dict(row)

    row = query_one("SELECT * FROM qa_models WHERE enabled = 1 ORDER BY sort ASC LIMIT 1")
    return dict(row) if row else None


def save_model(model_id: str, name: str, base_url: str, api_key: str,
               model_name: str, role: str = "fast", enabled: bool = True, sort: int = 0) -> None:
    ensure_schema()
    execute(
        "INSERT INTO qa_models (id, name, base_url, api_key, model_name, role, enabled, sort, created_at) "
        "VALUES (?,?,?,?,?,?,?,?,?) "
        "ON CONFLICT(id) DO UPDATE SET name=excluded.name, base_url=excluded.base_url, "
        "api_key=excluded.api_key, model_name=excluded.model_name, role=excluded.role, "
        "enabled=excluded.enabled, sort=excluded.sort",
        (model_id, name, base_url.rstrip("/"), api_key, model_name, role, 1 if enabled else 0, sort, now_ms()),
    )


def delete_model(model_id: str) -> None:
    ensure_schema()
    execute("DELETE FROM qa_models WHERE id = ?", (model_id,))


def parse_ai_json(text: str) -> Optional[dict]:
    """AI 常常会在 JSON 外面套 ```json 围栏，或者前后加一句话。这里只取最外层 {}。"""
    if not text:
        return None

    s = text.strip()
    if s.startswith("```"):
        s = re.sub(r"^```[a-zA-Z]*\s*", "", s)
        s = re.sub(r"\s*```$", "", s).strip()

    start = s.find("{")
    end = s.rfind("}")
    if start < 0 or end <= start:
        return None

    try:
        return json.loads(s[start:end + 1])
    except (ValueError, TypeError):
        return None
