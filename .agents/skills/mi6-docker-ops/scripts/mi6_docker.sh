#!/usr/bin/env bash
# ==============================================================================
# mi6_docker.sh: 在小米 6 上免输长指令执行 Docker 命令的快捷工具
# 用法:
#   ./mi6_docker.sh ps -a
#   ./mi6_docker.sh logs pnodes --tail 50
#   ./mi6_docker.sh exec -u 0 pnodes ls -la /app
# ==============================================================================

set -euo pipefail

ADB_TARGET="${ADB_TARGET:-192.168.2.237:5555}"
DOCKER_BIN="/data/data/com.termux/files/usr/bin/docker"

if [ $# -eq 0 ]; then
    echo "用法: $0 <docker子命令与参数>"
    echo "示例: $0 ps -a"
    echo "      $0 logs pnodes --tail 50"
    echo "      $0 restart pnodes"
    exit 1
fi

# 拼接所有参数并做适度转义
CMD=""
for arg in "$@"; do
    CMD="$CMD $(printf %q "$arg")"
done

adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN $CMD'"
