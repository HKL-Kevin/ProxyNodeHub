#!/usr/bin/env bash
# ==============================================================================
# mi6_hotupdate.sh: 通用小米 6 容器文件热推与镜像固化工具
# 特性:
#   1. 自动清理 Android 端临时暂存目录，彻底避免 adb push 目录嵌套陷阱
#   2. 通过 docker cp 安全覆盖容器内部目标路径
#   3. 可选以 root (-u 0) 修复容器内部文件属主与权限
#   4. 重启目标容器
#   5. 自动执行 docker commit 将热推修改固化进本地镜像，避免容器重建丢失变更
#
# 用法:
#   ./mi6_hotupdate.sh <容器名称> <本地源路径> <容器内部目标路径> [镜像固化名称:标签] [容器内所有者如 app:app]
# 示例:
#   ./mi6_hotupdate.sh webapp ./build /app/www webapp:latest
#   ./mi6_hotupdate.sh api-server ./bin /app api-server:latest app:app
# ==============================================================================

set -euo pipefail

ADB_TARGET="${ADB_TARGET:-192.168.2.237:5555}"
DOCKER_BIN="/data/data/com.termux/files/usr/bin/docker"

if [ $# -lt 3 ]; then
    echo "用法: $0 <容器名称> <本地源路径> <容器目标路径> [镜像固化名称] [所有者]"
    echo "示例: $0 myapp ./dist /app/dist myapp:latest app:app"
    exit 1
fi

CONTAINER="$1"
LOCAL_PATH="$2"
CONTAINER_DEST="$3"
COMMIT_IMAGE="${4:-}"
OWNER="${5:-}"

REMOTE_TMP="/data/local/tmp/mi6_hotupdate_$$"

if [ ! -e "$LOCAL_PATH" ]; then
    echo "错误: 本地路径不存在: $LOCAL_PATH" >&2
    exit 1
fi

echo "==> [1/5] 清理手机端临时暂存区 (防嵌套)..."
adb -s "$ADB_TARGET" shell "rm -rf '$REMOTE_TMP'"

echo "==> [2/5] 推送文件至手机暂存区..."
adb -s "$ADB_TARGET" push "$LOCAL_PATH" "$REMOTE_TMP"

echo "==> [3/5] 将更新复制进容器 $CONTAINER ($CONTAINER_DEST)..."
adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN cp \"$REMOTE_TMP/.\" \"$CONTAINER:$CONTAINER_DEST/\"'"

# 清理手机宿主临时目录
adb -s "$ADB_TARGET" shell "rm -rf '$REMOTE_TMP'"

if [ -n "$OWNER" ]; then
    echo "==> 修复容器内文件属主为 $OWNER..."
    adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN exec -u 0 \"$CONTAINER\" chown -R \"$OWNER\" \"$CONTAINER_DEST\"'"
fi

echo "==> [4/5] 重启容器 $CONTAINER..."
adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN restart \"$CONTAINER\"'"

if [ -n "$COMMIT_IMAGE" ]; then
    echo "==> [5/5] 固化变更到本地 Docker 镜像 ($COMMIT_IMAGE)..."
    adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN commit \"$CONTAINER\" \"$COMMIT_IMAGE\"'"
fi

echo "==> 更新完成！容器 $CONTAINER 已成功生效。"
