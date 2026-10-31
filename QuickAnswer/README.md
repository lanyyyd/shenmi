# QuickAnswer

屏幕取词搜题助手（Windows / WinUI 3）。框选屏幕上一块区域，自动 OCR 出题干与选项，
先查共享题库，未命中再问 AI，答案以小悬浮窗或直接在题目上标注的方式呈现。

配套后端模块直接装进现有的 `shenmi-server`（FastAPI + SQLite），复用它的账号体系。

---

## 目录

```
QuickAnswer/
├─ src/
│  ├─ QuickAnswer.Core/           纯逻辑：截图 / OCR / 解析 / 匹配 / 接口
│  └─ QuickAnswer.App/            WinUI 3 界面
└─ server/                        后端增量模块（装进 shenmi-server）
   ├─ qa_core.py                  归一化 / 相似度 / 选项映射 / 检索 / 投票
   ├─ qa_router.py                4 个客户端接口 + 管理接口
   ├─ admin-qa.html               网页后台：模型 / 题库 / 提示词
   └─ install.py                  一键装进 shenmi-server
```

---

## 装后端

```bash
cd QuickAnswer/server
python install.py /path/to/shenmi-server
```

脚本做三件事（幂等，重复执行安全）：

1. `qa_core.py` → `server/app/qa_core.py`
2. `qa_router.py` → `server/app/routers/qa.py`
3. `admin-qa.html` → `web/admin-qa.html`
4. 在 `server/app/main.py` 补一行 `include_router(qa.router)` 和 import

表结构在首次请求时自动创建（`qa_questions` / `qa_variants` / `qa_votes` / `qa_models` / `qa_prompts`），
**不需要手动执行任何 SQL**，也不会改动现有业务表。

重启后端，用管理员账号打开 `https://你的域名/admin-qa.html`：

1. 先在「AI 模型」里加一个模型（`base_url` 填到域名级，如 `https://api.deepseek.com`，程序自己拼 `/chat/completions`）
2. 题库一开始是空的，用户在客户端搜到新题后会自动落进来，状态为「待验证」
3. 觉得 AI 答得不准，可以在「提示词」里改一版

---

## 编客户端

Windows 上：

```powershell
dotnet build    QuickAnswer/src/QuickAnswer.App/QuickAnswer.App.csproj -c Release -p:Platform=x64
dotnet publish  QuickAnswer/src/QuickAnswer.App/QuickAnswer.App.csproj -c Release -r win-x64 -p:Platform=x64 -o out
```

推送到 `main` 且改动落在 `QuickAnswer/**` 时，GitHub Actions 会自动构建并发布到 Release（tag `qa-v1.0.N`）。

首次运行：

1. 填服务器地址 + 账号密码（用你 shenmi 账号直接登）
2. 点「框选区域」，把题干和全部选项一起框进去
3. 打开「自动循环」，翻到下一题时会自动识别

热键：`Alt+Q` 手动识别 · `Alt+W` 开关循环 · `Alt+D` 切隐蔽模式

---

## 两种呈现方式

**悬浮窗**（默认）：右下角 232×136 的小窗，深色半透明，选择题显示大字字母。
双击可收成一条横条。拖动可换位置。

**隐蔽模式**：不创建任何悬浮窗，答案直接画在题目上 ——
选择题/多选题在正确选项左侧打一个红色三角，判断题在括号处写 √/×，
填空题在题干下方写绿色答案。窗口对鼠标完全透明，点下去是点到下面的软件。

---

## 几个设计上非做不可的点

**选项乱序映射**：题库按「选项文本」存答案，字母只是索引。用户屏幕上选项被打乱后，
程序会做双向贪心匹配把字母翻译过来；只要有一项配不上就整体放弃、改问 AI。
宁可贵 2 秒，不给错答案。

**短选项要用编辑距离**：`打印机` 被 OCR 读成 `打即机` 时，3-gram 相似度是 **0**
（两个字没有任何共同的三字片段），但编辑距离是 0.667。所以 5 字以内走编辑距离，
5 字以上才用 n-gram。这个坑不踩过一次是想不起来的。

**dHash 预筛**：每秒截一次图但基本不做 OCR —— 先缩成 9×8 灰度算 64 位指纹，
和上一帧比对，没变就直接返回。静止时 CPU 占用不到 1%。

**题库两阶段**：AI 答完立刻上传，但状态是 `pending`；同题累到 3 票且占比 85% 才转
`verified`。上传即生效的话，AI 的一次错误会被后面所有人继承并放大。

**防截屏**：`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，让 DWM 合成时跳过窗口。
不注入、不 hook 任何进程，也不会被杀软拦。**但它防不了进程枚举** —— 对方如果开着
进程列表，仍然看得见 `QuickAnswer.exe`。

---

## 使用边界

这套工具适合：网课练习自查、刷题对答案、错题整理、OCR 取词。

**不适合**在正式考试/测评里使用 —— 属学术不端，查实通常取消成绩并记过。
技术上降低的是被发现概率，不改变事情的性质。这个判断留给你自己。
