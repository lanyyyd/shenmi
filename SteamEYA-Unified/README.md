# SteamEYA 一体化上号器（统一工程）

把 **Steam 上号器**、**云游戏上号器（原神/星穹铁道/异环/哔哩哔哩）**、**聊天室**、**云存档** 合并为一个客户端 + 一套统一账号。
设计说明见 [docs/方案设计.md](docs/方案设计.md)。

---

## 目录结构

```
SteamEYA-Unified/
├── server/                统一后端网关（FastAPI，单服务合并）
│   ├── app/
│   │   ├── main.py        入口：挂载全部路由 + 静态前端 + WS
│   │   ├── config.py      配置（数据目录/密钥/上传限制/离线模式开关）
│   │   ├── db.py          SQLite 表结构（统一 users + 各业务表 + 云存档）
│   │   ├── security.py    scrypt 密码（兼容旧哈希）+ JWT + Fernet
│   │   ├── deps.py        鉴权（JWT 或 cookie 会话）
│   │   ├── chat_logic.py  聊天核心（消息整形/插入/未读）
│   │   ├── hub.py         聊天实时广播
│   │   ├── ws.py          /ws 实时端点
│   │   ├── lan.py         UDP 局域网发现
│   │   └── routers/       auth / steam / cloud / chat / sync / save / lan
│   ├── gateway_main.py    打包入口（--local 进离线模式）
│   ├── run_server.py      在线网关启动
│   ├── run_local.py       离线/局域网网关启动
│   ├── build_gateway.bat  打包为 steameya-gateway.exe
│   └── requirements.txt
├── engine/                云游戏引擎（Selenium）本地服务
│   ├── local_service.py   本地 HTTP 服务（127.0.0.1:6971）
│   ├── browser_driver.py  抓/注入 Cookie、检测、续期（原逻辑）
│   ├── cookie_crypto.py   本地加密
│   └── build_engine.bat   打包为 steameya-engine.exe
├── web/                   聊天 Web 前端（原聊天室 public/，WebView2 内嵌）
├── client/                WinUI3 客户端（源自上游 OpenSteamEYA，已扩展）
│   └── SteamEyaWinUI/
│       ├── LoginWindow.*        统一登录窗口（首屏）
│       ├── MainWindow.*         主界面导航（在线全功能 / 离线仅聊天）
│       ├── Pages/
│       │   ├── CloudGamePage.*  云游戏上号（一键上号/更新Cookie/检测）
│       │   ├── CloudSavePage.*  云存档（备份/还原/共享/拖拽）
│       │   ├── ChatPage.*       聊天室（WebView2）
│       │   └── ... (原有 Steam 上号各页)
│       ├── Services/            SessionState / CloudAuthService / CloudGameService
│       │                        / CloudSaveService / SyncService / GatewayLauncher
│       └── Models/              CloudApiModels / CloudSaveModels（AOT JSON 源生成）
└── docs/方案设计.md
```

---

## 快速开始

### 1) 在线后端（部署到服务器）
```bash
cd server
pip install -r requirements.txt
python run_server.py --port 8897      # 监听 0.0.0.0:8897
```
- 默认管理员：`admin / admin123`（请在后台修改）
- 健康检查：`GET /health`
- 聊天前端：`GET /`（浏览器打开即为聊天室）
- 管理后台：`GET /admin.html`（聊天管理，顶部可进入 `/admin-steam.html` Steam 账号管理、`/admin-cloud.html` 云游戏账号管理）

### 2) 云游戏引擎（Windows，需 Chrome/Edge）
```bash
cd engine
pip install -r requirements.txt selenium
python local_service.py --port 6971
```
- 数据（浏览器 profile / Cookie）默认落在脚本同级目录，可用 `EYA_ENGINE_DATA` 指到非 C 盘。

### 3) 客户端（Windows + .NET 10 SDK + VS C++ 工具链）
```bash
cd client
dotnet build SteamEyaWinUI/SteamEyaWinUI.csproj -c Release
```
启动流程：**先弹统一登录窗口 → 登录成功后进入主界面**；主界面导航含「Steam 上号 / 云游戏上号 / 聊天室 / 历史账号 / 已缓存账号 / 云存档 / 设置 / 关于」。

