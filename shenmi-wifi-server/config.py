# ============================================================================
# 神秘 WiFi - 服务端配置
# ============================================================================

# ---- 服务器网络 ----
SERVER_IP = "47.104.210.199"
SERVER_PORT = 53

# ---- WireGuard 配置 ----
WG_INTERFACE = "wg0"
WG_CONFIG_PATH = "/etc/wireguard/wg0.conf"
WG_CLIENT_IP_PREFIX = "10.7.0."
WG_CLIENT_IP_START = 2  # 从 10.7.0.2 开始分配

# 服务器私钥（从 wg0.conf 读取，这里作为备用）
WG_SERVER_PRIVATE_KEY = "<填入你的 wg0 私钥>"
WG_PRESHARED_KEY = "<填入你的 PresharedKey>"

# ---- DNS ----
WG_DNS = "223.5.5.5, 119.29.29.29"

# ---- API 服务器 ----
API_HOST = "0.0.0.0"
API_PORT = 8999

# ---- 数据库 ----
DATABASE_PATH = "/etc/shenmi-wifi/shenmi.db"

# ---- JWT ----
JWT_SECRET = "<改成你自己的随机串>"  # 随机生成，请勿修改
JWT_ALGORITHM = "HS256"
JWT_EXPIRE_HOURS = 24 * 30  # Token 有效期 30 天（App 端「小工具」登录态）

# ---- 管理员账户 ----
ADMIN_USERNAME = "admin"
ADMIN_PASSWORD = "<改成你自己的密码>"  # 部署后务必修改！

# ---- 流量采集 ----
TRAFFIC_CHECK_INTERVAL = 30  # 秒，每 30 秒采集一次 wg show

# ---- 默认用户限制 ----
DEFAULT_EXPIRE_DAYS = 30      # 新用户默认 30 天到期
DEFAULT_TRAFFIC_LIMIT_GB = 10  # 新用户默认 10GB 流量上限
