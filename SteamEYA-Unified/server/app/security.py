"""安全工具：密码哈希、JWT(HS256)、云游戏 Cookie 主密钥加密。

密码哈希统一用 scrypt（`scrypt$salt$hash`），同时兼容三套旧服务的旧格式，
便于把历史账号平滑迁移到统一账号库。
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import secrets
import time
from typing import Any, Optional

from .config import ALGORITHM, KEY_PATH, SECRET_KEY

# ---------------------------------------------------------------- 时间/随机
def now_ms() -> int:
    return int(time.time() * 1000)


def now_s() -> int:
    return int(time.time())


def gen_token(nbytes: int = 32) -> str:
    return secrets.token_hex(nbytes)


# ---------------------------------------------------------------- 密码哈希
_SCRYPT_N, _SCRYPT_R, _SCRYPT_P = 2 ** 14, 8, 1


def _scrypt_hash(password: str, salt_hex: str) -> str:
    dk = hashlib.scrypt(
        password.encode("utf-8"),
        salt=bytes.fromhex(salt_hex),
        n=_SCRYPT_N, r=_SCRYPT_R, p=_SCRYPT_P, dklen=32,
    )
    return dk.hex()


def hash_password(password: str) -> str:
    """新格式：scrypt$salt$hash"""
    salt_hex = secrets.token_hex(16)
    return f"scrypt${salt_hex}${_scrypt_hash(password, salt_hex)}"


def verify_password(password: str, stored: str) -> bool:
    """校验密码，兼容旧格式。"""
    if not stored:
        return False
    try:
        # 新格式
        if stored.startswith("scrypt$"):
            _, salt_hex, digest = stored.split("$", 2)
            calc = _scrypt_hash(password, salt_hex)
            return hmac.compare_digest(calc, digest)
        # Node 聊天室旧格式：scrypt(password, salt, 64)，盐与散列分开存
        # 迁移时合并存为 nodescrypt$<salt>$<scrypt64 hex>
        if stored.startswith("nodescrypt$"):
            _, salt, digest = stored.split("$", 2)
            calc = hashlib.scrypt(password.encode("utf-8"), salt=salt.encode("utf-8"),
                                  n=2 ** 14, r=8, p=1, dklen=64).hex()
            return hmac.compare_digest(calc, digest)
        # Steam 旧格式：salt:sha256(salt+password)
        if ":" in stored:
            salt, digest = stored.split(":", 1)
            calc = hashlib.sha256((salt + password).encode("utf-8")).hexdigest()
            return hmac.compare_digest(calc, digest)
        # 云游戏旧格式：sha256(固定盐 + password)
        calc = hashlib.sha256(("cloud_genshin_salt_v1" + password).encode("utf-8")).hexdigest()
        return hmac.compare_digest(calc, stored)
    except Exception:
        return False


def needs_rehash(stored: str) -> bool:
    return not stored.startswith("scrypt$")


# ---------------------------------------------------------------- JWT (HS256)
def _b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def _b64url_decode(s: str) -> bytes:
    pad = "=" * (-len(s) % 4)
    return base64.urlsafe_b64decode(s + pad)


def create_access_token(user_id: int, username: str, is_admin: bool,
                        expire_minutes: int = 60 * 24 * 7) -> str:
    header = {"alg": ALGORITHM, "typ": "JWT"}
    payload = {
        "sub": str(user_id),
        "username": username,
        "is_admin": bool(is_admin),
        "iat": now_s(),
        "exp": now_s() + expire_minutes * 60,
    }
    seg = f"{_b64url(json.dumps(header, separators=(',', ':')).encode())}." \
          f"{_b64url(json.dumps(payload, separators=(',', ':')).encode())}"
    sig = hmac.new(SECRET_KEY.encode(), seg.encode(), hashlib.sha256).digest()
    return f"{seg}.{_b64url(sig)}"


def decode_access_token(token: str) -> Optional[dict[str, Any]]:
    try:
        seg, sig = token.rsplit(".", 1)
        expected = hmac.new(SECRET_KEY.encode(), seg.encode(), hashlib.sha256).digest()
        if not hmac.compare_digest(_b64url(expected), sig):
            return None
        payload = json.loads(_b64url_decode(seg.split(".")[1]).decode())
        if int(payload.get("exp", 0)) < now_s():
            return None
        return payload
    except Exception:
        return None


# ---------------------------------------------------------------- 云游戏 Cookie 加密
def _get_fernet():
    from cryptography.fernet import Fernet

    if not KEY_PATH.exists():
        KEY_PATH.write_bytes(Fernet.generate_key())
        try:
            os.chmod(KEY_PATH, 0o600)
        except Exception:
            pass
    return Fernet(KEY_PATH.read_bytes())


def encrypt_blob(obj: Any) -> bytes:
    return _get_fernet().encrypt(json.dumps(obj, ensure_ascii=False).encode("utf-8"))


def decrypt_blob(data: bytes) -> Any:
    return json.loads(_get_fernet().decrypt(data).decode("utf-8"))