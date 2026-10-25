# 神秘 WiFi 服务端

基于 WireGuard + FastAPI 的 VPN 管理系统，支持用户注册登录、流量统计、到期限制。

## 功能

- 用户注册 / 登录（JWT 认证）
- 自动生成 WireGuard 客户端配置
- 实时流量统计（每 30 秒采集 `wg show`）
- 用户到期自动断开
- 流量超限自动断开
- 管理员后台网页面板（用户管理、流量查看、续期、设置上限）

## 系统要求

- Linux 服务器（Ubuntu 20.04+）
- root 权限
- 已安装并运行 WireGuard（`wg0` 接口）
- Python 3.8+
- 端口 8080 可用（API + 管理后台）

## 快速安装

把源码包上传到服务器后：

```bash
tar xzf shenmi-wifi-server.tar.gz
cd shenmi-wifi-server
chmod +x setup.sh
sudo ./setup.sh
```

安装脚本会自动完成：
1. 安装 Python 3 和 pip
2. 创建虚拟环境
3. 安装 Python 依赖
4. 复制文件到 `/opt/shenmi-wifi/`
5. 配置 sudoers（允许 API 读取 wg 状态）
6. 创建 systemd 服务
7. 初始化数据库和管理员账户
8. 启动服务

## 安装后配置

### 1. 修改默认密码

```bash
nano /opt/shenmi-wifi/config.py
```

修改以下字段：
- `JWT_SECRET`：改成随机字符串
- `ADMIN_PASSWORD`：改成你的管理员密码

然后重启：
```bash
systemctl restart shenmi-wifi
```

### 2. 开放防火墙端口

```bash
# UFW
ufw allow 8080/tcp

# 或 iptables
iptables -A INPUT -p tcp --dport 8080 -j ACCEPT
```

### 3. 阿里云安全组

在阿里云控制台 → 安全组规则 → 添加入方向规则：
- 端口：8080
- 协议：TCP
- 授权对象：0.0.0.0/0（或限制为你的 IP）

## 访问方式

| 地址 | 用途 |
|------|------|
| `http://服务器IP:8080/admin` | 管理员后台 |
| `http://服务器IP:8080/api/login` | APP 登录接口 |
| `http://服务器IP:8080/api/register` | APP 注册接口 |
| `http://服务器IP:8080/api/vpn-config` | 获取 VPN 配置 |

## API 接口

### 用户注册
```
POST /api/register
Body: {"username": "xxx", "password": "xxx"}
返回: {"token": "...", "user": {...}}
```

### 用户登录
```
POST /api/login
Body: {"username": "xxx", "password": "xxx"}
返回: {"token": "...", "user": {...}}
```

### 获取用户信息
```
GET /api/profile
Header: Authorization: Bearer <token>
返回: {username, expire_at, traffic_used, ...}
```

### 获取 VPN 配置
```
GET /api/vpn-config
Header: Authorization: Bearer <token>
返回: {"config": "[Interface]\nPrivateKey=...", "client_ip": "10.7.0.2"}
```

## 管理员后台功能

| 功能 | 说明 |
|------|------|
| 用户列表 | 查看所有用户、在线状态、流量消耗 |
| 新建用户 | 设置用户名、密码、有效期、流量上限 |
| 续期 | 延长用户使用天数 |
| 设置流量 | 设置流量上限（GB） |
| 禁用/启用 | 临时禁用用户（自动断开 VPN） |
| 删除用户 | 永久删除用户（回收 IP 和配置） |

## 常用运维命令

```bash
# 服务管理
systemctl start shenmi-wifi      # 启动
systemctl stop shenmi-wifi       # 停止
systemctl restart shenmi-wifi    # 重启
systemctl status shenmi-wifi     # 状态

# 查看日志
journalctl -u shenmi-wifi -f    # 实时日志

# 查看流量
sudo wg show                    # WireGuard 状态
sudo wg show wg0 transfer        # 各 peer 流量

# 修改配置后重启
nano /opt/shenmi-wifi/config.py
systemctl restart shenmi-wifi
```

## 数据存储

| 数据 | 位置 |
|------|------|
| 用户数据 | `/etc/shenmi-wifi/shenmi.db`（SQLite） |
| WireGuard 配置 | `/etc/wireguard/wg0.conf` |
| 应用配置 | `/opt/shenmi-wifi/config.py` |
| 源码 | `/opt/shenmi-wifi/app/` |

## 目录结构

```
shenmi-wifi-server/
├── app/
│   ├── main.py              # FastAPI 主应用（API + 路由）
│   ├── database.py          # SQLite 数据库操作
│   ├── auth.py              # JWT 认证
│   ├── wireguard.py         # WireGuard 管理（密钥/配置/流量）
│   ├── traffic_monitor.py   # 流量采集守护进程
│   └── templates/
│       ├── admin_login.html     # 管理员登录页
│       └── admin_dashboard.html # 管理后台面板
├── config.py                # 配置文件
├── requirements.txt         # Python 依赖
├── setup.sh                 # 一键安装脚本
├── start.sh                 # 手动启动脚本
└── README.md                # 本文档
```

## 安全建议

1. **修改默认密码**：安装后立即修改 `ADMIN_PASSWORD` 和 `JWT_SECRET`
2. **HTTPS**：生产环境建议用 Nginx 反向代理加 SSL
3. **限制端口**：8080 只对你的 IP 开放
4. **定期备份**：备份 `/etc/shenmi-wifi/shenmi.db`
5. **监控日志**：定期检查 `journalctl -u shenmi-wifi`
