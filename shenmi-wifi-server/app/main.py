"""
神秘 WiFi - FastAPI 主应用
提供：
  - 用户注册/登录 API
  - 获取 VPN 配置 API
  - 上报流量 API
  - 管理员后台网页面板
"""
import os
import sys
import time
import subprocess
import re

# 确保能 import app 模块
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from fastapi import FastAPI, Request, HTTPException, Depends, Form, UploadFile, File
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
try:
    from fastapi.responses import FileResponse
except ImportError:
    FileResponse = None
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from pydantic import BaseModel
from config import API_HOST, API_PORT, ADMIN_USERNAME, ADMIN_PASSWORD, DEFAULT_EXPIRE_DAYS, DEFAULT_TRAFFIC_LIMIT_GB
from app.database import Database, init_db, verify_password
from app.auth import create_access_token, decode_token, authenticate_user, register_user, get_current_user_from_token
from app.wireguard import gen_keypair, generate_client_config, allocate_ip, add_peer_to_server, remove_peer_from_server, release_ip, get_traffic_stats, get_peer_handshakes
from app.traffic_monitor import start_monitor, stop_monitor
try:
    from app.traffic_monitor import get_realtime_speed
except ImportError:
    # 兼容旧版 traffic_monitor.py（没有实时网速功能）
    def get_realtime_speed():
        return {"download_bps": 0, "upload_bps": 0, "total_bps": 0, "online_peers": 0, "timestamp": 0}

# ==================== 初始化 ====================

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
TEMPLATES_DIR = os.path.join(BASE_DIR, "templates")

app = FastAPI(title="神秘 WiFi API", version="1.0")
templates = Jinja2Templates(directory=TEMPLATES_DIR)

# Jinja2 自定义过滤器
import datetime
def timestamp_to_date(ts):
    try:
        return datetime.datetime.fromtimestamp(float(ts)).strftime("%Y-%m-%d %H:%M")
    except Exception:
        return "-"
templates.env.filters["timestamp_to_date"] = timestamp_to_date

def fmt_bytes(b):
    """将字节数格式化为带单位的字符串"""
    try:
        b = int(b)
    except (ValueError, TypeError):
        b = 0
    if b < 0:
        b = 0
    if b < 1024:
        return f"{b} B"
    elif b < 1024 * 1024:
        return f"{b / 1024:.1f} KB"
    elif b < 1024 * 1024 * 1024:
        return f"{b / (1024 * 1024):.1f} MB"
    else:
        return f"{b / (1024 * 1024 * 1024):.2f} GB"
templates.env.filters["fmt_bytes"] = fmt_bytes

db = Database()


@app.on_event("startup")
def on_startup():
    init_db()
    # 确保管理员存在
    admin = db.get_user_by_username(ADMIN_USERNAME)
    if not admin:
        db.create_user(ADMIN_USERNAME, ADMIN_PASSWORD, is_admin=True)
    start_monitor()
    print(f"[API] 神秘 WiFi 服务端已启动: http://{API_HOST}:{API_PORT}")


@app.on_event("shutdown")
def on_shutdown():
    stop_monitor()


# ==================== 请求模型 ====================

class RegisterRequest(BaseModel):
    username: str
    password: str


class LoginRequest(BaseModel):
    username: str
    password: str


class TrafficReport(BaseModel):
    rx_bytes: int
    tx_bytes: int


# ==================== 认证依赖 ====================

def get_token_from_header(request: Request) -> str:
    auth = request.headers.get("Authorization")
    if not auth or not auth.startswith("Bearer "):
        return None
    return auth.split(" ")[1]


def get_current_user(request: Request) -> dict:
    token = get_token_from_header(request)
    if not token:
        raise HTTPException(status_code=401, detail="未登录")
    user = get_current_user_from_token(token)
    if not user:
        raise HTTPException(status_code=401, detail="Token 无效或已过期")
    return user


def require_admin(user: dict = Depends(get_current_user)) -> dict:
    if not user.get("is_admin"):
        raise HTTPException(status_code=403, detail="需要管理员权限")
    return user


# ==================== 用户 API ====================

