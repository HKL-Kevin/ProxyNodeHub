# 小米 6 Docker 通用命令与参数兼容性矩阵 (Command Matrix)

所有命令通过 ADB 远程提权执行。端点预设为 `192.168.2.237:5555`，Docker CLI 路径为 `/data/data/com.termux/files/usr/bin/docker`。

---

## 一、通用 Docker 参数在小米 6 上的兼容性矩阵

| 参数 | 兼容性评级 | 说明与处理建议 |
| :--- | :---: | :--- |
| **`--platform linux/arm64`** | **必须具备** | 小米 6（骁龙 835）仅支持 64 位 ARM 指令集，严禁运行 x86/amd64 镜像。 |
| **`-v /绝对路径:/容器路径`** | **强力推荐** | 宿主挂载源必须为位于 `/data/` 上的真实 ext4 分区绝对路径。严禁使用 `./相对路径` 或 `/sdcard/`。 |
| **`--net=host`** | **强力推荐** | 避开 Android `netd` 对 iptables 的重置冲刷，网络性能最高，零端口转发损耗。 |
| **`-p 0.0.0.0:宿主:容器`** | **标准推荐** | 必须显式声明绑定 IP `0.0.0.0`，防止绑定局限于 127.0.0.1 导致局域网不可达。 |
| **`--privileged`** | **按需推荐** | 当容器需要访问原始套接字、底层系统调用或特定硬件时使用，规避 Android SELinux 上下文限制。 |
| **`--restart unless-stopped`** | **标准推荐** | 保持容器在 dockerd 重启或手机开机后自启。 |
| **`-u 0` (`docker exec`)** | **关键技巧** | 生产容器若运行在非 root 用户下，需要以特权排障、修复权限时必传 `-u 0`。 |
| **`--memory <limit>`** | **谨慎使用** | 若内核未编译 `CONFIG_MEMCG_SWAP`，会导致 Docker daemon 报警告或无法生效。 |
| **`--cpus <limit>`** | **基本支持** | 可控制 CFS 配额，但在大小核高通架构上，极端限制易导致线程调度卡死。 |
| **`--pids-limit`** | **尽量避免** | 很多旧版 Android 4.4 内核缺失 pids cgroup 子系统，传入可能引发容器创建报错。 |
| **`--cgroup-parent`** | **严禁自定义** | Android 系统的 cgroup 树由 Android init 管理，自定义父节点容易导致无法挂载 cgroup 报错退出。 |

---

## 二、通用 Docker 运维与排障命令速查

### 1. 容器状态与生命周期
```bash
# 查看所有容器列表 (含已退出的容器)
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker ps -a'"

# 查看容器详细 JSON 配置 (如检查挂载点、环境变量、网络绑定)
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker inspect <容器名>'"

# 查看当前运行资源统计 (CPU、内存、网络 IO)
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker stats --no-stream'"

# 启停与重启
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart <容器名>'"
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker stop <容器名>'"
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker start <容器名>'"
```

### 2. 日志查看与追踪
```bash
# 查看最后 100 行日志
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker logs <容器名> --tail 100'"

# 查看包含时间戳的日志
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker logs <容器名> -t --tail 50'"
```

### 3. 容器内交互与诊断 (`exec`)
```bash
# 进入容器交互终端 (如果容器内包含 sh)
adb -s 192.168.2.237:5555 shell -t "su -c '/data/data/com.termux/files/usr/bin/docker exec -it <容器名> sh'"

# 以 Root 用户特权进入容器
adb -s 192.168.2.237:5555 shell -t "su -c '/data/data/com.termux/files/usr/bin/docker exec -it -u 0 <容器名> sh'"

# 极简容器缺失工具时的内核状态探测:
# 1) 查看当前活跃 TCP 套接字数量
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec <容器名> cat /proc/net/tcp | wc -l'"
# 2) 查看当前容器挂载表
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec <容器名> cat /proc/mounts'"
```

### 4. 文件拷贝与数据提取 (`cp`)
```bash
# 将宿主机准备好的文件/目录拷入容器
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker cp <宿主绝对路径>/. <容器名>:<容器内部路径>/'"

# 将容器内部文件提取到宿主机
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker cp <容器名>:<容器内部路径> <宿主绝对路径>'"
```

### 5. 镜像构建产物固化 (`commit`)
```bash
# 对运行中做过文件更新或配置热修复的容器生成/覆盖新镜像
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker commit <容器名> <镜像名>:<标签>'"
```

### 6. 镜像与悬空层清理 (`prune`)
```bash
# 手机存储空间极其珍贵，清理未使用的悬空镜像 (dangling images)
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker image prune -f'"

# 查看当前磁盘空间占用分布
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker system df'"
```
