"""在线统一后端网关启动脚本（部署到服务器）。

用法：python run_server.py --host 0.0.0.0 --port 8897
"""
from __future__ import annotations

import argparse


def main() -> None:
    parser = argparse.ArgumentParser(description="SteamEYA 在线统一网关")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8897)
    args = parser.parse_args()

    import uvicorn

    from app.main import app

    uvicorn.run(app, host=args.host, port=args.port)


if __name__ == "__main__":
    main()