@app.post("/api/register")
async def api_register(req: RegisterRequest):
    """用户注册"""
    result = register_user(req.username, req.password)
    if "error" in result:
        raise HTTPException(status_code=400, detail=result["error"])

    user = result["user"]
    token = create_access_token({"user_id": user["id"], "username": user["username"]})
    return {
        "token": token,
        "user": {
            "id": user["id"],
            "username": user["username"],
            "expire_at": user["expire_at"],
            "traffic_limit_bytes": user["traffic_limit_bytes"]
        }
    }


@app.post("/api/login")
async def api_login(req: LoginRequest):
    """用户登录"""
    user = authenticate_user(req.username, req.password)
    if not user:
        raise HTTPException(status_code=401, detail="用户名或密码错误")

    token = create_access_token({"user_id": user["id"], "username": user["username"]})
    return {
        "token": token,
        "user": {
            "id": user["id"],
            "username": user["username"],
            "expire_at": user["expire_at"],
            "traffic_limit_bytes": user["traffic_limit_bytes"],
            "is_admin": bool(user["is_admin"])
        }
    }


@app.get("/api/profile")
async def api_profile(user: dict = Depends(get_current_user)):
    """获取用户信息 + 流量统计"""
    traffic = db.get_traffic_summary(user["id"])
    return {
        "id": user["id"],
        "username": user["username"],
        "is_active": bool(user["is_active"]),
        "is_admin": bool(user["is_admin"]),
        "created_at": user["created_at"],
        "expire_at": user["expire_at"],
        "traffic_limit_bytes": user["traffic_limit_bytes"],
        "traffic_used_bytes": traffic["total"],
        "traffic_rx": traffic["rx_bytes"],
        "traffic_tx": traffic["tx_bytes"],
        "has_vpn_config": bool(user.get("wg_public_key"))
    }


@app.get("/api/vpn-config")
async def api_get_vpn_config(user: dict = Depends(get_current_user)):
    """
    获取 VPN 配置。
    如果用户还没有 WireGuard 密钥，自动生成并注册到服务器。
    """
    if not user["is_active"]:
        raise HTTPException(status_code=403, detail="账号已被禁用")

    if user["expire_at"] and time.time() > user["expire_at"]:
        raise HTTPException(status_code=403, detail="账号已过期")

    # 如果已有配置，直接返回
    if user.get("wg_private_key") and user.get("wg_client_ip"):
        config_text = generate_client_config(user["wg_private_key"], user["wg_client_ip"])
        traffic = db.get_traffic_summary(user["id"])
        return {
            "config": config_text,
            "client_ip": user["wg_client_ip"],
            "traffic_used": traffic["total"],
            "traffic_limit": user["traffic_limit_bytes"]
        }

    # 生成新密钥对
    priv_key, pub_key = gen_keypair()

    # 分配 IP
    client_ip = allocate_ip(db, user["id"])

    # 添加到 WireGuard 服务器
    add_peer_to_server(pub_key, client_ip)

    # 保存到数据库
    db.update_user(user["id"],
                    wg_private_key=priv_key,
                    wg_public_key=pub_key,
                    wg_client_ip=client_ip,
                    wg_address=f"{client_ip}/24")

    # 生成配置文本
    config_text = generate_client_config(priv_key, client_ip)

    return {
        "config": config_text,
        "client_ip": client_ip,
        "traffic_used": 0,
        "traffic_limit": user["traffic_limit_bytes"]
    }


# ==================== 管理员后台网页 ====================

@app.get("/admin", response_class=HTMLResponse)
async def admin_login_page(request: Request):
    """管理员登录页"""
    return templates.TemplateResponse("admin_login.html", {"request": request})


@app.post("/admin/login")
async def admin_do_login(request: Request, username: str = Form(...), password: str = Form(...)):
    """管理员登录处理"""
    user = authenticate_user(username, password)
    if not user or not user.get("is_admin"):
        return templates.TemplateResponse("admin_login.html", {
            "request": request, "error": "管理员账号或密码错误"
        })

    token = create_access_token({"user_id": user["id"], "username": user["username"]})
    response = RedirectResponse(url="/admin/dashboard", status_code=302)
    response.set_cookie(key="admin_token", value=token, httponly=True, max_age=86400 * 7)
    return response


