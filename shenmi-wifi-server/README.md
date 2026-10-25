# 神秘WiFi 服务端（脱敏版）

WireGuard + FastAPI 的 VPN 管理系统。安卓客户端「我的 → 小工具」里的
「神秘WiFi」功能对接的就是这套服务端。

> ⚠️ **这是脱敏版**：`config.py` 里的私钥、JWT 密钥、管理员密码都已替换成占位符。
> 线上真实配置请从服务器 `/opt/shenmi-wifi/config.py` 取，不要提交进仓库。
> 仓库 `lanyyyd/shenmi` 是公开的。

## 部署

```bash
sudo ./setup.sh          # 自动装依赖、建表、配 systemd、建 wg0 peer 权限
sudo systemctl restart shenmi-wifi
```

首次部署后**必须**改 `config.py` 里的四项：

| 配置项 | 说明 |
|---|---|
| `WG_SERVER_PRIVATE_KEY` | 服务器 wg0 接口私钥（`wg show wg0 private-key`） |
| `WG_PRESHARED_KEY` | Preshared Key |
| `JWT_SECRET` | 随机串，签发登录 token 用 |
| `ADMIN_PASSWORD` | 后台管理员密码 |

## 关键接口

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/api/login` | 登录，返回 `{token, user:{id, username, expire_at, traffic_limit_bytes, is_admin}}` |
| GET | `/api/profile` | 用户信息 + 流量统计 |
| GET | `/api/vpn-config` | 取 WireGuard 配置（首次调用自动生成密钥并注册 peer） |
| GET | `/admin` | 管理后台（用管理员账号登录） |

鉴权用 `Authorization: Bearer <token>`。

## 配置说明

- `JWT_EXPIRE_HOURS = 24 * 30` —— 登录 token 有效期 30 天。
  客户端「小工具」是移动端场景，7 天太短会导致频繁重新登录。
- `SERVER_PORT = 53` —— WireGuard 走 UDP 53（便于穿墙）。
- `WG_CLIENT_IP_PREFIX = "10.7.0."` —— 客户端地址池，账号按顺序分配。
- `DEFAULT_EXPIRE_DAYS = 30` / `DEFAULT_TRAFFIC_LIMIT_GB = 10` —— 新建账号的默认到期与流量上限。

## 与安卓客户端的对应关系

| 客户端 | 服务端 |
|---|---|
| `WgApi.login` | `POST /api/login` |
| `WgApi.profile` | `GET /api/profile` |
| `WgApi.vpnConfig` | `GET /api/vpn-config` |
| `WgManager.connect` | 本地 WireGuard 库直接建隧道，不经服务端 |

客户端侧代码在 `SteamEYA-Unified/client/SteamEyaAndroid/app/src/main/kotlin/com/teatt/pocket/feature/wireguard/`。
