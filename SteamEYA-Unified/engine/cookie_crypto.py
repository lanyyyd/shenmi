"""
Cookie 加密存储模块
使用 PBKDF2 派生密钥 + Fernet(AES-128-CBC + HMAC) 加密
"""
import base64
import json
import os
from pathlib import Path

from cryptography.fernet import Fernet, InvalidToken
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC

# 固定盐值（在本工具语境下，盐值的作用是防止彩虹表，
# 真正的安全性来自用户设置的密码）
_SALT = b"cloud_genshin_login_salt_v1"
_ITERATIONS = 200_000


def _derive_key(password: str) -> bytes:
    """从用户密码派生 Fernet 密钥"""
    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=32,
        salt=_SALT,
        iterations=_ITERATIONS,
    )
    return base64.urlsafe_b64encode(kdf.derive(password.encode("utf-8")))


def encrypt_cookies(cookies: list, combo_token: str, password: str) -> bytes:
    """
    将 cookies 列表和 combo_token 加密为二进制数据
    cookies: Selenium get_cookies() 返回的列表
    combo_token: x-rpc-combo_token 字符串
    """
    payload = {
        "cookies": cookies,
        "combo_token": combo_token,
    }
    raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    fernet = Fernet(_derive_key(password))
    return fernet.encrypt(raw)


def decrypt_cookies(data: bytes, password: str) -> dict:
    """解密，返回 {"cookies": [...], "combo_token": "..."}"""
    fernet = Fernet(_derive_key(password))
    try:
        raw = fernet.decrypt(data)
    except InvalidToken:
        raise ValueError("密码错误或数据已损坏")
    return json.loads(raw.decode("utf-8"))


def save_encrypted(cookies: list, combo_token: str, password: str, path: str) -> None:
    """加密并保存到文件"""
    data = encrypt_cookies(cookies, combo_token, password)
    Path(path).write_bytes(data)


def load_encrypted(path: str, password: str) -> dict:
    """从文件读取并解密"""
    data = Path(path).read_bytes()
    return decrypt_cookies(data, password)
