"""离线 / 局域网模式启动脚本（客户端在无网络时本机运行）。

与在线网关同一套代码，区别：
- 允许仅凭昵称登录（EYA_ALLOW_LOCAL_AUTH=1）；
- 开启 UDP 局域网发现（EYA_LAN_MODE=1）；
- 监听 0.0.0.0，同网段其他实例可直连做聊天与文件互传；
- 数据目录建议指向**非 C 盘**（由客户端设置 EYA_DATA_DIR），如 D:\\SteamEYA\\data。

用法：
    set EYA_DATA_DIR=D:\\SteamEYA\\data
    python run_local.py --port 8898 --name "我的电脑"
"""
from __future__ import annotations

import argparse
import os

os.environ.setdefault("EYA_ALLOW_LOCAL_AUTH", "1")
os.environ.setdefault("EYA_LAN_MODE", "1")


def main() -> None:
    parser = argparse.ArgumentParser(description="SteamEYA 离线/局域网网关")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8898)
    parser.add_argument("--name", default=os.getenv("EYA_DEVICE_NAME", ""))
    args = parser.parse_args()

    if args.name:
        os.environ["EYA_DEVICE_NAME"] = args.name
    os.environ["EYA_LAN_PORT"] = str(args.port)

    import uvicorn

    from app.main import app

    uvicorn.run(app, host=args.host, port=args.port)


if __name__ == "__main__":
    main()