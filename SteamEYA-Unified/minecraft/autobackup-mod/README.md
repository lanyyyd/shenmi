# Auto Backup

Minecraft Java 版 Fabric 存档备份模组，支持自动/手动备份、备份轮转，并可上传到自建 HTTP 服务器。

- 模组 ID：`autobackup`
- 版本：1.0.0
- 适用环境：Minecraft 1.21.1 + Fabric（单机/专用服务器均可）

## 功能

- 手动备份：`/autobackup now`
- 定时自动备份（默认每 30 分钟）
- 备份轮转：只保留最近 N 份，自动删除旧文件
- 备份时暂停自动保存并执行 flush，保证 zip 内容一致
- 压缩在后台线程执行，尽量不阻塞服务器主线程
- 可选：服务器启动后备份、服务器关闭时同步备份
- 可选：备份完成后上传到自建服务器（Bearer Token 鉴权，失败重试）
- 上传配置支持热重载

## 环境要求

| 项目 | 要求 |
| --- | --- |
| Minecraft | 1.21.1 |
| Fabric Loader | >= 0.16.0 |
| Fabric API | 1.21.1 对应版本 |
| Java | 21 |

## 安装

1. 服务端或客户端安装 Fabric Loader 与 Fabric API。
2. 将 `autobackup-1.0.0.jar` 放入 `mods/` 目录。
3. 启动游戏/服务器。
4. 首次启动会生成配置文件 `config/autobackup.json`，备份文件存放在游戏/服务器根目录下的 `autobackups/`。

从源码构建出的 jar 位于 `build/libs/autobackup-1.0.0.jar`。

## 命令

需要权限等级 2（服务器 OP / 单机开启作弊）。

| 命令 | 说明 |
| --- | --- |
| `/autobackup now` | 立即执行一次手动备份 |
| `/autobackup list` | 列出本地备份（最多显示 15 条） |
| `/autobackup status` | 查看自动备份状态、下次执行时间、上传配置和最近结果 |
| `/autobackup reload` | 重新加载 `config/autobackup.json` |

不带子命令的 `/autobackup` 等价于 `/autobackup status`。

## 配置

配置文件路径：`config/autobackup.json`。修改后执行 `/autobackup reload`，或重启游戏/服务器。

```json
{
  "enabled": true,
  "intervalMinutes": 30,
  "keepBackups": 12,
  "backupOnStart": false,
  "backupOnStop": false,
  "compressLevel": 6,
  "upload": {
    "enabled": true,
    "url": "https://your-server.com/upload",
    "token": "your-token",
    "fieldName": "file",
    "timeoutSeconds": 300
  }
}
```

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 自动备份总开关；关闭后定时/启动/关服备份都不执行，手动备份仍可用 |
| `intervalMinutes` | `30` | 定时备份间隔（分钟），范围 0-1440；`0` 表示不定时备份 |
| `keepBackups` | `12` | 本地保留份数，范围 1-1000，超出后删除最旧的备份 |
| `backupOnStart` | `false` | 服务器启动 5 秒后是否自动备份一次 |
| `backupOnStop` | `false` | 服务器关闭时是否同步备份一次（大存档会延长关服时间） |
| `compressLevel` | `6` | zip 压缩级别 0-9 |
| `upload.enabled` | `false` | 是否在上传备份完成后上传 |
| `upload.url` | `""` | 接收备份文件的 HTTP 地址 |
| `upload.token` | `""` | 可选，放入 `Authorization: Bearer <token>` |
| `upload.fieldName` | `"file"` | multipart/form-data 中的文件字段名 |
| `upload.timeoutSeconds` | `300` | 单次上传超时时间（秒），范围 10-3600 |

## 上传接口约定

模组会以 `POST` 方式向 `upload.url` 发送 `multipart/form-data` 请求：

- 文件字段名：`upload.fieldName`（默认 `file`）
- 文件名：备份 zip 的文件名，例如 `world-20261120-162444.zip`
- 请求头：
  - `Content-Type: multipart/form-data; boundary=...`
  - `X-Backup-File: <文件名>`
  - `Authorization: Bearer <token>`（仅当配置了 token）
  - `User-Agent: AutoBackup/1.0`
- 成功判定：HTTP 状态码 2xx
- 重试策略：网络错误或 5xx 最多重试 3 次；4xx 不重试

服务端只需解析 multipart 并保存文件字段即可。示例（Python 风格伪代码）：

```python
# POST /upload
# 读取 multipart 中的 file 字段，保存到磁盘，返回 200 即可
```

上传失败不会删除本地备份，本地备份仍会按 `keepBackups` 轮转。

## 备份与恢复

- 备份目录：游戏/服务器根目录下的 `autobackups/`
- 文件命名：`<存档名>-yyyyMMdd-HHmmss.zip`，例如 `world-20261120-162444.zip`
- zip 内部结构：以存档目录名为根，例如 `world/level.dat`、`world/region/...`
- 已排除 `session.lock`

恢复步骤：

1. 停止服务器（或退出单机存档）。
2. 备份当前存档以防万一。
3. 解压目标 zip，得到 `world/` 目录。
4. 用解压出的 `world/` 覆盖服务器根目录或 `saves/` 下的同名存档目录。
5. 重新启动服务器/游戏。

## 从源码构建

需要 JDK 21：

```bash
./gradlew build
```

产物：`build/libs/autobackup-1.0.0.jar`

主要依赖版本见 `gradle.properties`：

- Minecraft 1.21.1
- Yarn 1.21.1+build.3
- Fabric Loader 0.16.14
- Fabric API 0.116.18+1.21.1
- Fabric Loom 1.11.8

## 项目结构

```
autobackup/
├── build.gradle
├── gradle.properties
├── settings.gradle
├── src/main/java/com/autobackup/
│   ├── AutoBackupMod.java      # 模组入口，注册事件与命令
│   ├── BackupConfig.java       # 配置加载/保存/校验
│   ├── BackupManager.java      # 备份调度、世界保存、zip、轮转
│   ├── BackupUploader.java     # multipart HTTP 上传
│   └── BackupCommand.java      # /autobackup 命令
└── src/main/resources/fabric.mod.json
```

## 已测试

在真实 Fabric 1.21.1 开发服务器上验证通过：

- 手动备份与命令反馈
- 定时备份触发
- `keepBackups=1` 时的旧备份删除
- 关服备份
- multipart 上传、Bearer Token 与文件名请求头

## 已知限制

- 当前仅声明支持 Minecraft 1.21.1。
- 备份期间会关闭自动保存，超大存档的打包时间会拉长该窗口。
- 上传功能只负责发送，接收端需要自行部署。
