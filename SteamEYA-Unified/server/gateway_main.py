"""打包入口：既可作在线网关，也可作离线/局域网本地网关。

- 在线：python gateway_main.py --port 8897
- 离线：python gateway_main.py --local --port 8898 --name "我的电脑"

打包（Windows）见 build_gateway.bat，产物 steameya-gateway.exe 供客户端 GatewayLauncher 调用。
"""
from __future__ import annotations

import argparse
import os


def main() -> None:
    parser = argparse.ArgumentParser(description="SteamEYA 统一网关")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8897)
    parser.add_argument("--local", action="store_true", help="以离线/局域网模式启动")
    parser.add_argument("--name", default="")
    args = parser.parse_args()

    if args.local:
        os.environ["EYA_ALLOW_LOCAL_AUTH"] = "1"
        os.environ["EYA_LAN_MODE"] = "1"
        os.environ["EYA_LAN_PORT"] = str(args.port)
        if args.name:
            os.environ["EYA_DEVICE_NAME"] = args.name

    import uvicorn

    from app.main import app

    uvicorn.run(app, host=args.host, port=args.port)


if __name__ == "__main__":
    main()