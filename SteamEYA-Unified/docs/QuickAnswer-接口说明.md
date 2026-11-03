# QuickAnswer 题库接口说明

客户端（`QuickAnswer.exe`）用到的全部接口都在 `/api/v1/qa` 下，鉴权走网关现有的
Bearer JWT（`/api/auth/login` 拿到的 `access_token`）。

**部署位置**：`server/app/routers/qa.py` + `server/app/qa_core.py`。
随网关一起启动，不需要额外进程。

---

## 一、接口一览

| 方法 | 路径 | 作用 |
|---|---|---|
| POST | `/api/v1/qa/search` | 查共享题库（本地相似度匹配，不调模型） |
| POST | `/api/v1/qa/solve` | 题干 + 选项 → 问 AI，SSE 流式返回 |
| POST | `/api/v1/qa/solve-image` | **整张截图 → 多模态模型，跳过 OCR**，SSE 流式返回 |
| POST | `/api/v1/qa/contribute` | 客户端上报题目与答案 |
| GET | `/api/v1/qa/models` | 可用模型列表（不含 api_key） |
| GET/POST/DELETE | `/api/v1/qa/admin/*` | 管理后台接口，仅管理员 |

`solve` 和 `solve-image` 返回**完全相同的 SSE 结构**，客户端只写一套解析：

```
data: {"delta": "累计到当前的全部文本"}

data: {"done": true, "result": {"type":"choice","answer_letter":"B",
        "answer_text":"...","explain":"...","confidence":0.86},
        "model": "qwen2.5-vl-7b", "elapsed_ms": 3120, "saved": {...}}
```

失败时 `"result": null` 并带 `"error"`，客户端据此走失败分支。

---

## 二、AI 识图（`/solve-image`）

### 请求

```json
{
  "image": "data:image/png;base64,iVBORw0KGgo...",
  "model_id": "qwen-vl"
}
```

- `image`：Data URL，必须是 `data:image/...`。Base64 部分上限 **8MB**（约 6MB 原图），
  客户端会先把截图缩到 1600px，正常只有几百 KB。
- `image_url`：也支持公网 `http(s)://` 地址，二选一。
- 图片**不落盘、不入库**，只在这一次请求的内存里过一遍。

### 提示词

独立于文本模式，存在 `qa_prompts` 表里 `type='vision'` 的那一行，
可在 `/admin-qa.html → 提示词` 里改。老库里没有这一行，代码内置
`DEFAULT_VISION_PROMPT` 兜底，升级后立刻可用。

提示词要求模型额外返回 `stem` 和 `options`，服务端拿它做两件事：

1. **纠字母**。模型经常把「第 3 个选项」写成 C，却把 B 的原文抄进 `answer_text`。
   文本是它逐字读出来的，比它自己标的字母可信，两者打架时以文本为准。
2. **顺手入库**。题干 ≥ 8 字、选项 ≥ 2 个、`confidence ≥ 0.5` 才写库，
   下次同一题直接命中题库，不用再等模型几秒。

### 模型选择

`get_vision_model()` 的顺序：

1. 客户端传的 `model_id`，且配置看起来是多模态 → 用它
2. 数据库里 `role` 含 `vision` 的（`sort` 最前）
3. 客户端传的 `model_id`，就算不像多模态
4. `sort` 最前的任意启用模型

第 3 步之前插一个"换模型"，是因为客户端的 `model_id` 来自设置页那个**通用**下拉，
用户很可能选的是文本模型。图发过去上游直接 400，用户只看到"调用失败"，
根本想不到是选错模型。

**所以务必在后台把多模态模型的「能力」设成「多模态（看图）」**：

```
/admin-qa.html → 新增 / 修改模型 → 能力 = 多模态（能读截图）
```

### 超时

| 场景 | 连接 | 读取 |
|---|---|---|
| 文本 `solve` | 4s | 40s |
| 视觉 `solve-image` | 4s | **90s** |

视觉比文本慢得多（读 1600px 图 + 推理，20 秒起步），40 秒会在一半的题上
被自己掐断，看起来像"模型超时"，其实是客户端放弃太早。

> **反向代理要一起放大**：nginx 默认 `proxy_read_timeout` 是 60s，
> 会先于服务端把连接切掉。SSE 还要关缓冲：
>
> ```nginx
> location /api/v1/qa/ {
>     proxy_pass http://127.0.0.1:8897;
>     proxy_read_timeout 120s;
>     proxy_buffering off;
>     proxy_cache off;
>     add_header X-Accel-Buffering no;
> }
> ```

---

## 三、排障

| 现象 | 原因 | 处理 |
|---|---|---|
| `服务端还没有 /solve-image 接口`（客户端提示） | 还在跑旧版服务端 | 更新 `server/` 后重启 |
| `当前模型 xxx 可能不支持图片输入` | 用的模型不是多模态，或没标 `role=vision` | 后台新增多模态模型，能力选「多模态」 |
| `AI 接口超时` | 模型太慢，或 nginx 60s 掐断 | 换快一点的模型；放大 `proxy_read_timeout` |
| `模型没有返回可解析的 JSON` | 模型没按 JSON 输出 | 看后台提示词里 `type='vision'` 那条是否被改坏 |
| `图片太大` | 客户端没缩图 | 检查客户端版本（1.0.14+ 会缩到 1600px） |
| `答案与选项不匹配` | 文本模式：模型给的字母不在选项里 | 正常保护，重试一次通常就好 |

服务端日志关键字：

```
qa solve-image: model=... img_chars=... src=data-url role=vision
qa done: ok=True elapsed_ms=3120 letter='B' text_len=21
qa 入库：qid=1 merged=False status=verified
qa 无法解析：raw_len=0 reasoning_len=812 finish='length' error=''
```

最后一行说明模型的额度被思考过程吃光了 —— 把 `max_tokens` 调大，
或者换一个不带 reasoning 的模型。

---

## 四、数据表

| 表 | 说明 |
|---|---|
| `qa_questions` | 题目 + 答案 + 状态（`pending` / `verified` / `conflict` / `rejected`） |
| `qa_variants` | 同一题的不同 OCR 写法，提升匹配率 |
| `qa_votes` | 每次作答一条投票，用于聚合出最终答案 |
| `qa_models` | 模型配置（`api_key` 只存在服务端，客户端永远拿不到） |
| `qa_prompts` | 提示词模板，按 `type` + `version` 取最新一条 |

`qa_models.role` 的取值：`fast`（文本，默认）/ `vision`（多模态，能吃图片）。
