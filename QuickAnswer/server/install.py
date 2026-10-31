#!/usr/bin/env python3
"""把 QuickAnswer 题库模块装进现有的 shenmi-server。

用法：
    python install.py <shenmi-server 根目录>
    python install.py                 # 自动在当前目录附近找 server/app

做的事情只有三件（全部幂等，重复执行安全）：
    1. qa_core.py   → server/app/qa_core.py
    2. qa_router.py → server/app/routers/qa.py
    3. admin-qa.html→ web/admin-qa.html
    4. 在 server/app/main.py 里补 import 与 include_router（已存在则跳过）

不改动任何现有业务代码，回滚只需删掉上面三个文件并撤销 main.py 的那两行。
"""
from __future__ import annotations

import shutil
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent


def find_server_root(explicit: str | None) -> Path | None:
    if explicit:
        p = Path(explicit).expanduser().resolve()
        return p if (p / "server" / "app").is_dir() else None

    for candidate in (HERE.parent, HERE.parent.parent, Path.cwd()):
        if (candidate / "server" / "app").is_dir():
            return candidate
        if (candidate / "SteamEYA-Unified" / "server" / "app").is_dir():
            return candidate / "SteamEYA-Unified"

    return None


def patch_main(main_py: Path) -> bool:
    src = main_py.read_text(encoding="utf-8")

    if "routers import" not in src:
        print(f"  ! {main_py} 结构不符合预期，请手动添加 include_router")
        return False

    changed = False

    # 1) import：把 qa 加进 from .routers import (...) 里
    #    注意要先 strip 再比较 —— 直接 split(",") 得到的是 " qa"，裸比较永远不命中，
    #    重复执行就会拼出 "qa, qa"。
    marker = "from .routers import ("
    if marker in src:
        head, tail = src.split(marker, 1)
        names, rest = tail.split(")", 1)
        items = [x.strip() for x in names.replace("\n", " ").split(",") if x.strip()]
        if "qa" not in items:
            items.append("qa")
            src = head + marker + ", ".join(items) + ")" + rest
            changed = True

    # 2) include_router：插在第一个 include_router 前面，保证它在静态目录挂载之前
    if "qa.router" not in src:
        anchor = "app.include_router("
        if anchor in src:
            src = src.replace(anchor, "app.include_router(qa.router)\n" + anchor, 1)
            changed = True

    if changed:
        main_py.write_text(src, encoding="utf-8")

    return changed


def main() -> int:
    root = find_server_root(sys.argv[1] if len(sys.argv) > 1 else None)
    if root is None:
        print("找不到 shenmi-server 目录。请显式传入根目录：")
        print("    python install.py /path/to/shenmi-server")
        return 1

    app_dir = root / "server" / "app"
    routers_dir = app_dir / "routers"
    web_dir = root / "web"

    print(f"目标后端：{root}")

    if not routers_dir.is_dir():
        print(f"  ! 缺少目录 {routers_dir}")
        return 1

    shutil.copy2(HERE / "qa_core.py", app_dir / "qa_core.py")
    print("  ✓ server/app/qa_core.py")

    shutil.copy2(HERE / "qa_router.py", routers_dir / "qa.py")
    print("  ✓ server/app/routers/qa.py")

    if web_dir.is_dir():
        shutil.copy2(HERE / "admin-qa.html", web_dir / "admin-qa.html")
        print("  ✓ web/admin-qa.html")
    else:
        print(f"  ! 跳过管理页：{web_dir} 不存在")

    main_py = app_dir / "main.py"
    if main_py.is_file():
        if patch_main(main_py):
            print("  ✓ 已修改 server/app/main.py（+ qa router）")
        else:
            print("  · server/app/main.py 无需修改")
    else:
        print(f"  ! 找不到 {main_py}")

    print()
    print("完成。重启后端后访问 /admin-qa.html 配置模型。")
    print("首次使用记得在「AI 模型」里添加一个模型，否则搜题只会查题库、不会问 AI。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
