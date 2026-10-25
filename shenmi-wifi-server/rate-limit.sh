#!/bin/bash
# ============================================================================
# 神秘 WiFi - WireGuard 带宽限速脚本 (v3 - 修复 IFB 生命周期 + 错误上报)
#
# 原理: 在物理网卡上用 tc + HTB 限速，
#       用 u32 过滤器只限制 WireGuard 端口的 UDP 流量，
#       出向(egress)限制下载，入向(ingress)通过 IFB 限制上传。
# ============================================================================

WG_INTERFACE="wg0"
WG_PORT="53"
IFB_DEV="ifb0"
RATE_FILE="/tmp/shenmi-ratelimit.state"

# ---- 自动检测物理网卡 ----
get_phys_iface() {
    local iface
    iface=$(ip route show default 2>/dev/null | awk '/default/ {print $5; exit}')
    if [ -z "$iface" ]; then
        iface=$(ip -o link show up 2>/dev/null | awk -F': ' '{print $2}' | grep -vE '^(lo|wg0|ifb0|docker)' | head -1)
    fi
    echo "$iface"
}

PHYS_IFACE=$(get_phys_iface)

# ---- 判断是否需要 sudo ----
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    SUDO="sudo"
fi

# ---- 检查 tc ----
if ! command -v tc &> /dev/null; then
    echo "ENABLED=no"
    echo "RATE=0"
    echo "ERROR=tc_not_installed"
    exit 1
fi

# ---- 检查物理网卡 ----
if [ -z "$PHYS_IFACE" ]; then
    echo "ENABLED=no"
    echo "RATE=0"
    echo "ERROR=no_physical_interface"
    exit 1
fi

# ---- 清理函数（在加载 IFB 之前调用） ----
do_cleanup() {
    $SUDO tc qdisc del dev "$PHYS_IFACE" root 2>/dev/null
    $SUDO tc qdisc del dev "$PHYS_IFACE" ingress 2>/dev/null
    $SUDO tc qdisc del dev "$IFB_DEV" root 2>/dev/null
    $SUDO ip link set "$IFB_DEV" down 2>/dev/null
    $SUDO modprobe -r ifb 2>/dev/null
}

# ---- 设置限速 (核心函数) ----
apply_rate() {
    local LIMIT="$1"
    local BURST="2mbit"
    local ERRORS=""

    echo "[限速] 物理网卡=$PHYS_IFACE  WireGuard端口=$WG_PORT  限速=$LIMIT"

    # 1. 先清理旧规则（在加载 IFB 之前！）
    do_cleanup

    # 2. 加载 IFB 模块（清理之后重新加载）
    $SUDO modprobe ifb numifbs=1 2>/dev/null
    if ! $SUDO ip link set "$IFB_DEV" up 2>/dev/null; then
        ERRORS="$ERRORS ifb_setup_failed"
    fi

    # 3. 出向限速 (服务器→客户端 = 下载)
    if ! $SUDO tc qdisc add dev "$PHYS_IFACE" root handle 1: htb default 99 2>/dev/null; then
        ERRORS="$ERRORS egress_qdisc_failed"
    fi

    # 限速类
    $SUDO tc class add dev "$PHYS_IFACE" parent 1: classid 1:10 htb rate "$LIMIT" burst "$BURST" ceil "$LIMIT" 2>/dev/null || ERRORS="$ERRORS egress_class_failed"
    # 不限速类
    $SUDO tc class add dev "$PHYS_IFACE" parent 1: classid 1:99 htb rate 1000mbit burst 10mbit ceil 1000mbit 2>/dev/null || true
    # 过滤器: UDP 目标端口=WG_PORT
    $SUDO tc filter add dev "$PHYS_IFACE" parent 1: protocol ip prio 1 u32 \
        match ip protocol 17 0xff \
        match ip dport "$WG_PORT" 0xffff \
        flowid 1:10 2>/dev/null || ERRORS="$ERRORS egress_filter_failed"

    # 4. 入向限速 (客户端→服务器 = 上传) 通过 IFB
    if ! $SUDO tc qdisc add dev "$PHYS_IFACE" ingress 2>/dev/null; then
        ERRORS="$ERRORS ingress_qdisc_failed"
    fi

    $SUDO tc filter add dev "$PHYS_IFACE" parent ffff: protocol ip prio 1 u32 \
        match ip protocol 17 0xff \
        match ip sport "$WG_PORT" 0xffff \
        action mirred egress redirect dev "$IFB_DEV" 2>/dev/null || ERRORS="$ERRORS ingress_filter_failed"

    # IFB 上的限速
    $SUDO tc qdisc add dev "$IFB_DEV" root handle 1: htb default 99 2>/dev/null || ERRORS="$ERRORS ifb_qdisc_failed"
    $SUDO tc class add dev "$IFB_DEV" parent 1: classid 1:10 htb rate "$LIMIT" burst "$BURST" ceil "$LIMIT" 2>/dev/null || ERRORS="$ERRORS ifb_class_failed"
    $SUDO tc class add dev "$IFB_DEV" parent 1: classid 1:99 htb rate 1000mbit burst 10mbit ceil 1000mbit 2>/dev/null || true

    # 记录状态
    LIMIT_NUM=$(echo "$LIMIT" | grep -oP '[0-9]+' | head -1)
    echo "$LIMIT_NUM" > "$RATE_FILE"

    # 输出结果
    if [ -n "$ERRORS" ]; then
        echo "ENABLED=no"
        echo "RATE=0"
        echo "ERROR=$ERRORS"
        echo "DETAIL=限速设置部分失败:$ERRORS"
    else
        echo "ENABLED=yes"
        echo "RATE=$LIMIT_NUM"
        echo "INTERFACE=$PHYS_IFACE"
        echo "WG_PORT=$WG_PORT"
        echo "DIRECTION=both"
    fi
}

