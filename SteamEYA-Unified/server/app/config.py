"""统一后端网关 - 配置。

所有路径默认落在 <server>/data 下，可用环境变量覆盖。
"""
from __future__ import annotations

import os
from pathlib import Path

# server/ 目录
BASE_DIR = Path(__file__).resolve().parent.parent

# 数据目录（数据库 / 主密钥 / 上传文件）
DATA_DIR = Path(os.getenv("EYA_DATA_DIR", str(BASE_DIR / "data")))
DATA_DIR.mkdir(parents=True, exist_ok=True)

DB_PATH = DATA_DIR / "unified.db"
KEY_PATH = DATA_DIR / "master.key"
UPLOAD_DIR = DATA_DIR / "uploads"
AVATAR_DIR = UPLOAD_DIR / "avatars"
STICKER_DIR = UPLOAD_DIR / "stickers"
for _d in (UPLOAD_DIR, AVATAR_DIR, STICKER_DIR, UPLOAD_DIR / "images", UPLOAD_DIR / "videos", UPLOAD_DIR / "files"):
    _d.mkdir(parents=True, exist_ok=True)

# 聊天前端静态目录（web/，来自原聊天室 public/）
WEB_DIR = Path(os.getenv("EYA_WEB_DIR", str(BASE_DIR.parent / "web")))

# 鉴权
SECRET_KEY = os.getenv("EYA_SECRET_KEY", "change-me-unified-2026")
ALGORITHM = "HS256"
ACCESS_TOKEN_EXPIRE_MINUTES = 60 * 24 * 7  # 7 天

# 上传限制
MAX_UPLOAD = 50 * 1024 * 1024   # 普通文件 50MB
MAX_AVATAR = 2 * 1024 * 1024    # 头像 2MB
MAX_STICKER = 2 * 1024 * 1024   # 表情 2MB

# 默认管理员（首次初始化时创建）
ADMIN_USERNAME = os.getenv("EYA_ADMIN_USERNAME", "admin")
ADMIN_PASSWORD = os.getenv("EYA_ADMIN_PASSWORD", "admin123")

# 注册是否需要邀请码
INVITE_REQUIRED_DEFAULT = True

# 离线/局域网模式：本机安装唯一标识（客户端生成并持久化，用于同步幂等去重）
INSTALL_ID = os.getenv("EYA_INSTALL_ID", "")
LAN_MODE = os.getenv("EYA_LAN_MODE") == "1"