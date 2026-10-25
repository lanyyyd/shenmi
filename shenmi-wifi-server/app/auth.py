"""
神秘 WiFi - 用户认证模块（JWT）
"""
import time
from datetime import datetime, timedelta
from jose import jwt, JWTError
from passlib.context import CryptContext
from config import JWT_SECRET, JWT_ALGORITHM, JWT_EXPIRE_HOURS, ADMIN_USERNAME, ADMIN_PASSWORD
from app.database import Database, verify_password

pwd_context = CryptContext(schemes=["bcrypt"], deprecated="auto")
db = Database()


def create_access_token(data: dict) -> str:
    """生成 JWT Token"""
    to_encode = data.copy()
    expire = datetime.utcnow() + timedelta(hours=JWT_EXPIRE_HOURS)
    to_encode.update({"exp": expire.timestamp()})
    return jwt.encode(to_encode, JWT_SECRET, algorithm=JWT_ALGORITHM)


def decode_token(token: str) -> dict:
    """解码 JWT Token"""
    try:
        payload = jwt.decode(token, JWT_SECRET, algorithms=[JWT_ALGORITHM])
        return payload
    except JWTError:
        return None


def authenticate_user(username: str, password: str) -> dict:
    """
    验证用户登录
    返回 user dict 或 None
    """
    # 先检查是否是管理员
    if username == ADMIN_USERNAME and password == ADMIN_PASSWORD:
        admin = db.get_user_by_username(ADMIN_USERNAME)
        if not admin:
            admin = db.create_user(ADMIN_USERNAME, ADMIN_PASSWORD, is_admin=True)
        return admin

    # 普通用户
    user = db.get_user_by_username(username)
    if not user:
        return None

    if not verify_password(password, user["password_hash"], user["salt"]):
        return None

    if not user["is_active"]:
        return None

    return user


def register_user(username: str, password: str) -> dict:
    """注册新用户"""
    if len(username) < 3:
        return {"error": "用户名至少 3 个字符"}
    if len(password) < 6:
        return {"error": "密码至少 6 个字符"}

    user = db.create_user(username, password)
    if not user:
        return {"error": "用户名已存在"}

    return {"user": user}


def get_current_user_from_token(token: str) -> dict:
    """从 JWT Token 获取用户信息"""
    payload = decode_token(token)
    if not payload:
        return None

    user_id = payload.get("user_id")
    if not user_id:
        return None

    return db.get_user_by_id(user_id)