Steam 上号页的「远程账号」模式：登录统一账号后，一键从服务器领取账号（`/api/steam/account/get`）并直接登录本机 Steam（一键上号）。原「自动模式（卡密解析）」及「配装 / 个性化」页已移除。

---

## 关键行为

- **统一账号**：一套用户名/密码用于 Steam 上号、云游戏、聊天、云存档；旧账号哈希（sha256）首次登录自动升级为 scrypt。
- **在线聊天**：客户端用 WebView2 打开 `{网关}/api/autologin?jwt=<JWT>`，免二次登录复用现有聊天前端。
- **离线（局域网）模式**：无网络时启动本机网关（`gateway_main.py --local`），仅保留「聊天室」导航；首次需输入昵称，提示语强调「务必使用和在线聊天一模一样的用户名」。
- **离线→在线同步**：本地网关把离线消息标记为 `install_id + client_msg_id + synced=0`；联网后客户端调用
  `GET {本地}/api/sync/pending` → `POST {在线}/api/sync/push`（服务端按 `(install_id, client_msg_id)` 幂等去重）→
  `POST {本地}/api/sync/mark_synced`。**按消息真实 `created_at` 落库排序**；已同步的机器不再重复上传。
- **局域网发现**：UDP 广播自动发现同网段实例（默认端口 8899），并支持手动填 IP 兜底（`/api/lan/peers/add`）。
- **云存档**：植物大战僵尸支持自动备份（压缩 `yourdata` 上传）；我的世界「自动备份」置灰，仅支持拖入单个 `.zip`。
  共享存档显示上传者与时间，备注跟随上传者自己列表的备注。

---

## 接口一览（前缀）

| 模块 | 前缀 | 说明 |
|---|---|---|
| 统一账号 | `/api/auth` | register / login / me / **local**（离线昵称登录） |
| Steam 分发 | `/api/steam` | account/get + admin 账号/用户/日志/统计/设置 |
| 云游戏 | `/api/cloud` | upload / download / check / my / report_login + admin 账号/统计/检测/日志 |
| 管理后台 | `/api/admin` | 用户列表/封禁/新建/改密/改昵称、私聊记录、邀请码、推送配置、面板预设 |
| 聊天 | `/api/*` + `/ws` | 与原聊天室完全一致（前端零改动） |
| 离线同步 | `/api/sync` | push / pull / **pending** / **mark_synced** |
| 云存档 | `/api/save` | upload / list / shared / download / remark / share / delete |
| 局域网 | `/api/lan` | info / peers / peers/add |

---

## 打包发布（可选）

详见 [docs/部署打包说明.md](docs/部署打包说明.md)（客户端 / 服务端划分与逐步打包步骤）。速览：

1. `server\build_gateway.bat` → `steameya-gateway.exe`
2. `engine\build_engine.bat` → `steameya-engine.exe`
3. 把 `steameya-gateway.exe` 放到客户端输出目录的 `gateway\` 子目录，`steameya-engine.exe` 放到 `engine\`，并把根目录 `web\` 拷进去。
4. 用仓库自带 Inno Setup 脚本（`client/build/installer/SteamEYA.iss`）生成安装包。

---

## 当前状态与注意事项

- **后端 / 引擎 / 聊天前端**：已实现并**实测通过**（注册登录、JWT、云游戏 Cookie 上传/检测、聊天 HTTP+WS、离线同步幂等与按原始时间、云存档全流程、局域网发现与离线昵称登录）。
- **WinUI 客户端**：源码已完整扩展，但**本仓库的沙箱为 Linux、无 .NET/WinUI，未能编译验证**；请在 Windows 上构建，若报错按提示修正即可。
- 生产环境请：改默认管理员密码、配置 `EYA_SECRET_KEY`、启用 HTTPS、限制上传目录访问鉴权。