#!/bin/bash
# ============================================================================
# 神秘 WiFi 服务端 - 一键安装脚本
# ============================================================================
set -e

INSTALL_DIR="/opt/shenmi-wifi"
VENV_DIR="${INSTALL_DIR}/venv"
SERVICE_NAME="shenmi-wifi"
API_PORT=8999

echo "========================================"
echo "  神秘 WiFi 服务端安装"
echo "========================================"
echo ""

# 检查 root
if [ "$EUID" -ne 0 ]; then
    echo "请用 root 运行: sudo bash setup.sh"
    exit 1
fi

# 检查 WireGuard
if ! command -v wg &> /dev/null; then
    echo "[错误] 未找到 WireGuard，请先安装"
    exit 1
fi
echo "[OK] WireGuard 已安装"

# 检查 wg0 是否存在
if [ ! -f /etc/wireguard/wg0.conf ]; then
    echo "[错误] 未找到 /etc/wireguard/wg0.conf"
    exit 1
fi
echo "[OK] WireGuard 配置文件存在"

# 检查 wg0 是否运行
if ! wg show wg0 &> /dev/null; then
    echo "[警告] wg0 未运行，尝试启动..."
    systemctl start wg-quick@wg0 || echo "[错误] 无法启动 wg0"
fi
echo "[OK] WireGuard wg0 运行中"

# ---- 检查 Python 环境，决定是否需要 apt ----
NEED_APT=0
if ! command -v python3 &> /dev/null; then
    NEED_APT=1
elif ! command -v pip3 &> /dev/null && ! python3 -m pip --version &> /dev/null 2>&1; then
    NEED_APT=1
fi

if [ "$NEED_APT" -eq 0 ]; then
    echo "[OK] Python3 和 pip 已安装，跳过 apt 更新"
else
    echo ">>> 检查并修复 apt 源..."

# 方法1：删除有问题的 backports 源
if [ -f /etc/apt/sources.list ]; then
    # 备份原文件
    cp /etc/apt/sources.list /etc/apt/sources.list.bak.$(date +%s) 2>/dev/null || true
    # 注释掉包含 backports 的行
    sed -i '/backports/s/^/#/' /etc/apt/sources.list
    echo "[OK] 已处理 sources.list 中的 backports 源"
fi

# 方法2：处理 sources.list.d 目录中的的问题源
if [ -d /etc/apt/sources.list.d ]; then
    for f in /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources; do
        [ -f "$f" ] || continue
        if grep -q "backports" "$f" 2>/dev/null; then
            cp "$f" "${f}.bak.$(date +%s)" 2>/dev/null || true
            sed -i '/backports/s/^/#/' "$f"
            echo "[OK] 已处理 $(basename $f) 中的 backports 源"
        fi
    done
fi

# 方法3：检测系统版本，替换为可用的公网镜像
# 获取 Debian 版本代号（如 bullseye, bookworm）
CODENAME=""
if [ -f /etc/os-release ]; then
    CODENAME=$(grep '^VERSION_CODENAME=' /etc/os-release | head -1 | cut -d= -f2 | tr -d '"' | tr -d '\n')
fi
if [ -z "$CODENAME" ]; then
    # 兼容旧系统
    CODENAME=$(lsb_release -cs 2>/dev/null || echo "")
fi

if [ -n "$CODENAME" ]; then
    echo "[INFO] 检测到 Debian 版本代号: $CODENAME"

    # Debian 11 (bullseye) 已于 2024-08 EOL，需使用 archive.debian.org
    # Debian 10 (buster) 也已 EOL
    case "$CODENAME" in
        buster|bullseye)
            echo "[警告] 检测到 $CODENAME 已停止维护(EOL)，使用 archive.debian.org 归档源..."
            cat > /etc/apt/sources.list << APT_SOURCES
