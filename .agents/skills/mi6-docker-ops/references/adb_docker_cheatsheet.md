# 小米 6 Docker 命令与 ADB 操作速查表 (CheatSheet)

所有命令针对设备端点：`-s 192.168.2.237:5555`，Docker 绝对路径：`/data/data/com.termux/files/usr/bin/docker`。

---

## 1. 容器生命周期管理

### 查看容器列表
```bash
# 查看所有运行与停止的容器
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker ps -a'"
```

### 启动 / 停止 / 重启
```bash
# 重启 pnodes 服务
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart pnodes'"

# 停止容器
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker stop pnodes'"

# 启动容器
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker start pnodes'"
```

### 查看实时日志与输出截断
```bash
# 查看最近 50 行日志
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker logs pnodes --tail 50'"

# 持续滚动跟踪日志 (按 Ctrl+C 退出)
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker logs pnodes -f'"
```

### 资源监控
```bash
# 查看容器内存、CPU 与网络消耗
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker stats --no-stream'"
```

---

## 2. 容器内部指令执行 (`exec`)

### 容器内普通命令 (以 `app` 用户身份)
```bash
# 检查 /app 二进制文件时间戳与大小
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec pnodes ls -la /app'"

# 检查 /data 数据目录内容
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec pnodes ls -la /data'"
```

### 容器内特权命令 (必须加 `-u 0` 以 root 运行)
```bash
# 修复容器 /app 属主为 app 用户
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec -u 0 pnodes chown -R app:app /app'"

# 清除容器内因误操作产生的嵌套子目录
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec -u 0 pnodes rm -rf /app/published_app'"
```

### 网络连接与底层内核探测 (无需 netstat / ps)
```bash
# 查看当前 ESTABLISHED 与活跃连接数量
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec pnodes cat /proc/net/tcp | wc -l'"

# 查看容器挂载详情
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec pnodes cat /proc/mounts'"
```

---

## 3. 文件拷贝与数据同步 (`docker cp`)

### 宿主机文件考入容器
```bash
# 将手机 /data/local/tmp/published_app 下的内容完整覆盖容器 /app/
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker cp /data/local/tmp/published_app/. pnodes:/app/'"
```

### 容器文件提取至宿主
```bash
# 将容器内生成的配置文件提取至手机暂存区
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker cp pnodes:/app/pnodes /data/local/tmp/pnodes_backup'"
```

---

## 4. 镜像提交与固化 (`docker commit`)

```bash
# 将当前 pnodes 容器保存为本地 pnodes:latest 镜像
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker commit pnodes pnodes:latest'"

# 检查当前设备上的镜像列表与更新时间
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker images'"
```

---

## 5. Docker Compose 管理 (在小米 6 上的项目路径)

```bash
# 进入 compose 项目所在目录查看
adb -s 192.168.2.237:5555 shell "su -c 'ls -la /data/docker/compose/pnodes/'"

# 重启 compose 服务组（如包含 minisubconvert 与 pnodes）
adb -s 192.168.2.237:5555 shell "su -c 'cd /data/docker/compose/pnodes && /data/data/com.termux/files/usr/bin/docker compose restart'"
```
