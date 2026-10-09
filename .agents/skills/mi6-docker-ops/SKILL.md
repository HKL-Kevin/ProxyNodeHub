---
name: mi6-docker-ops
description: >-
  Universal guide and runbook for operating, managing, and debugging Docker on Xiaomi Mi 6
  (sagit, Android ARM64) via ADB, covering Android kernel differences, volume mount caveats,
  parameter compatibility, and container maintenance standards.
---

# 小米 6 Docker 通用运维与规范指南 (mi6-docker-ops)

本指南针对运行在小米 6（高通骁龙 835 / MSM8998，ARM64 架构，已 Root）Android 系统（基于 Termux/chroot/Linux Deploy 底座）上的 Docker 容器运行环境。

由于 Android 系统的内核配置、文件系统体系（FUSE/sdcardfs vs ext4）、安全策略（SELinux/Magisk）、网络栈（netd/iptables）及进程调度（LMK）与常规 Linux 发行版（如 Ubuntu/Debian/CentOS）存在显著差异，所有 Docker 命令执行与容器维护必须严格遵循本指南规范。

---

## 一、环境底座与通用 ADB 调用机制

### 1. 通用调用前缀
Android 系统的默认环境变量（`PATH`）不包含 Termux 或容器软件安装路径，且 Docker daemon 的 UNIX socket（`/var/run/docker.sock`）受权限保护。

因此，**所有 Docker 相关命令必须通过 ADB 提权并使用绝对路径调用**：

```bash
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker <命令与参数>'"
```

* **目标设备 ADB 端口**：默认 `192.168.2.237:5555`（可通过环境变量 `ADB_TARGET` 覆盖）。
* **Docker CLI 路径**：`/data/data/com.termux/files/usr/bin/docker`。
* **快捷执行脚本**：可直接使用 [mi6_docker.sh](./scripts/mi6_docker.sh)：
  ```bash
  ./scripts/mi6_docker.sh ps -a
  ./scripts/mi6_docker.sh stats --no-stream
  ```

### 2. 多层 Shell 引号转义原则
从宿主机终端（zsh/bash）通过 `adb shell` 再经过 Android `su -c`，经历**三层命令行解析**。
* **常规命令**：外层双引号，`su -c` 内使用单引号包裹：
  ```bash
  adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker inspect <容器名>'"
  ```
* **含嵌套参数或双引号的命令**：内层双引号必须使用反斜杠转义：
  ```bash
  adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec <容器名> sh -c \"echo hello\"'"
  ```

---

## 二、目录映射与卷挂载特殊规范 (Volume & Mount Rules)

这是小米 6 Docker 与常规 Docker 最大的区别点，90% 的启动报错与运行异常源于此。

### 1. 挂载点文件系统三级划分与绝对禁区
Android 的存储体系与通用 Linux 完全不同，盲目挂载会导致数据损坏或完全无法读取：

* ❌ **禁区一：严禁挂载 `/sdcard` / `/storage/emulated/0`（FUSE/sdcardfs 模拟层）**
  * **底层缺陷**：属于虚拟文件系统，**不具备 POSIX 权限（chmod/chown 无效），不支持软硬链接和 UNIX Socket**。
  * **致命后果**：数据库（SQLite WAL 模式、MySQL、Redis）因缺少标准文件锁和 mmap 支持直接报错崩溃；可执行文件无法赋予 `+x` 执行权限。
* ❌ **禁区二：严禁挂载 Termux 私有目录 `/data/data/com.termux/...`（Android FBE 凭据加密陷阱）**
  * **底层缺陷**：Android 启用基于文件的加密（FBE, File-Based Encryption），Termux 私有数据位于 **CE（Credential Encrypted，凭据加密）** 存储区，受用户锁屏密码密钥环和 Android `app_data_file` 沙箱保护。
  * **致命后果**：Docker 容器处于独立的挂载命名空间（Mount Namespace），无法解密 CE 存储。挂载后容器内部表现为**读取目录为空、文件名显示为加密密文散列乱码，或直接报错 `Required key not available` / `Input/output error`**。设备锁屏后挂载更会直接失效。
* ✅ **唯一正解：系统 Root 级原生 ext4/f2fs 分区（`/data/docker/...`）**
  * **标准规范**：统一在 `/data/docker/` 目录下规划（例如 `/data/docker/volumes/<容器名>/`）。
  * **核心优势**：直接位于系统 ext4/f2fs 分区根下，**不受 Android App FBE 凭据加密限制**，具备纯正 POSIX 权限、支持标准文件锁、UNIX 套接字与软硬链接。

### 2. 绝对路径强制
* 在 ADB `su -c` 下执行时，当前工作目录通常被重置为 `/` 或 Termux 主目录。
* **规范**：执行 `docker run -v` 或 `docker compose` 时，**挂载源路径必须是宿主机绝对路径**（如 `/data/docker/volumes/myapp:/app/data`），严禁使用 `./data` 相对路径。

### 3. 宿主 Android AID 与容器内部 UID/GID 权限映射
* Android 系统拥有一套专有的 AID 用户体系（如 UID 0 为 root，1000 为 system，2000 为 shell，10000+ 为应用分配 UID）。
* 若容器以非 root 用户（如 UID 1000、10001）运行：
  * 在宿主机以 root 身份创建的文件或数据卷，容器内无权写入。
  * **规范**：容器启动前或在宿主机操作数据后，必须将挂载目录赋权给对应 UID，或开放工作目录权限（如 `chmod -R 777 <目录>` 或 `chown -R <UID>:<GID> <目录>`）。

