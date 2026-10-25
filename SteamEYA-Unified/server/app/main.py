"""统一后端网关入口。

单 FastAPI 应用合并三块业务：
  - 统一账号中心            /api/auth/*
  - Steam 账号分发           /api/steam/*
  - 云游戏 Cookie            /api/cloud/*
  - 聊天室（HTTP + WS）      /api/* 与 /ws
  - 离线同步                /api/sync/*
并托管聊天 Web 前端（WebView2 用）与上传文件。
"""
from __future__ import annotations

import logging
import os
import socket

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from . import ws as ws_module
from .config import UPLOAD_DIR, WEB_DIR
from .db import init_db
from .lan import LanDiscovery
from .routers import (admin, ai, ai_image, auth, chat, cloud, eat, gacha, lan, level, points, redpacket,
                      save, steam, sync, tips, update)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("gateway")

app = FastAPI(title="SteamEYA 统一网关", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
def _startup():
    init_db()
    log.info("统一后端网关已启动，数据目录: %s", UPLOAD_DIR.parent)
    # 局域网发现（UDP 广播）：
    #   - 离线模式（EYA_LAN_MODE=1）默认开启；
    #   - 在线模式可显式设 EYA_LAN_DISCOVERY=1 开启，以支持“在线状态下也能做局域网互传”。
    lan_mode = os.getenv("EYA_LAN_MODE") == "1"
    lan_discovery = os.getenv("EYA_LAN_DISCOVERY", "1" if lan_mode else "0") == "1"
    if lan_discovery:
        port = int(os.getenv("EYA_LAN_PORT", "8898"))
        name = os.getenv("EYA_DEVICE_NAME") or socket.gethostname()
        discovery = LanDiscovery(port=port, name=name)
        discovery.start()
        lan.set_discovery(discovery)
        log.info("局域网发现已启用：name=%s port=%d mode=%s", name, port, "lan" if lan_mode else "online")

    # 红包到期结算：后台线程每分钟检查一次，把过期未领完的剩余退回发包者
    import threading
    import time as _time

    def _redpacket_sweeper():
        from . import redpacket as RP
        while True:
            try:
                RP.expire_due()
            except Exception:
                pass
            _time.sleep(60)

    threading.Thread(target=_redpacket_sweeper, name="redpacket-sweeper", daemon=True).start()

    # 「小火人」主动发言：后台线程每 30s 检查一次，仅在后台开关打开且群聊冷场时触发
    from . import aicore
    aicore.start_active_speak()

    # AI 自动配图：新建无图店铺/菜品入队后，由这个后台线程逐条生成
    from . import aigen
    aigen.start_worker()

    # 导航 tips：后台预热一言缓冲池（上游较慢，避免首批请求只拿到兜底句）
    tips.start_prefetch()


@app.get("/health")
@app.get("/api/health")
def health():
    return {"ok": True, "status": "ok", "service": "steameya-gateway"}


# 业务路由
app.include_router(auth.router)
app.include_router(steam.router)
app.include_router(cloud.router)
app.include_router(sync.router)
app.include_router(save.router)
app.include_router(lan.router)
app.include_router(chat.router)
app.include_router(admin.router)
app.include_router(update.router)
app.include_router(tips.router)
app.include_router(points.router)
app.include_router(eat.router)
app.include_router(ai.router)
app.include_router(ai_image.router)
app.include_router(level.router)
app.include_router(gacha.router)
app.include_router(redpacket.router)
app.include_router(ws_module.router)

# 上传文件（聊天图片/文件/头像/表情）
app.mount("/uploads", StaticFiles(directory=str(UPLOAD_DIR)), name="uploads")

# 聊天 Web 前端（放最后，避免遮蔽上面的 API/WS 路由）
if WEB_DIR.exists():
    app.mount("/", StaticFiles(directory=str(WEB_DIR), html=True), name="web")


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8897)