@app.get("/admin/dashboard", response_class=HTMLResponse)
async def admin_dashboard(request: Request):
    """管理后台首页 - 用户列表"""
    token = request.cookies.get("admin_token")
    if not token:
        return RedirectResponse(url="/admin", status_code=302)

    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    users = db.get_all_users()
    handshakes = get_peer_handshakes()
    now = time.time()

    # 获取今日流量统计
    today_traffic = db.get_today_traffic()

    # 获取流量趋势（最近7天）
    traffic_trend = db.get_traffic_trend(7)

    # 获取限速状态
    rate_limit_status = {"enabled": False, "rate_mbit": 0, "error": None}
    try:
        rl_result = subprocess.run(
            ["bash", "/opt/shenmi-wifi/rate-limit.sh", "status"],
            capture_output=True, text=True, timeout=10
        )
        rl_output = rl_result.stdout + rl_result.stderr
        # 解析 ENABLED=yes/no
        if "ENABLED=yes" in rl_output:
            rate_limit_status["enabled"] = True
        # 解析 RATE=XXMbit 或 RATE=XXmbit
        for line in rl_output.split('\n'):
            line = line.strip()
            if line.startswith("RATE="):
                m = re.search(r'(\d+)', line)
                if m:
                    rate_limit_status["rate_mbit"] = int(m.group(1))
                    break
        # 检查错误
        if "ERROR=" in rl_output:
            m = re.search(r'ERROR=(\S+)', rl_output)
            if m:
                rate_limit_status["error"] = m.group(1)
    except Exception as e:
        rate_limit_status["error"] = str(e)

    # 组装数据
    user_list = []
    for u in users:
        pub_key = u.get("wg_public_key") or ""
        hs = handshakes.get(pub_key, 0)
        online = (now - hs) < 180 if hs > 0 else False  # 3 分钟内有握手算在线

        # 使用数据库中的累计流量（重启不丢失）
        traffic_rx = u.get("traffic_rx_total", 0) or u.get("rx_bytes", 0)
        traffic_tx = u.get("traffic_tx_total", 0) or u.get("tx_bytes", 0)
        traffic_total = traffic_rx + traffic_tx

        user_list.append({
            "id": u["id"],
            "username": u["username"],
            "is_active": bool(u["is_active"]),
            "is_admin": bool(u["is_admin"]),
            "created_at": u["created_at"],
            "expire_at": u["expire_at"],
            "expired": now > u["expire_at"] if u["expire_at"] else False,
            "traffic_limit_bytes": u["traffic_limit_bytes"],
            "traffic_rx": traffic_rx,
            "traffic_tx": traffic_tx,
            "traffic_total": traffic_total,
            "online": online,
            "last_handshake": hs,
            "client_ip": u.get("wg_client_ip", "-"),
            "has_vpn": bool(u.get("wg_public_key"))
        })

    # 排行榜按总使用量降序
    user_list_sorted = sorted(
        [u for u in user_list if not u["is_admin"]],
        key=lambda x: x["traffic_total"], reverse=True
    )

    return templates.TemplateResponse("admin_dashboard.html", {
        "request": request,
        "users": user_list,
        "leaderboard": user_list_sorted[:10],
        "admin": admin,
        "total_users": len(user_list),
        "online_users": sum(1 for u in user_list if u["online"]),
        "today_traffic": today_traffic,
        "traffic_trend": traffic_trend,
        "rate_limit": rate_limit_status,
        "now": now
    })


@app.post("/admin/user/{user_id}/toggle")
async def admin_toggle_user(request: Request, user_id: int):
    """启用/禁用用户"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    user = db.get_user_by_id(user_id)
    if user and not user["is_admin"]:
        new_status = 0 if user["is_active"] else 1
        db.update_user(user_id, is_active=new_status)

        if new_status == 0:
            # 封禁：移除 peer（保留 IP 和密钥，方便解封后恢复）
            if user.get("wg_public_key"):
                remove_peer_from_server(user["wg_public_key"])
        else:
            # 解封：重新添加 peer 到服务器
            if user.get("wg_public_key") and user.get("wg_client_ip"):
                try:
                    add_peer_to_server(user["wg_public_key"], user["wg_client_ip"])
                except Exception as e:
                    print(f"[WARN] 解封时重新添加 peer 失败: {e}")

    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.post("/admin/user/{user_id}/delete")
async def admin_delete_user(request: Request, user_id: int):
    """删除用户"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    user = db.get_user_by_id(user_id)
    if user and not user["is_admin"]:
        if user.get("wg_public_key"):
            remove_peer_from_server(user["wg_public_key"])
        release_ip(db, user_id)
        db.delete_user(user_id)

    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.post("/admin/user/{user_id}/extend")