deb http://archive.debian.org/debian ${CODENAME} main contrib non-free
deb http://archive.debian.org/debian ${CODENAME}-updates main contrib non-free
deb http://archive.debian.org/debian-security ${CODENAME}-security main contrib non-free
APT_SOURCES
            # EOL 版本的 Release 文件可能过期，需禁用有效期检查
            echo 'Acquire::Check-Valid-Until "false";' > /etc/apt/apt.conf.d/99no-check-valid-until
            echo "[OK] apt 源已替换为 Debian 归档镜像 (archive.debian.org, $CODENAME)"
            ;;
        *)
            # 非 EOL 版本，使用官方源
            NEED_REPLACE=0

            # 检查是否使用了阿里云内网镜像
            if grep -q "mirrors.cloud.aliyuncs.com" /etc/apt/sources.list 2>/dev/null; then
                echo "[警告] 检测到阿里云内网镜像，替换为公网镜像..."
                NEED_REPLACE=1
            fi

            # 检查 sources.list.d 中的阿里云内网镜像
            if [ -d /etc/apt/sources.list.d ]; then
                for f in /etc/apt/sources.list.d/*.list; do
                    [ -f "$f" ] || continue
                    if grep -q "mirrors.cloud.aliyuncs.com" "$f" 2>/dev/null; then
                        NEED_REPLACE=1
                        cp "$f" "${f}.bak.$(date +%s)" 2>/dev/null || true
                        rm -f "$f"
                        echo "[OK] 已移除 $(basename $f) 中的阿里云内网源"
                    fi
                done
            fi

            if [ "$NEED_REPLACE" -eq 1 ]; then
                cat > /etc/apt/sources.list << APT_SOURCES
deb http://deb.debian.org/debian ${CODENAME} main contrib non-free
deb http://deb.debian.org/debian ${CODENAME}-updates main contrib non-free
deb http://deb.debian.org/debian-security ${CODENAME}-security main contrib non-free
APT_SOURCES
                echo "[OK] apt 源已替换为 Debian 官方镜像 ($CODENAME)"
            else
                echo "[OK] apt 源配置正常 ($CODENAME)，无需替换"
            fi
            ;;
    esac
else
    echo "[警告] 无法检测 Debian 版本代号，跳过源替换"
    echo "       如果 apt-get update 失败，请手动修复 /etc/apt/sources.list"
fi

# 更新 apt
echo ">>> 更新 apt 索引..."
apt-get update -qq 2>&1 || {
    echo "[警告] apt-get update 仍有警告，尝试继续..."
}

# 安装 Python 依赖
echo ">>> 安装 Python 3 和 pip..."
apt-get install -y -qq python3 python3-pip python3-venv 2>&1 || {
    echo "[错误] Python 安装失败，请手动运行: apt-get install python3 python3-pip python3-venv"
    exit 1
}
fi  # NEED_APT 结束

# 创建安装目录
echo ">>> 安装到 ${INSTALL_DIR}..."
mkdir -p "${INSTALL_DIR}"
mkdir -p /etc/shenmi-wifi

# 复制源码（不覆盖 start.sh 和 requirements.txt，保留用户定制）
cp -r app/ "${INSTALL_DIR}/"
# 只有 requirements.txt 不存在时才复制（保留宝塔环境的定制版本）
if [ ! -f "${INSTALL_DIR}/requirements.txt" ]; then
    cp requirements.txt "${INSTALL_DIR}/"
fi
# config.py 只在不存在时复制（保留已改好的配置）
if [ ! -f "${INSTALL_DIR}/config.py" ]; then
    cp config.py "${INSTALL_DIR}/"
fi

# 创建虚拟环境
echo ">>> 创建 Python 虚拟环境..."
python3 -m venv "${VENV_DIR}"

# 安装依赖
echo ">>> 安装 Python 依赖..."
"${VENV_DIR}/bin/pip" install --upgrade pip -q
"${VENV_DIR}/bin/pip" install -r "${INSTALL_DIR}/requirements.txt" -q

echo ">>> 配置 sudoers（允许 API 读取 wg 状态和管理 peer）..."
# 允许 shenmi-wifi 服务无需密码执行 wg 和 wg-quick save 命令
cat > /etc/sudoers.d/shenmi-wifi << 'SUDOERS'
shenmi-wifi ALL=(root) NOPASSWD: /usr/bin/wg show *, /usr/bin/wg set *, /usr/bin/wg pubkey, /usr/bin/wg-quick save *
SUDOERS
chmod 440 /etc/sudoers.d/shenmi-wifi

# 复制限速脚本
echo ">>> 配置带宽限速..."
cp rate-limit.sh "${INSTALL_DIR}/rate-limit.sh"
chmod +x "${INSTALL_DIR}/rate-limit.sh"

# 创建限速 systemd 服务（开机自启）
cat > /etc/systemd/system/shenmi-ratelimit.service << 'RATLIMIT_SVC'
[Unit]
Description=Shenmi WiFi Rate Limiter (tc on wg0)
After=wg-quick@wg0.service
Wants=wg-quick@wg0.service

[Service]
Type=oneshot
ExecStart=/opt/shenmi-wifi/rate-limit.sh start
ExecStop=/opt/shenmi-wifi/rate-limit.sh stop
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
RATLIMIT_SVC

systemctl daemon-reload
systemctl enable shenmi-ratelimit
echo "[OK] 限速服务已配置（开机自启，默认 100mbit）"

echo ">>> 创建 systemd 服务..."
cat > /etc/systemd/system/${SERVICE_NAME}.service << EOF
[Unit]
Description=Shenmi WiFi API Server
After=network.target wg-quick@wg0.service
Wants=wg-quick@wg0.service

[Service]
Type=simple
WorkingDirectory=${INSTALL_DIR}
ExecStart=${VENV_DIR}/bin/python -m uvicorn app.main:app --host 0.0.0.0 --port ${API_PORT}
Restart=always
RestartSec=5
Environment=PYTHONPATH=${INSTALL_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable ${SERVICE_NAME}

echo ""
echo ">>> 初始化数据库..."
cd "${INSTALL_DIR}"
"${VENV_DIR}/bin/python" -c "
import sys; sys.path.insert(0, '${INSTALL_DIR}')
from app.database import init_db
init_db()
from app.database import Database
from config import ADMIN_USERNAME, ADMIN_PASSWORD
db = Database()
admin = db.get_user_by_username(ADMIN_USERNAME)
if not admin:
    db.create_user(ADMIN_USERNAME, ADMIN_PASSWORD, is_admin=True)
    print(f'[OK] 管理员账户已创建: {ADMIN_USERNAME}')
else:
    print('[OK] 管理员账户已存在')
"

echo ""
echo ">>> 启动服务..."
systemctl start ${SERVICE_NAME}
systemctl start shenmi-ratelimit
sleep 2
systemctl status ${SERVICE_NAME} --no-pager || true

echo ""
echo "========================================"
echo "  安装完成！"
echo "========================================"
echo ""
SERVER_IP=$(hostname -I | awk '{print $1}')
echo "管理后台: http://${SERVER_IP}:${API_PORT}/admin"
echo "API 地址: http://${SERVER_IP}:${API_PORT}"
echo ""
echo "默认管理员账号见 config.py 里的 ADMIN_USERNAME / ADMIN_PASSWORD"
echo ""
echo "【重要】请修改以下配置："
echo "  1. 编辑 ${INSTALL_DIR}/config.py 修改 ADMIN_PASSWORD"
echo "  2. 重启服务: systemctl restart ${SERVICE_NAME}"
echo ""
echo "常用命令："
echo "  启动: systemctl start ${SERVICE_NAME}"
echo "  停止: systemctl stop ${SERVICE_NAME}"
echo "  重启: systemctl restart ${SERVICE_NAME}"
echo "  日志: journalctl -u ${SERVICE_NAME} -f"
echo ""
echo "带宽限速："
echo "  查看状态: bash ${INSTALL_DIR}/rate-limit.sh status"
echo "  动态修改: bash ${INSTALL_DIR}/rate-limit.sh set 80mbit"
echo "  关闭限速: bash ${INSTALL_DIR}/rate-limit.sh stop"
echo "  限速服务: systemctl restart shenmi-ratelimit"
echo ""
echo "防火墙（如需开放 ${API_PORT} 端口）："
echo "  ufw allow ${API_PORT}/tcp"
echo ""
