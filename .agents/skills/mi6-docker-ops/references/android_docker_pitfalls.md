# Android (小米 6) 特性与 Docker 深度坑点分析

本文件详细剖析 Android 底层（特别是基于 Linux 4.4 内核的高通骁龙 835 设备）与通用 Linux 服务器在运行 Docker 容器时的核心底层差异与防坑指南。

---

## 1. 文件系统陷阱：ext4/f2fs 与 sdcardfs/FUSE 的本质冲突

在常规 Linux（如 Ubuntu/Debian）上，除特殊挂载点外，整个根目录及用户目录均具备完整的 POSIX 语义支持。但在 Android 系统上，存储被严格划分为不同性质的区域：

### (1) 模拟存储 (`/sdcard` / `/storage/emulated/0`)
* **底层实现**：由 Android 内核的 `sdcardfs` 或用户态 `FUSE` 守护进程模拟挂载。
* **致命缺陷**：
  * **所有权被锁定**：所有文件由 Android 系统的虚拟 UID/GID 接管，执行 `chown` 或 `chmod` 永远返回成功但实际**不生效**。
  * **不支持 UNIX Socket**：无法在这些路径下创建 `.sock` 文件（如 Docker、Redis、MySQL 所需的套接字）。
  * **不支持硬链接 (Hardlinks)**：Git 仓库或构建缓存操作会直接报错 `Operation not permitted`。
  * **缺少标准文件锁与 mmap 异常**：数据库（SQLite WAL 模式、RocksDB、LevelDB、MySQL）在此路径下会出现严重的数据损坏或启动失败。
* **规则**：**绝不可将容器的任何工作目录、数据库目录挂载到 `/sdcard` 或其子目录下！**

### (2) 真正的 Linux 原生分区 (`/data/` 根目录下的自建目录)
* **路径示例**：`/data/docker/...`，`/data/data/com.termux/files/...`。
* **特点**：直接基于设备的 `ext4` 或 `f2fs` 文件系统，具备完整 POSIX 特性，支持软硬链接、所有权隔离、文件锁与可执行标志。
* **规则**：**所有 Docker Volume、数据卷持久化目录一律规划在 `/data/` 或 Termux 私有目录下。**

---

## 2. 权限与身份隔离：Android AID (Android IDs)

* **常规 Linux**：UID 从 0 到 65535，用户组为 `/etc/passwd` 和 `/etc/group` 定义。
* **Android 机制**：
  * 拥有固定的内核级 AID 常量（例如：`AID_ROOT=0`, `AID_SYSTEM=1000`, `AID_SHELL=2000`, `AID_APP_START=10000`）。
  * 容器内的 UID（例如常见非 root 镜像的 UID 1000、1001 或 10001）在宿主机看来会被识别为某个特定的 Android 系统用户或独立 App。
* **运维风险**：
  * 宿主机使用 Root 创建文件，其权限往往是 `0:0 644`。容器内的普通用户无法追加写入，往往抛出 `EACCES: permission denied`。
* **规范应对**：
  * 挂载宿主机目录前，明确容器内运行用户的 UID。
  * 在宿主机提前使用 `chown -R <目标UID>:<目标GID> <宿主目录>`，或者对共享读写目录赋予宽容权限（`chmod -R 775` / `777`）。

---

## 3. 网络架构与 Android `netd` 的博弈

### (1) Docker 默认桥接网桥 (docker0) 的脆弱性
* 在标准 Linux 上，Docker 通过 `iptables` 配置 `PREROUTING`、`POSTROUTING` 及 `FORWARD` 链实现端口映射与容器外网访问。
* **Android 的特殊机制**：
  * Android 拥有专门的网络守护进程 `netd`，负责管理蜂窝移动网络、Wi-Fi、热点及网络共享（tethering）。
  * `netd` 会在网络状态变化（例如 Wi-Fi 断开重连、热点开启、反向网络变动）时，**主动刷新或重置 iptables 规则链**，常常抹掉 Docker 写入的转发规则，导致容器突然无法访问外网或宿主端口映射失联。
* **优化策略**：
  * **优先采用 Host 网络 (`--net=host`)**：对于独立运行的服务（如本地网关、控制面板、代理服务），优先使用 `--net=host`。它直接借用 Android 宿主网络命名空间，彻底规避 iptables 被 `netd` 冲刷的问题，且几乎无额外 NAT 转发性能损耗。
  * **显式监听 `0.0.0.0`**：如果必须使用 Bridge 端口映射，`-p` 参数必须明确指定 `0.0.0.0:宿主端口:容器端口`，防止绑定到 `127.0.0.1` 导致外部局域网不可达。

### (2) DNS 解析机制差异
* Android 自身不使用 `/etc/resolv.conf` 进行 DNS 查询，而是通过 `netd` 的 IPC 套接字使用 `getaddrinfo`。
* 容器内部如果使用默认系统配置，可能会向空或不存在的本地 DNS 发起查询导致解析超时。
* **建议**：在 `daemon.json` 或 `docker run` 时显式指定可靠的 DNS 服务器（例如 `--dns 223.5.5.5 --dns 119.29.29.29`）。

---

## 4. 内核与 cgroups 子系统限制

* 骁龙 835（高通 MSM8998）的官方 Android 内核多为 Linux 4.4 分支。
* 内核在编译时为节省开销与功耗，往往未开启完整的容器 cgroup 功能：
  * 常见缺失或受限模块：`cgroup_enable=memory`, `swapaccount=1`, `cpuset`, `pids`。
* **兼容性表现**：
  * 启动容器时附带严格的资源隔离参数（如 `--pids-limit`, `--memory-swap`）可能会引发内核警告或直接拒绝容器创建。
  * 生产实践中，应尽量保持容器资源限制参数的简洁，避免使用高级调度器参数。

---

## 5. Android LMK (Low Memory Killer) 机制与防御

* **机制**：Android 在内存不足时，会依据 `/proc/<PID>/oom_score_adj`（取值范围 -1000 到 1000）从高到低强杀后台进程。
* **Docker 的尴尬地位**：
  * Docker 守护进程和容器进程在 Android 看来属于“纯原生后台进程（Native Daemon）”，且未绑定 Android Activity 或 Foreground Service。
  * 当系统前台应用或系统服务内存吃紧时，Docker 进程容易被 LMK 作为替罪羊强制杀掉。
* **防护规范**：
  * **进程保护**：对于核心服务容器的主进程，可以通过 Root 权限将其 OOM 打分调整为最高优先级：
    ```bash
    echo -1000 > /proc/<dockerd_pid>/oom_score_adj
    ```
  * **内存水位线控制**：避免单容器启动高并发内存突增任务，容器常驻内存开销建议控制在单机总空闲内存的 50% 以内。
