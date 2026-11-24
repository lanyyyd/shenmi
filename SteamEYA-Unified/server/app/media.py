"""聊天图片缩略图。

只生成用于气泡预览的小图，原图保持原样（不重新编码、不压缩），
所以「点开看大图」拿到的仍是用户上传的原始文件。
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

from .config import UPLOAD_DIR

log = logging.getLogger("media")

THUMB_DIR = UPLOAD_DIR / "thumbs"
THUMB_MAX = 640
THUMB_QUALITY = 80


def thumb_url_for(file_path: str | None) -> Optional[str]:
    """按原图路径找缩略图；没有缩略图（老图片/非图片）返回 None。"""
    if not file_path or not file_path.startswith("/uploads/images/"):
        return None
    stem = Path(file_path).stem
    if (THUMB_DIR / f"{stem}.webp").is_file():
        return f"/uploads/thumbs/{stem}.webp"
    return None


def make_thumbnail(src: Path) -> Optional[str]:
    """给 /uploads/images 下的原图生成 WebP 缩略图，返回相对 URL。

    失败或缩略图反而更大时返回 None，调用方回退原图即可。
    """
    try:
        from PIL import Image, ImageOps
    except Exception:
        return None

    dst = THUMB_DIR / f"{src.stem}.webp"
    try:
        THUMB_DIR.mkdir(parents=True, exist_ok=True)
        with Image.open(src) as im:
            im = ImageOps.exif_transpose(im)
            if getattr(im, "is_animated", False):
                im.seek(0)
            if im.mode in ("P", "LA"):
                im = im.convert("RGBA")
            elif im.mode not in ("RGB", "RGBA"):
                im = im.convert("RGB")
            if max(im.size) > THUMB_MAX:
                im.thumbnail((THUMB_MAX, THUMB_MAX), Image.Resampling.LANCZOS)
            im.save(dst, "WEBP", quality=THUMB_QUALITY, method=4)
        if dst.stat().st_size >= src.stat().st_size:
            dst.unlink(missing_ok=True)
            return None
        return f"/uploads/thumbs/{dst.name}"
    except Exception as e:
        log.debug("生成缩略图失败 %s: %s", src, e)
        try:
            dst.unlink(missing_ok=True)
        except Exception:
            pass
        return None