async def admin_extend_user(request: Request, user_id: int, days: int = Form(...)):
    """延长用户有效期"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    user = db.get_user_by_id(user_id)
    if user:
        now = time.time()
        base = max(user["expire_at"], now)
        new_expire = base + days * 86400
        db.update_user(user_id, expire_at=new_expire, is_active=1)

    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.post("/admin/user/{user_id}/traffic")
async def admin_set_traffic(request: Request, user_id: int, gb: int = Form(...)):
    """设置用户流量上限"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    db.update_user(user_id, traffic_limit_bytes=gb * 1024 * 1024 * 1024)
    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.post("/admin/user/{user_id}/reset-traffic")
async def admin_reset_traffic(request: Request, user_id: int):
    """重置用户已用流量"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    db.update_user(user_id,
                   traffic_rx_total=0,
                   traffic_tx_total=0,
                   last_wg_rx=0,
                   last_wg_tx=0)
    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.post("/admin/user/create")
async def admin_create_user(request: Request, username: str = Form(...), password: str = Form(...),
                             days: int = Form(default=DEFAULT_EXPIRE_DAYS), traffic_gb: int = Form(default=DEFAULT_TRAFFIC_LIMIT_GB),
                             generate_config: bool = Form(default=False)):
    """管理员创建用户（可选择立即生成 VPN 配置，支持 AJAX 返回配置文本）"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    # 判断是否 AJAX 请求
    is_ajax = request.headers.get("X-Requested-With") == "XMLHttpRequest"

    user = db.create_user(username, password)
    if not user:
        if is_ajax:
            return JSONResponse({"error": "用户名已存在"}, status_code=400)
        return RedirectResponse(url="/admin/dashboard", status_code=302)

    now = time.time()
    db.update_user(user["id"],
                    expire_at=now + days * 86400,
                    traffic_limit_bytes=traffic_gb * 1024 * 1024 * 1024)

    config_text = None
    client_ip = None

    # 如果勾选了立即生成配置
    if generate_config:
        priv_key, pub_key = gen_keypair()
        client_ip = allocate_ip(db, user["id"])
        add_peer_to_server(pub_key, client_ip)
        db.update_user(user["id"],
                        wg_private_key=priv_key,
                        wg_public_key=pub_key,
                        wg_client_ip=client_ip,
                        wg_address=f"{client_ip}/24")
        config_text = generate_client_config(priv_key, client_ip)

    # AJAX 请求返回 JSON（含配置文本）
    if is_ajax and config_text:
        return JSONResponse({
            "success": True,
            "username": username,
            "client_ip": client_ip,
            "config": config_text,
            "config_filename": f"shenmi-wifi-{username}.conf"
        })

    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.get("/admin/user/{user_id}/download-config")
async def admin_download_user_config(request: Request, user_id: int):
    """管理员下载用户的 VPN 配置文件（.conf）"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    user = db.get_user_by_id(user_id)
    if not user or not user.get("wg_private_key") or not user.get("wg_client_ip"):
        raise HTTPException(status_code=404, detail="该用户尚未生成配置")

    config_text = generate_client_config(user["wg_private_key"], user["wg_client_ip"])
    filename = f"shenmi-wifi-{user['username']}.conf"

    from fastapi.responses import Response
    return Response(
        content=config_text,
        media_type="application/octet-stream",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'}
    )


@app.post("/admin/user/{user_id}/restore-peer")
async def admin_restore_peer(request: Request, user_id: int):
    """手动恢复用户的 WireGuard peer（用于修复历史封禁后未重新添加 peer 的问题）"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    user = db.get_user_by_id(user_id)
    if user and user.get("wg_public_key") and user.get("wg_client_ip"):
        try:
            add_peer_to_server(user["wg_public_key"], user["wg_client_ip"])
        except Exception as e:
            return JSONResponse({"error": f"恢复失败: {e}"}, status_code=500)

    return RedirectResponse(url="/admin/dashboard", status_code=302)


