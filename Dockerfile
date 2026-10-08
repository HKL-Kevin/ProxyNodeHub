# ══════════════════════════════════════════════════════════════════
# ProxyNodeHub 容器镜像
#
# 支持 linux/amd64 与 linux/arm64：
#   docker buildx build --platform linux/amd64,linux/arm64 -t pnodes .
#
# 镜像内预装 subs-check 测速内核（按 TARGETARCH 自动选对应架构的资产），
# 因此容器开箱即可搜索、聚合与测速，无需运行时下载 55MB 内核。
#
# 桌面版（WinForms）不在此镜像内 —— 它跑在 Windows 上，与本镜像共享
# 同一份 ProxyNodeHub.Core 逻辑。
# ══════════════════════════════════════════════════════════════════

# ───────────────────────── 构建阶段 ─────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src

# Docker 的 TARGETARCH 是 amd64/arm64，.NET 的 RID 是 linux-x64/linux-arm64，
# 两者命名不同，必须显式映射。
RUN case "$TARGETARCH" in \
        amd64) echo "linux-x64" > /tmp/rid ;; \
        arm64) echo "linux-arm64" > /tmp/rid ;; \
        *) echo "不支持的架构: $TARGETARCH"; exit 1 ;; \
    esac && cat /tmp/rid

# 先只拷贝 csproj 并 restore：源码变动时这一层可复用缓存，
# 不必每次重新拉 NuGet。
COPY src/ProxyNodeHub.Core/ProxyNodeHub.Core.csproj src/ProxyNodeHub.Core/
COPY src/ProxyNodeHub.Cli/ProxyNodeHub.Cli.csproj src/ProxyNodeHub.Cli/
RUN dotnet restore src/ProxyNodeHub.Cli/ProxyNodeHub.Cli.csproj -r "$(cat /tmp/rid)"

# 再拷贝源码并发布。显式指定 linux RID —— 构建阶段跑在 BUILDPLATFORM
# （可能是 macOS/arm64 主机），不指定会产出宿主平台的二进制。
#
# logo.png 单独拷贝：Core 把它作为内嵌资源（Web 界面的图标），
# 而它在 src/ 根目录、不在 Core 目录里，只拷两个项目目录会漏掉它。
COPY src/logo.png src/logo.png
COPY src/ProxyNodeHub.Core/ src/ProxyNodeHub.Core/
COPY src/ProxyNodeHub.Cli/ src/ProxyNodeHub.Cli/
RUN dotnet publish src/ProxyNodeHub.Cli/ProxyNodeHub.Cli.csproj \
        -c Release \
        -r "$(cat /tmp/rid)" \
        --self-contained false \
        --no-restore \
        -o /app

# ───────────────────────── 运行阶段 ─────────────────────────
# 使用 Debian 官方 aspnet:8.0 运行时（原生完整 glibc 环境）。
# 原因：subs-check 内核内置的 sub-store 依赖动态拉取的预编译 Node.js，
# 该二进制强依赖 glibc (/lib/ld-linux-aarch64.so.1)。
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl 用于容器健康检查与调试
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

ARG TARGETARCH
ARG SUBS_CHECK_VERSION=v1.6.5

# 将用户创建、内核解压、软链建立与权限分配整合到单层 RUN，彻底消除写时复制冗余。
RUN set -eux; \
    if ! id -u app > /dev/null 2>&1; then \
        if getent passwd 10001 > /dev/null 2>&1; then \
            useradd -m -s /usr/sbin/nologin app; \
        else \
            useradd -m -u 10001 -s /usr/sbin/nologin app; \
        fi; \
    fi; \
    case "$TARGETARCH" in \
        amd64) KERNEL_ASSET="subs-check_Linux_x86_64.tar.gz" ;; \
        arm64) KERNEL_ASSET="subs-check_Linux_aarch64.tar.gz" ;; \
        *) echo "不支持的架构: $TARGETARCH (仅支持 amd64 / arm64)"; exit 1 ;; \
    esac; \
    mkdir -p /opt/subs-check /data/output /data/subcheck/config /data/subcheck/history /data/subcheck/output; \
    URL="https://github.com/beck-8/subs-check/releases/download/${SUBS_CHECK_VERSION}/${KERNEL_ASSET}"; \
    (curl -fsSL --retry 3 --retry-delay 2 -o /tmp/kernel.tar.gz "https://gh-proxy.com/${URL}" \
     || curl -fsSL --retry 3 --retry-delay 2 -o /tmp/kernel.tar.gz "$URL"); \
    tar -xzf /tmp/kernel.tar.gz -C /opt/subs-check; \
    rm -f /tmp/kernel.tar.gz; \
    chmod +x /opt/subs-check/subs-check; \
    /opt/subs-check/subs-check --version 2>&1 | head -1 || true; \
    echo "${SUBS_CHECK_VERSION}" > /opt/subs-check/kernel.version; \
    for d in config history output; do \
        rm -rf "/opt/subs-check/$d"; \
        ln -s "/data/subcheck/$d" "/opt/subs-check/$d"; \
    done; \
    chown -R app:app /data /opt/subs-check

WORKDIR /app
COPY --from=build --chown=app:app /app/ ./

ENV PROXYNODEHUB_DATA_DIR=/data \
    PROXYNODEHUB_CONFIG_DIR=/data \
    PROXYNODEHUB_OUTPUT_DIR=/data/output \
    PROXYNODEHUB_KERNEL_PATH=/opt/subs-check/subs-check \
    PROXYNODEHUB_SUBCHECK_DIR=/opt/subs-check \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_ENVIRONMENT=Production

USER app

# 默认启动 Web 管理界面并常驻。批处理用法覆盖命令即可：
#   docker run --rm pnodes search -n 30
#   docker run --rm pnodes merge https://example.com/sub
#   docker run --rm pnodes speedtest https://example.com/sub
# PublishSingleFile 的产物是可执行文件 pnodes（不是 pnodes.dll）。
ENTRYPOINT ["/app/pnodes"]
CMD ["web", "--port", "8080"]

# 健康检查打 Web 的状态接口；批处理模式下这个检查会失败，
# 但批处理容器本来就跑完即退，无妨。
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/state > /dev/null || exit 1
