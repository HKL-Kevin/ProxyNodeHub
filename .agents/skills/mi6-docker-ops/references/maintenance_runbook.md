# 小米 6 容器维护与数据持久化规范手册 (Maintenance Runbook)

---

## 一、文件权限与所有权规范

在 Android Linux 底层与 Docker 容器之间存在权限映射机制，不当的操作会导致容器因权限不足抛出 `Permission denied` 或 `attempt to write a readonly database`。

### 1. 用户映射标准

* **容器内用户**：`app`，UID 为 `10001`，GID 为 `10001`。
* **宿主机显示用户**：在 Android 宿主机（`/data/docker/compose/pnodes/data/`）上，该 UID 通常被映射为 `1654:1654`（或对应 Termux/应用专用 UID）。

### 2. 权限维护原则

1. **绝对禁止以宿主 Root 身份向数据卷新建未赋权的文件**：
   若在宿主机通过命令直接 touch 或写入文件，其属主为 `root:root`，容器内部用户 `app` 将无法修改。
2. **写后权限修复**：
   如果在宿主机修改或重置了 `/data/docker/compose/pnodes/data/` 下的文件（如 `nodes.db`），必须立即执行：
   ```bash
   adb -s 192.168.2.237:5555 shell "su -c 'chown -R 1654:1654 /data/docker/compose/pnodes/data/'"
   ```
3. **应用二进制目录权限**：
   容器内部 `/app` 目录必须属于 `app:app`：
   ```bash
   adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec -u 0 pnodes chown -R app:app /app'"
   ```

---

## 二、SQLite 存活池数据库维护规范

持久化数据库路径：
* 容器内部：`/data/nodes.db`
* 宿主机路径：`/data/docker/compose/pnodes/data/nodes.db`

### 1. 数据安全性与 WAL 机制
* 数据库由 `Microsoft.Data.Sqlite` 驱动，使用嵌入式单表结构 (`alive_nodes`)。
* 具有完整的 ACID 事务保证，`raw_yaml` 节点块与元数据为同一条记录原子更新。

### 2. 脱机导出与备份规范
严禁直接通过 ADB pull 正在处于高频写操作的 `/data/docker/.../nodes.db` 文件（可能导致文件锁冲突或拷贝撕裂）。标准脱机导出流程：
```bash
# 1. 复制到手机临时公共目录并开放可读权限
adb -s 192.168.2.237:5555 shell "su -c 'cp /data/docker/compose/pnodes/data/nodes.db /data/local/tmp/nodes.db && chmod 666 /data/local/tmp/nodes.db'"

# 2. 从手机下拉到本地
adb -s 192.168.2.237:5555 pull /data/local/tmp/nodes.db /tmp/nodes.db

# 3. 本地使用 sqlite3 或 python 进行无锁只读分析
python3 -c "
import sqlite3
conn = sqlite3.connect('/tmp/nodes.db')
c = conn.cursor()
c.execute('SELECT COUNT(*), COUNT(DISTINCT country) FROM alive_nodes')
print('总节点数与国家数:', c.fetchone())
"
```

### 3. 清理重置规范
若需要重新全量构建存活池：
```bash
adb -s 192.168.2.237:5555 shell "su -c 'rm -f /data/docker/compose/pnodes/data/nodes.db'"
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart pnodes'"
```

---

## 三、小米 6 资源管理与 Android LMK 规避

### 1. 硬件瓶颈与内核特性
* **CPU**：骁龙 835（4 × 2.45 GHz Kryo 280 + 4 × 1.9 GHz Kryo 280）。
* **内存**：6 GB LPDDR4x。
* **特性**：ARM64 架构，无主动散热；长期高负载容易触发降频或系统 Low Memory Killer（杀进程机制）。

### 2. 容器参数安全水位线

| 指标 | 安全阈值 | 危险阈值 | 风险说明 |
| :--- | :--- | :--- | :--- |
| **测活并发数 (concurrent)** | **50** | $> 100$ | 超过 100 会导致套接字耗尽与网络栈丢包 |
| **测速并发数 (speed-concurrent)** | **10 - 20** | $> 30$ | 带宽抢占导致测速不准，且峰值内存激增 |
| **单节点测速大小 (download-mb)** | **3 - 5 MB** | $> 20$ MB | 大流量持续下载容易使手机发热降频 |
| **测活探针超时 (timeout)** | **2000 ms** | $> 5000$ ms | 缩短超时可让坏节点快速释放连接池 |

---

## 四、故障应急与恢复流程 (Disaster Recovery)

### 故障 1：ADB 提示 Device Offline 或无法连接
```bash
# 1. 重启本地 adb 服务
adb kill-server
adb start-server

# 2. 重新连接局域网 ADB
adb connect 192.168.2.237:5555

# 3. 检查设备状态
adb devices
```

### 故障 2：Docker 服务异常未响应或端口拒绝连接
```bash
# 检查 dockerd 进程是否存在
adb -s 192.168.2.237:5555 shell "su -c 'ps -A | grep dockerd'"

# 若 dockerd 僵死，进入 Termux 重启 docker 服务
adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart pnodes'"
```

### 故障 3：Web 界面正常，但测速/测活无法出结果
1. 检查日志：
   ```bash
   adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker logs pnodes --tail 50'"
   ```
2. 检查内核是否成功拉起：
   ```bash
   adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker exec pnodes cat /proc/net/tcp | wc -l'"
   ```
3. 检查是否有残留僵死内核进程：
   ```bash
   adb -s 192.168.2.237:5555 shell "su -c '/data/data/com.termux/files/usr/bin/docker restart pnodes'"
   ```