### 4. Magisk 挂载命名空间（Mount Namespace）隔离
* 若通过 Magisk Root，默认情况下每个 root 进程可能处于独立的挂载命名空间。
* 若在某一终端挂载了目录但在 Docker 中不可见，需使用 `su -mm -c '...'`（Mount Master）进入全局命名空间。

---

## 三、Docker 参数支持与兼容性矩阵

由于 Android 4.4+ 定制内核对 Linux cgroups、SELinux 及安全特性的裁剪，部分常规 Docker 参数在小米 6 上表现特殊：

| 参数类别 | 参数 | 在小米 6 上的表现与处理规范 |
| :--- | :--- | :--- |
| **架构约束** | `--platform` / 镜像标签 | **强制仅限 `linux/arm64`（或 `aarch64`）**。骁龙 835 是 64 位 ARM，严禁拉取或构建 amd64 镜像（无法转译）。 |
| **内存限制** | `--memory`, `--memory-swap` | **视内核支持而定**。部分 Android 内核未编译 `CONFIG_MEMCG_SWAP`，传入该参数可能被内核忽略或报 `WARNING: No memory limit support`。 |
| **CPU 限制** | `--cpus`, `--cpu-shares` | 支持基础配额，但不建议在高负载下设过低，避免大小核调度不均导致严重卡死。 |
| **特权与安全** | `--privileged` | **推荐在需要访问硬件或网络协议栈时使用**。Android 的 SELinux 可能会阻止容器的特定系统调用，加 `--privileged` 可有效规避安全上下文拒绝。 |
| **网络模式** | `--net=host` vs `bridge` | **核心差异**：<br>1. Android 的 `netd` 和防火墙规则会定期重置 iptables，偶尔会导致 Docker 的 bridge 转发链失效。<br>2. 桥接模式下宿主机无标准 `/etc/resolv.conf`，容器 DNS 偶尔解析缓慢。<br>3. **对网络延迟、吞吐要求高或不需要端口隔离的服务，优先推荐 `--net=host`**。 |
| **端口绑定** | `-p 0.0.0.0:port:port` | **必须显式绑定 `0.0.0.0`**。若仅写 `-p 8080:8080`，部分 Android 系统可能仅监听本地回路（`127.0.0.1`），导致局域网或 USB 反向网络无法访问。 |

---

## 四、容器通用维护与更新规范

### 1. 避免使用 `docker-in-docker` 构建
* 小米 6 运算能力有限，且内部缺失完整的镜像构建环境依赖。
* **规范**：一律在开发机（如 PC/Mac）通过交叉编译或 buildx 构建出目标镜像（`linux/arm64`），再通过 Docker 镜像推送或产物传输部署到手机。

### 2. 避免 ADB Push 目录嵌套陷阱
当需要向容器或宿主机推送文件更新时：
* 若设备端目标目录已存在，`adb push <本地目录> <远程目录>` 会将本地目录作为**子文件夹**推入其中。
* **通用更新防坑流程**：
  1. `adb shell "rm -rf /data/local/tmp/staging_dir"`（先清理暂存区）
  2. `adb push ./dist /data/local/tmp/staging_dir`
  3. `docker cp /data/local/tmp/staging_dir/. <容器名>:<容器内部路径>/`
  4. 检查容器内文件并使用 `-u 0` 修复属主权限。

### 3. 容器修改后的镜像固化规则 (`docker commit`)
* 若通过 `docker cp` 或 `docker exec` 对已运行的容器进行了热补丁修复或文件更新：
  * **必须立即执行**：`docker commit <容器名> <镜像名>:<标签>`。
  * **原因**：若未提交镜像，一旦执行容器重启或重新构建（`docker compose down && up`），未提交的层将彻底丢失。

### 4. 生产容器排障技巧（替代缺失工具）
生产 ARM64 极简容器（如 Alpine、Distroless、Debian Slim）通常不带 `ps`, `netstat`, `curl` 等工具：
* **查进程树**：宿主机直接使用 `adb shell "su -c 'ps -ef | grep <进程名>'"`。
* **查连接数与网络端口**：`docker exec <容器名> cat /proc/net/tcp | wc -l`。
* **查文件与挂载**：`docker exec <容器名> cat /proc/mounts`。

---

## 五、Android 系统级风险规避与 LMK 防御

### 1. 规避 Android LMK (Low Memory Killer)
* Android 系统常驻 `lmkd` 守护进程，专门在 RAM 紧张时根据进程的 `oom_score_adj` 强杀后台进程。
* Docker 容器不属于常规 Android 桌面应用，系统内存紧缺时极易被作为低优先级目标无预警终止。
* **防御措施**：
  * 控制容器总内存开销，单个高负荷容器不要瞬时申请超过 1GB 堆内存。
  * 可通过脚本对关键容器进程调低 OOM 杀进程打分：
    ```bash
    echo -1000 > /proc/<PID>/oom_score_adj
    ```
  * 测速、测活或批量任务中，严格控制并发数（并发度一般建议 $\le 50$）。

### 2. 散热与性能温控限制
* 小米 6 无主动散热，持续多核满载（CPU 8 核全速运转）会导致温控限制（Thermal Throttling）触发，CPU 会被硬降频至最低档甚至断网。
* 批量批处理或高并发作业中，应适当保留协程/线程休眠间隔。

---

## 六、参考手册与工具

* [Android Docker 命令与参数矩阵速查表](./references/docker_command_matrix.md)
* [Android 底层特性与避坑深入分析](./references/android_docker_pitfalls.md)
* [免长指令 Docker 远程包装脚本](./scripts/mi6_docker.sh)
* [通用容器文件热推与镜像固化脚本](./scripts/mi6_hotupdate.sh)
