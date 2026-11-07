#!/bin/sh
# X-Ray 焊线检测（Python 版）开发菜单 —— macOS/Linux 开发用；Windows 用 build.cmd
cd "$(dirname "$0")" || exit 1
PY=python3
[ -x .venv/bin/python ] && PY=.venv/bin/python

echo "==================  X-Ray 焊线检测（Python 版） =================="
echo "  [1] 安装/修复环境 (.venv + pip install -r requirements.txt)"
echo "  [2] 运行检测程序"
echo "  [3] 运行注册机（界面）"
echo "  [4] 运行测试"
echo "  [0] 退出"
printf "请选择: "
read -r M
case "$M" in
    1)
        python3 -m venv .venv
        .venv/bin/python -m pip install -U pip setuptools wheel
        .venv/bin/python -m pip install -r requirements.txt
        echo "环境就绪" ;;
    2) "$PY" main.py ;;
    3) "$PY" tools/license_tool.py ;;
    4) "$PY" -m unittest discover -s tests -v ;;
    0) exit 0 ;;
    *) echo "无效选项" ;;
esac