@app.get("/admin/user/{user_id}/config")
async def admin_get_user_config(request: Request, user_id: int):
    """管理员查看用户的 VPN 配置文本"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    user = db.get_user_by_id(user_id)
    if not user or not user.get("wg_private_key") or not user.get("wg_client_ip"):
        return JSONResponse({"error": "该用户尚未生成配置"}, status_code=404)

    config_text = generate_client_config(user["wg_private_key"], user["wg_client_ip"])
    return JSONResponse({
        "config": config_text,
        "client_ip": user["wg_client_ip"],
        "username": user["username"]
    })


@app.get("/admin/logout")
async def admin_logout():
    response = RedirectResponse(url="/admin", status_code=302)
    response.delete_cookie("admin_token")
    return response


# ==================== 限速管理 API ====================

@app.get("/admin/api/rate-limit")
async def api_get_rate_limit(request: Request):
    """获取当前限速状态"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return JSONResponse({"error": "未授权"}, status_code=403)

    try:
        result = subprocess.run(
            ["bash", "/opt/shenmi-wifi/rate-limit.sh", "status"],
            capture_output=True, text=True, timeout=10
        )
        output = result.stdout + result.stderr

        enabled = "ENABLED=yes" in output
        rate = 0
        for line in output.split('\n'):
            line = line.strip()
            if line.startswith("RATE="):
                m = re.search(r'(\d+)', line)
                if m:
                    rate = int(m.group(1))
                    break

        error = None
        if "ERROR=" in output:
            m = re.search(r'ERROR=(\S+)', output)
            if m:
                error = m.group(1)

        return {
            "enabled": enabled,
            "rate_mbit": rate,
            "error": error,
            "raw_output": output
        }
    except Exception as e:
        return JSONResponse({"error": str(e)}, status_code=500)


@app.post("/admin/api/rate-limit")
async def api_set_rate_limit(request: Request, rate_mbit: int = Form(...)):
    """设置限速值（返回 JSON，前端 AJAX 处理）"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return JSONResponse({"error": "未授权"}, status_code=403)

    if rate_mbit < 1 or rate_mbit > 1000:
        return JSONResponse({"error": "限速值应在 1-1000 之间"}, status_code=400)

    try:
        result = subprocess.run(
            ["bash", "/opt/shenmi-wifi/rate-limit.sh", "set", f"{rate_mbit}mbit"],
            capture_output=True, text=True, timeout=15
        )
        output = result.stdout + result.stderr

        # 检查是否成功
        if "ENABLED=yes" in output:
            # 提取限速值
            rate_num = rate_mbit
            for line in output.split('\n'):
                line = line.strip()
                if line.startswith("RATE="):
                    m = re.search(r'(\d+)', line)
                    if m:
                        rate_num = int(m.group(1))
                    break
            return JSONResponse({
                "success": True,
                "enabled": True,
                "rate_mbit": rate_num,
                "message": f"限速已设置为 {rate_num} Mbps"
            })
        elif "ERROR=" in output:
            m = re.search(r'ERROR=(\S+)', output)
            err_msg = m.group(1) if m else "未知错误"
            # 提取 DETAIL 行
            detail = ""
            for line in output.split('\n'):
                if line.startswith("DETAIL="):
                    detail = line.replace("DETAIL=", "")
                    break
            return JSONResponse({
                "error": f"限速设置失败: {err_msg}",
                "detail": detail,
                "raw_output": output
            }, status_code=500)
        else:
            # 没有明确的 ENABLED=yes 也没有 ERROR=
            return JSONResponse({
                "error": "限速设置结果未知，请检查脚本输出",
                "raw_output": output
            }, status_code=500)
    except subprocess.TimeoutExpired:
        return JSONResponse({"error": "限速设置超时，请检查 tc 和 ifb 模块是否可用"}, status_code=500)
    except Exception as e:
        return JSONResponse({"error": str(e)}, status_code=500)


@app.post("/admin/api/rate-limit/off")
async def api_rate_limit_off(request: Request):
    """关闭限速（返回 JSON）"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return JSONResponse({"error": "未授权"}, status_code=403)

    try:
        subprocess.run(["bash", "/opt/shenmi-wifi/rate-limit.sh", "stop"],
                       capture_output=True, text=True, timeout=5)
        return JSONResponse({"success": True, "message": "限速已关闭"})
    except Exception as e:
        return JSONResponse({"error": str(e)}, status_code=500)


