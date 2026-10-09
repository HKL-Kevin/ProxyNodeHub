---
name: mi6-docker-ops
description: >-
  Use this skill whenever managing, debugging, deploying, or maintaining Docker containers
  and services on the Xiaomi Mi 6 (sagit) Android device via ADB network connection.
---

# 小米 6 Docker 容器运维与部署指南 (mi6-docker-ops)

本指南针对部署在小米 6（代号 `sagit`，骁龙 835，ARM64，已 Root）Termux/chroot 环境中的 Docker 容器运维、ADB 远程控制、热更新部署规范及注意事项进行标准化说明。

---

## 核心环境要素

| 属性 | 参数值 | 说明 |
| :--- | :--- | :--- |
| **ADB 目标端点** | `192.168.2.237:5555` | 局域网 ADB 调试端口 |
| **设备局域网 IP** | `192.168.2.6` | 宿主机网络访问 IP |
| **Docker 二进制绝对路径** | `/data/data/com.termux/files/usr/bin/docker` | Android 默认 PATH 无此路径，必须全路径调用 |
| **提权要求** | `su -c '...'` | 必须以 root 身份访问 Docker socket (`/var/run/docker.sock`) |
| **默认应用容器** | `pnodes` | 镜像：`pnodes:latest` (linux/arm64) |
| **容器数据卷宿主映射** | `/data/docker/compose/pnodes/data/` | 对应容器内部 `/data/` 目录 |
| **容器运行用户** | `app` (UID: 10001, 宿主显示 1654) | 宿主提权修改文件后需确保该 UID 有读写权限 |

---

## 常用操作规范

### 1. 执行 Docker 命令的通用命令模板

所有针对小米 6 上 Docker 的操作，必须统一遵循如下前缀模板：

```bash
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker <命令>'"
```

便捷调用可使用内置辅助脚本：[mi6_docker.sh](./scripts/mi6_docker.sh)
```bash
./scripts/mi6_docker.sh ps -a
./scripts/mi6_docker.sh logs pnodes --tail 50
```

### 2. 标准容器生命周期与调试指令

* **容器状态与健康查看**：
  ```bash
  adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker ps -a'"
  adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker stats --no-stream'"
  ```
* **查看实时日志**：
  ```bash
  adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker logs pnodes --tail 100'"
  ```
* **重启应用服务**：
  ```bash
  adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart pnodes'"
  ```
* **容器内执行命令**：
  * 普通用户权限执行：
    ```bash
    adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec pnodes ls -la /app'"
    ```
  * **Root 特权执行（重要：修复权限、清理系统级文件必加 `-u 0`）**：
    ```bash
    adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec -u 0 pnodes chown -R app:app /app'"
    ```

---

## 应用构建与热推部署规范（Hot-Push Runbook）

为避免因多次传输导致代码未能生效，必须严格执行以下六步防踩坑流程（或直接执行脚本 [deploy_app.sh](./scripts/deploy_app.sh)）：

```bash
# 步骤 1：本地构建 linux/arm64 镜像并导出产物
docker build -t pnodes:latest .
docker create --name temp-pnodes pnodes:latest
rm -rf ./published_app
docker cp temp-pnodes:/app ./published_app
docker rm temp-pnodes

# 步骤 2：彻底清理手机临时暂存目录（关键！防止 adb push 产生嵌套目录）
adb -s 192.168.2.237:5555 shell "rm -rf /data/local/tmp/published_app"

# 步骤 3：推送到手机
adb -s 192.168.2.237:5555 push ./published_app /data/local/tmp/published_app

# 步骤 4：拷贝进容器覆盖并修复权限
adb -s 192.168.2.237:5555 shell "su -c '
  /data/data/com.termux/files/usr/bin/docker cp /data/local/tmp/published_app/. pnodes:/app/ &&
  /data/data/com.termux/files/usr/bin/docker exec -u 0 pnodes rm -rf /app/published_app &&
  /data/data/com.termux/files/usr/bin/docker exec -u 0 pnodes chown -R app:app /app
'"

# 步骤 5：重启容器以加载新代码
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart pnodes'"

# 步骤 6：固化变更到本机 Docker 镜像（关键！防止容器销毁后回退）
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker commit pnodes pnodes:latest'"
```

---

## 核心注意事项与排错禁忌

1. **绝对不要直接使用 `docker` 命令**：
   Android 系统底层没有加载 Termux 环境变量，运行 `adb shell docker` 会直接报 `inaccessible or not found`。必须使用完整绝对路径 `/data/data/com.termux/files/usr/bin/docker`。
2. **防范 ADB Push 目录嵌套陷阱**：
   如果手机端目标目录 `/data/local/tmp/published_app` 已存在，`adb push ./published_app /data/local/tmp/published_app` 会在内部生成 `published_app/published_app` 子目录！拷贝进容器后会导致二进制未更新。**每次 push 前必须先 `rm -rf` 手机端目标暂存目录**。
3. **每次热更新必须执行 `docker commit`**：
   在容器内覆盖文件后，务必执行 `docker commit pnodes pnodes:latest`。否则一旦执行 `docker compose down && docker compose up -d`，容器重新实例化就会恢复成旧镜像中的历史版本。
4. **宿主机文件所有权与 SQLite 锁问题**：
   持久化数据库位于宿主 `/data/docker/compose/pnodes/data/nodes.db`。容器内运行用户为 `app` (UID 10001，宿主映射为 1654)。在宿主机上对数据库执行删除、备份或复制操作后，必须确保属主属于 `1654:1654`，否则容器内写库会报 `SQLite Error: attempt to write a readonly database`。
5. **轻量容器工具缺失的诊断替代方案**：
   运行时容器（如 `mcr.microsoft.com/dotnet/aspnet:8.0`）为最小化体积，通常未安装 `ps`, `netstat`, `sqlite3` 等排查工具：
   * **查网络连接状态**：`docker exec pnodes cat /proc/net/tcp | wc -l`
   * **查挂载点**：`docker exec pnodes cat /proc/mounts`
   * **脱机分析数据库**：将数据库拷贝到手机 `/data/local/tmp/`，赋权 666，再通过 `adb pull` 拉到宿主机用本地 SQLite 工具分析。
6. **Android 内存压制（Low Memory Killer）**：
   小米 6 为 6GB RAM，Android 底层开启了激进的后台杀进程机制。容器测速/测活并发数严禁盲目调大（推荐测活 $\le 50$，完整测速 $\le 20$），避免内存暴涨触发系统 LMK 导致 Docker Daemon 或容器进程被强杀。

---

## 扩展参考文档

* [详细命令速查表 (CheatSheet)](./references/adb_docker_cheatsheet.md)
* [数据库维护与排障手册 (Runbook)](./references/maintenance_runbook.md)
