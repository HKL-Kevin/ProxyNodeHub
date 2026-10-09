#!/usr/bin/env bash
# ==============================================================================
# deploy_app.sh: 构建并将应用热推发布至小米 6 Docker 容器
# 特性:
#   1. 自动构建 linux/arm64 镜像并导出构建产物
#   2. 预先清理手机端暂存目录，彻底避免 adb push 目录嵌套陷阱
#   3. 特权覆盖更新容器并修正 app:app 属主
#   4. 重启应用并自动提交固化镜像 (docker commit)
# ==============================================================================

set -euo pipefail

ADB_TARGET="${ADB_TARGET:-192.168.2.237:5555}"
DOCKER_BIN="/data/data/com.termux/files/usr/bin/docker"
CONTAINER_NAME="pnodes"
IMAGE_NAME="pnodes:latest"
LOCAL_APP_DIR="./published_app"
REMOTE_TMP_DIR="/data/local/tmp/published_app"

echo "==> 1. 本地构建 Docker 镜像 (linux/arm64)..."
docker build -t "$IMAGE_NAME" .

echo "==> 2. 导出 /app 产物..."
docker create --name temp-deploy "$IMAGE_NAME"
rm -rf "$LOCAL_APP_DIR"
docker cp temp-deploy:/app "$LOCAL_APP_DIR"
docker rm temp-deploy

echo "==> 3. 清理设备暂存目录 (防嵌套)..."
adb -s "$ADB_TARGET" shell "rm -rf $REMOTE_TMP_DIR"

echo "==> 4. 推送构建产物到小米 6..."
adb -s "$ADB_TARGET" push "$LOCAL_APP_DIR" "$REMOTE_TMP_DIR"

echo "==> 5. 更新容器文件并修正所有权..."
adb -s "$ADB_TARGET" shell "su -c '
  $DOCKER_BIN cp $REMOTE_TMP_DIR/. $CONTAINER_NAME:/app/ &&
  $DOCKER_BIN exec -u 0 $CONTAINER_NAME rm -rf /app/published_app &&
  $DOCKER_BIN exec -u 0 $CONTAINER_NAME chown -R app:app /app
'"

echo "==> 6. 重启服务容器..."
adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN restart $CONTAINER_NAME'"

echo "==> 7. 固化镜像 (docker commit)..."
adb -s "$ADB_TARGET" shell "su -c '$DOCKER_BIN commit $CONTAINER_NAME $IMAGE_NAME'"

echo "==> 部署完成！服务已在小米 6 成功上线并固化。"
