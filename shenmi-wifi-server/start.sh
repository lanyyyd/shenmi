#!/bin/bash
# 宝塔 Python 项目启动脚本
# 以面板项目环境执行，端口与 config.py 的 API_PORT 保持一致
cd "$(dirname "$0")"
export PYTHONPATH="$(pwd)"
exec python -m uvicorn app.main:app --host 0.0.0.0 --port 8999