# ==================== 实时网速 API ====================

@app.get("/admin/api/realtime-speed")
async def api_realtime_speed(request: Request):
    """获取实时合计网速"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return JSONResponse({"error": "未授权"}, status_code=403)

    speed = get_realtime_speed()
    return {
        "download_bps": speed["download_bps"],
        "upload_bps": speed["upload_bps"],
        "total_bps": speed["total_bps"],
        "download_fmt": fmt_bytes(speed["download_bps"]) + "/s",
        "upload_fmt": fmt_bytes(speed["upload_bps"]) + "/s",
        "total_fmt": fmt_bytes(speed["total_bps"]) + "/s",
        "online_peers": speed["online_peers"],
        "timestamp": speed["timestamp"]
    }


# ==================== APK 版本更新 API ====================

# APK 存放目录
APK_DIR = "/opt/shenmi-wifi/apk"
APK_VERSION_FILE = "/opt/shenmi-wifi/apk/version.json"

import json as _json


def _get_apk_version_info():
    """读取版本信息"""
    info = {
        "version_code": 1,
        "version_name": "1.0",
        "message": "",
        "apk_filename": "shenmi-wifi.apk"
    }
    try:
        if os.path.exists(APK_VERSION_FILE):
            with open(APK_VERSION_FILE, "r") as f:
                saved = _json.load(f)
                info.update(saved)
    except Exception:
        pass
    return info


@app.get("/api/version")
async def api_check_version(current: int = 0):
    """检查 APK 新版本"""
    info = _get_apk_version_info()
    has_update = info["version_code"] > current

    return {
        "has_update": has_update,
        "version_code": info["version_code"],
        "version_name": info["version_name"],
        "download_url": "/api/download-apk",
        "message": info.get("message", "")
    }


@app.get("/api/download-apk")
async def api_download_apk():
    """下载 APK 文件"""
    info = _get_apk_version_info()
    apk_path = os.path.join(APK_DIR, info.get("apk_filename", "shenmi-wifi.apk"))

    if not os.path.exists(apk_path):
        raise HTTPException(status_code=404, detail="APK 文件不存在，请先上传")

    filename = info.get("apk_filename", "shenmi-wifi.apk")

    if FileResponse is not None:
        return FileResponse(
            path=apk_path,
            media_type="application/vnd.android.package-archive",
            filename=filename
        )
    else:
        # 回退方案：直接读取文件返回
        from fastapi.responses import Response
        with open(apk_path, "rb") as f:
            content = f.read()
        return Response(
            content=content,
            media_type="application/vnd.android.package-archive",
            headers={"Content-Disposition": f'attachment; filename="{filename}"'}
        )


@app.post("/admin/api/upload-apk")
async def api_upload_apk(request: Request,
                         version_name: str = Form(...),
                         version_code: int = Form(...),
                         message: str = Form(default=""),
                         apk: UploadFile = File(...)):
    """管理员上传新版本 APK"""
    token = request.cookies.get("admin_token")
    admin = get_current_user_from_token(token)
    if not admin or not admin.get("is_admin"):
        return RedirectResponse(url="/admin", status_code=302)

    # 确保 APK 目录存在
    os.makedirs(APK_DIR, exist_ok=True)

    # 保存 APK 文件
    apk_filename = "shenmi-wifi.apk"
    apk_path = os.path.join(APK_DIR, apk_filename)

    content = await apk.read()
    with open(apk_path, "wb") as f:
        f.write(content)

    # 保存版本信息
    version_info = {
        "version_code": version_code,
        "version_name": version_name,
        "message": message,
        "apk_filename": apk_filename,
        "uploaded_at": time.time()
    }
    with open(APK_VERSION_FILE, "w") as f:
        _json.dump(version_info, f, indent=2)

    return RedirectResponse(url="/admin/dashboard", status_code=302)


# ==================== 启动 ====================

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host=API_HOST, port=API_PORT)