case "$1" in
    start)
        LIMIT="${2:-100mbit}"
        apply_rate "$LIMIT"
        ;;

    stop)
        do_cleanup
        echo "" > "$RATE_FILE"
        echo "ENABLED=no"
        echo "RATE=0"
        echo "[限速] 已停止"
        ;;

    status)
        EGRESS_OUT=$($SUDO tc qdisc show dev "$PHYS_IFACE" 2>&1)
        CLASS_OUT=$($SUDO tc class show dev "$PHYS_IFACE" 2>&1)

        if echo "$EGRESS_OUT" | grep -q "htb"; then
            RATE_NUM=$(echo "$CLASS_OUT" | grep -oP 'rate \K[0-9]+(?=Mbit)' | head -1)
            if [ -z "$RATE_NUM" ]; then
                RATE_NUM=$(cat "$RATE_FILE" 2>/dev/null | grep -oP '[0-9]+' | head -1)
            fi
            if [ -z "$RATE_NUM" ]; then
                RATE_NUM=0
            fi
            echo "ENABLED=yes"
            echo "RATE=${RATE_NUM}"
            echo "INTERFACE=$PHYS_IFACE"
        else
            echo "ENABLED=no"
            echo "RATE=0"
            echo "INTERFACE=$PHYS_IFACE"
            if echo "$EGRESS_OUT" | grep -qi "error\|cannot\|not found\|No such"; then
                echo "ERROR=tc_query_failed"
            fi
        fi
        ;;

    set)
        if [ -z "$2" ]; then
            echo "ENABLED=no"
            echo "RATE=0"
            echo "ERROR=missing_rate_argument"
            exit 1
        fi
        NEW_LIMIT="$2"
        apply_rate "$NEW_LIMIT"
        ;;

    test)
        echo "==== 限速诊断 ===="
        echo "当前用户: $(whoami) (UID=$(id -u))"
        echo "tc 路径: $(which tc 2>/dev/null || echo '未安装')"
        echo "sudo: ${SUDO:-不需要(已root)}"
        echo "物理网卡: $PHYS_IFACE"
        echo "WireGuard 端口: $WG_PORT"
        echo ""
        echo "---- 物理网卡 qdisc ----"
        $SUDO tc qdisc show dev "$PHYS_IFACE" 2>&1
        echo ""
        echo "---- 物理网卡 class ----"
        $SUDO tc class show dev "$PHYS_IFACE" 2>&1
        echo ""
        echo "---- ifb0 状态 ----"
        $SUDO ip link show "$IFB_DEV" 2>&1 | head -2
        echo ""
        echo "---- 状态文件 ----"
        cat "$RATE_FILE" 2>/dev/null || echo "(无)"
        echo ""
        echo "---- sudo 测试 ----"
        $SUDO echo "sudo OK" 2>&1
        ;;

    *)
        echo "神秘 WiFi - WireGuard 限速管理 (v3)"
        echo ""
        echo "用法: $0 {start|stop|status|set|test}"
        echo "  start [带宽]  启动限速 (默认 100mbit)"
        echo "  stop          停止限速"
        echo "  status        查看状态"
        echo "  set <值>      修改限速 (如: set 80mbit)"
        echo "  test          诊断模式"
        echo ""
        echo "物理网卡: $PHYS_IFACE"
        ;;
esac
