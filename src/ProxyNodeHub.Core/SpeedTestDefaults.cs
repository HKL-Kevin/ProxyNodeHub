using System;
using System.IO;
using System.Text;

namespace ProxyNodeHub;

/// <summary>
/// 首次运行时生成一份可用的 config.yaml。
///
/// 生成的内容就是内核官方 config.example.yaml 原文（键、注释、默认值与内核完全一致），
/// 只把两个订阅来源键清空 —— 来源由「订阅来源」弹窗决定，测速前写入 session.yaml。
/// 这样内核升级新增配置项时，用户手里的就是同一份结构，合并新版示例不会错位。
///
/// 文件生成后完全归用户所有：改了、删了都不再被覆盖。
/// </summary>
public static class SpeedTestDefaults
{
    /// <summary>
    /// 内核官方配置原文。故意不裁剪、不改写默认值 ——
    /// 一旦我们自己压默认值，用户拿这份文件和内核文档就对不上，
    /// 升级时也无法直接套用官方示例。
    ///
    /// 测速是真实流量消耗：官方默认 20MB/节点 × 50 并发。消耗交给用户在
    /// download-mb / concurrent 上自行控制，表单页对每一项都有说明。
    /// </summary>
    public const string DefaultYaml = """
# ProxyNodeHub · 节点测速配置
#
# 本文 = subs-check 内核官方 config.example.yaml 原文：键名、注释、默认值
# 全部与内核保持一致。内核升级新增了配置项时，只需从新版示例把对应键块
# 合并进来，文件结构不会错位。
#
# 唯一的两处适配：
#   · sub-urls / sub-urls-remote 清空 —— 订阅来源由「订阅来源」弹窗决定，
#     每次测速前由程序生成 session.yaml 覆盖 sub-urls
#   · 本文件完全归你所有：改任何键、删任何注释都不会被覆盖
#
# 「测速参数」弹窗里的表单只是常用键的快捷方式，改完同样写回本文件。

# 是否显示进度
print-progress: true

# 并发线程数（测活阶段使用此值）
concurrent: 50
# 流媒体检测并发数（默认等于concurrent）
media-concurrent: 20
# 测速并发数（默认等于concurrent）
# 建议设置较低值，避免并发抢带宽导致测速不准
speed-concurrent: 20
# 是否打乱节点的测试顺序（仅影响测试先后，输出顺序保持订阅原序不变）
# 开启后可避免同机场相邻节点、同节点的多个协议被扎堆在同一时间窗口测试，
# 从而降低相邻IP一起出问题、互相干扰的概率
shuffle-test-order: true
# 检查间隔(分钟)
# 必须大于0，小于等于0会设置间隔1分钟
check-interval: 120
# cron表达式，如果配置了此项，将忽略check-interval
# 支持标准cron表达式，如：
# "0 */2 * * *" 表示每2小时的整点执行
# "0 0 */2 * *" 表示每2天的0点执行
# "0 0 1 * *" 表示每月1日0点执行
# "*/30 * * * *" 表示每30分钟执行一次
# cron-expression: "*/30 * * * *"

# 保存几个成功的节点，为0代表不限制 
# 如果你的并发数量超过这个参数，那么成功的结果可能会大于这个数值
# success-limit <= success <= success-limit+concurrent
success-limit: 0

# 超时时间(毫秒)(节点的最大延迟)
timeout: 5000
# 延迟测试URL
# 一些高级用法需要自己摸索，比如想要拒绝某些CF类节点，可将url替换为https://www.cloudflare.com
alive-test-url: http://gstatic.com/generate_204
# 测速地址(注意 并发数*节点速度<最大网速 否则测速结果不准确)
# 尽量不要使用Speedtest，Cloudflare提供的下载链接，因为很多节点屏蔽测速网站
# 如果找不到稳定的测速地址，可以自建测速地址
speed-test-url: https://github.com/AaronFeng753/Waifu2x-Extension-GUI/releases/download/v2.21.12/Waifu2x-Extension-GUI-v2.21.12-Portable.7z
# 最低测速结果舍弃(KB/s)
min-speed: 512
# 下载测试时间(s)(与下载链接大小相关，默认最大测试10s)
download-timeout: 10
# 单节点测速下载数据大小(MB)限制，0为不限
download-mb: 20
# 总下载速度速度限制(MB/s)，0为不限
total-speed-limit: 0

# 监听端口，用于直接返回节点信息，方便订阅转换
# http://127.0.0.1:8199/all.yaml
# 注意：为方便小白默认监听0.0.0.0:8199，请自行修改
# 更新需重启程序
listen-port: ":8199"

# 以节点IP查询位置重命名节点
# 质量差的节点可能造成IP查询失败，造成整体检查速度稍微变慢，默认true
rename-node: true
# 节点前缀，依赖rename-node为true才生效
node-prefix: ""

# 只测试指定协议的节点
node-type:
  # - ss
  # - vmess
  # - vless

# 节点名称过滤，只有匹配任一正则表达式的节点才会被保留
# 如果为空则不过滤，所有通过测试的节点都会保留
# 注意：过滤在流媒体检测+重命名之后、测速之前进行。此时节点名称已包含
#       国家和媒体标签（如 NF-US、GPT⁺、IP 风险百分比），但**不包含速度标签**。
#       速度标签在测速通过后追加，不参与过滤。
filter:
  # === 按地区过滤 ===
  # - "香港|HK|Hong Kong"        # 保留香港节点
  # - "新加坡|SG|Singapore"      # 保留新加坡节点
  # - "台湾|TW|Taiwan"           # 保留台湾节点
  #
  # === 按 IP 风险过滤（需开启 media-check 和 iprisk）===
  # - "\\|[0-4]?[0-9]%"          # 保留 IP 风险 < 50% 的节点
  # - "\\|[0-9]%\\|"             # 保留 IP 风险 < 10% 的节点（个位数）
  #
  # === 按平台解锁过滤（需开启 media-check）===
  # - "GPT⁺|GPT"                 # 保留解锁 OpenAI 的节点
  # - "NF"                       # 保留解锁 Netflix 的节点（NF=仅自制剧, NF-US=全解锁）
  # - "GM"                       # 保留解锁 Gemini 的节点
  # - "D\\+"                     # 保留解锁 Disney+ 的节点（D+=解锁但未知地区, D+-US=解锁并知道地区）
  # - "YT-"                      # 保留解锁 YouTube Premium 的节点
  # - "CL-"                      # 保留解锁 Claude 的节点
  # - "SP-"                      # 保留解锁 Spotify 的节点

# 是否开启流媒体检测，其中IP欺诈依赖重命名
# 开启后每个存活节点会跑流媒体解锁检测 + IP 欺诈分数 + IP 查询，
# 节点结果的 ip / ipRisk / media / country 字段才会被填充。
# 关闭则这些字段全空（ProxyNodeHub 的 ipinfo.io 补查也拿不到 IP）。
media-check: true
# 流媒体检测超时时间(秒)，未设置则默认为10秒
media-check-timeout: 5
platforms:
  - iprisk
  - tiktok
  - youtube
  - netflix
  - disney
  - openai
  - gemini
  - claude
  - spotify

# 保留最近N天内可用的历史节点（天）
# 开启后每次检测会保存节点快照到 output/history/ 目录
# 下次检测时会将历史节点加入待测队列重新检测，通过的节点才会保留到输出中
# 过期的历史文件会自动清理
# 0 = 关闭此功能
keep-days: 0

# 输出目录
# 如果为空，则为程序所在目录的config目录
output-dir: ""

# 是否启用Web控制面板
# 如果为false，则不启动Web控制界面，仅启动订阅服务相关接口
# 访问地址：http://127.0.0.1:8199/admin
enable-web-ui: true
# 填写Web控制面板的api-key，如果为空，则自动生成
# 配置文件为空时，支持使用环境变量设置 API_KEY
api-key: ""

# 检测完成后执行的回调脚本路径
# 脚本将在检测完成后执行，可用于自定义通知或其他操作
# 例如: "/path/to/your/script.sh" 或 'C:\path\to\your\script.bat'
# Linux请在脚本开头添加对应的：#!/bin/bash、#!/bin/sh、#!/usr/bin/env bash 等，编写标准的脚本
# 注意如果使用docker，目前docker使用的alpine，只有sh，不支持bash
callback-script: ""

# 填写搭建的apprise API server 地址
# https://notify.xxxx.us.kg/notify
apprise-api-server: ""
# 填写通知目标
# 支持100+ 个通知渠道，详细格式请参照 https://github.com/caronc/apprise
recipient-url: 
  # telegram格式：tgram://{bot_token}/{chat_id}
  # - tgram://xxxxxx/-1002149239223
  # 钉钉格式：dingtalk://{Secret}@{ApiKey}
  # - dingtalk://xxxxxx@xxxxxxx
# 自定义通知标题
notify-title: "🔔 节点状态更新"

# sub-store的启动端口，为空则不启动sub-store
# 更新需重启程序，不可监听局域网IP，只有三种写法 :8299, 127.0.0.1:8299, 0.0.0.0:8299
# sub-store-port: ":8299"
sub-store-port: ":8299"
# sub-store自定义访问路径，必须以/开头，后续访问订阅也要带上此路径
# 设置path之后，还可以开启订阅分享功能，无需暴露真实的path
# sub-store-path: "/path"
sub-store-path: ""
# 以下配置去查看sub-store相关的教程：https://hub.docker.com/r/xream/sub-store
# sub-store同步gist定时任务
# 定时任务指定时将订阅/文件上传到私有 Gist. 在前端, 叫做 同步 或 同步配置.
# 55 23 * * * 每天 23 点 55 分(避开部分机场后端每天0点定时重启)
sub-store-sync-cron: ""
# 定时更新订阅
# SUB_STORE_PRODUCE_CRON 在后台定时处理订阅. 格式为 0 */2 * * *,sub,a;0 */3 * * *,col,b
# 即每 2 小时处理一次单条订阅 a, 每 3 小时处理一次组合订阅 b.
sub-store-produce-cron: ""
# sub-store推送服务地址
# 例如：Brak: "SUB_STORE_PUSH_SERVICE=https://api.day.app/XXXXXXXXXXXX/[推送标题]/[推送内容]"
# 注意：仅需修改 XXXXXXXXXXXX 为自己的 token，[推送标题]/[推送内容] 会被sub-store自动替换为对应的内容
sub-store-push-service: ""


# 覆写订阅的url，这个的作用是生成带指定规则的mihomo/clash.meta订阅链接
# 防止网络不好，所以现在内置，依赖:8199端口
# 如果你想替换其他的自定义覆写文件，自己命名后放在output目录，然后更改此URL后缀即可
mihomo-overwrite-url: "http://127.0.0.1:8199/sub/ACL4SSR_Online_Full.yaml"

# 保存方法
# 目前支持的保存方法: r2, local, gist, webdav, s3
save-method: local

# webdav
webdav-url: "https://example.com/dav/"
webdav-username: "admin"
webdav-password: "admin"

# gist id
github-gist-id: ""
# github token
github-token: ""
# github api mirror
github-api-mirror: ""

# 将测速结果推送到Worker的地址
worker-url: https://example.worker.dev
# Worker令牌
worker-token: 1234567890

# 将测速结果推送到S3/Minio的地址
s3-endpoint: "127.0.0.1:9000"
# S3的访问凭证
s3-access-id: "ak"
s3-secret-key: "sk"
# S3的Bucket名称
s3-bucket: "public"
# 是否使用SSL
s3-use-ssl: false
# 默认自动判断dns还是path，但一些云厂商不遵循规范，所以有时需要手动设置
# 可选值：auto, path, dns
s3-bucket-lookup: "auto"

# 重试次数(获取订阅失败后重试次数)
sub-urls-retry: 3
# 订阅下载并发数(控制同时拉取多少个订阅链接)
sub-urls-concurrent: 20
# 获取订阅时使用的UA；如果设置random将会使用随机UA获取订阅
# sub-urls-get-ua: "random"
sub-urls-get-ua: "clash.meta (https://github.com/beck-8/subs-check)"
# Github Proxy，获取订阅使用，结尾要带的 /
# github-proxy: "https://ghfast.top/"
github-proxy: ""
# 此参数更通用，适用于拉取代理、消息推送、文件上传等等
# 写法跟环境变量一样。修改需重启生效
# proxy: "http://username:password@192.168.1.1:7890"
# proxy: "socks5://username:password@192.168.1.1:7890"
proxy: ""
# 符合条件节点数量的占比，低于此值会将订阅链接打印出来，用于排查质量差的订阅
success-rate: 0

# 是否允许 IPv6/AAAA 解析和连接
ipv6: true

# DNS 配置，支持 udp/tcp/tls/https/quic 协议
# enable=false 使用系统 DNS；enable=true 使用以下自定义 DNS
# enable=true 三字段可留空，按 proxy-server-nameserver→nameserver→default-nameserver 链回落
# default-nameserver 用作 bootstrap 必须是纯 IP
dns:
  enable: false
  proxy-server-nameserver:
    - https://dns.alidns.com/dns-query
  nameserver:
    - https://dns.alidns.com/dns-query
  default-nameserver:
    - 223.5.5.5
    - 119.29.29.29

# 远程订阅清单地址；用于集中维护多个订阅链接，避免频繁修改本地文件
# 支持两种格式：
# 1) 纯文本：按行分隔，支持 # 注释与空行
# 2) YAML/JSON：字符串数组 ["https://...", "https://..."]
# 支持时间占位符与 github-proxy，例如包含 {Ymd}、{Y-m-d}
sub-urls-remote:
  # - https://example.com/sub-list.txt
  # - https://example.com/sub-list.yaml

# 订阅地址 支持 clash/mihomo/v2ray/base64 格式的订阅链接
# 如果用户想明确使用clash类型，那可以在支持的订阅链接结尾加上 &flag=clash.meta
# github 链接可自己添加ghproxy使用；订阅链接支持 HTTP_PROXY HTTPS_PROXY 环境变量加速拉取
# 如果用户想区分节点来源，可在订阅链接结尾加上 #备注 ，备注字段会自动加到节点命名结尾
sub-urls:
  # - https://example.com/sub.txt
  # - https://example.com/sub2.txt
  # - https://example.com/sub?token=43fa8f0dc9bb00dcfec2afb21b14378a
  # - https://example.com/sub?token=43fa8f0dc9bb00dcfec2afb21b14378a?flag=clash.meta
    # 所有时间占位符均支持 ±N 天偏移，例如 {Ymd-1} 表示昨天、{Y-m-d+1} 表示明天
  # - https://raw.githubusercontent.com/example/repo/main/config/{Ymd}.yaml
  # - https://raw.githubusercontent.com/example/repo/main/daily/daily-{Y}-{m}-{d}.yaml
  # - https://raw.githubusercontent.com/example/repo/main/config/{Ymd-1}.yaml
  # - https://raw.githubusercontent.com/example/repo/main/daily/daily-{Y-m-d+1}.yaml
  # - https://example.com/sub.txt#我是备注我是备注
  # 打开这个就可以把上次可用的节点再次测一次
  # - "http://127.0.0.1:8199/sub/all.yaml"
""";

    /// <summary>
    /// 早期版本自己生成的基线（concurrent: 20 / download-mb: 3 那份裁剪版）。
    /// 只用来做一次性迁移，见 <see cref="Ensure"/>。
    /// </summary>
    private const string LegacyBaseline = """
# ProxyNodeHub · 节点测速配置
#
# 这个文件完全归你所有。可以直接手改任何键，注释和格式都会保留。
# 页面上的参数控件只是快捷方式 —— 改动同样写回这里。
#
# 内核升级新增的配置项不会自动出现在此文件；
# 需要时从内核新版示例合并，或直接手写（未知键不影响运行）。

# ── 并发与节奏 ──
concurrent: 20
media-concurrent: 20
speed-concurrent: 8
shuffle-test-order: true
check-interval: 120

# ── 阈值与流量 ──
# 测速是真实下载。单节点流量 3MB、总速不限制，一轮 50 节点约 150MB。
# 想更快就调高 concurrent，想省流量就调低 download-mb。
timeout: 5000
min-speed: 512
download-timeout: 8
download-mb: 3
total-speed-limit: 0
success-limit: 50

# ── 测试地址 ──
# 想过滤掉 CF 类节点，把 alive-test-url 换成 https://www.cloudflare.com
alive-test-url: http://gstatic.com/generate_204
speed-test-url: https://github.com/AaronFeng753/Waifu2x-Extension-GUI/releases/download/v2.21.12/Waifu2x-Extension-GUI-v2.21.12-Portable.7z

# ── 节点命名与筛选 ──
rename-node: true
node-prefix: ""
# 只测这些协议；不需要的删掉可省流量
node-type:
  - ss
  - vmess
  - vless
  - trojan
  - hysteria2

# ── 流媒体检测 ──
# 默认关闭：耗时，且部分检测在境内网络下结果不可信。按需打开。
media-check: false
media-check-timeout: 5
platforms:
  - iprisk
  - netflix
  - openai

# ── 历史节点保留 ──
# keep-days 由内核使用（按天）；按「回数」保留由 ProxyNodeHub 自己实现，
# 回数在测速页第一行调整。
keep-days: 7

# ── 输出与保存 ──
save-method: local
output-dir: output/
# 换成 gist / r2 / webdav / s3 / worker 后，
# 对应的凭据字段见内核官方示例 config.example.yaml
success-rate: 0

# ── 订阅拉取 ──
sub-urls-retry: 3
sub-urls-concurrent: 20
# 国内拉 raw.githubusercontent.com 建议填一个加速镜像
github-proxy: ""

# ── 其他 ──
ipv6: true
print-progress: true

""";

    /// <summary>
    /// 确保有 config.yaml：没有就写默认值；已存在则一律不碰。
    ///
    /// 唯一例外是一次性迁移 —— 只有文件与我们早期那份裁剪版基线逐字节相同时
    /// 才替换成官方原文。逐字节比对是关键：用户只要改过一个字（哪怕只是删了
    /// 一行注释），指纹就对不上，文件原样保留。改过的配置永远不会被悄悄覆盖。
    /// </summary>
    public static void Ensure()
    {
        try
        {
            SpeedTestConfig.EnsureDirs();
            if (!File.Exists(SpeedTestConfig.ConfigPath))
            {
                File.WriteAllText(SpeedTestConfig.ConfigPath, DefaultYaml, Encoding.UTF8);
                return;
            }

            var text = File.ReadAllText(SpeedTestConfig.ConfigPath);
            if (Fingerprint(text) == Fingerprint(LegacyBaseline))
            {
                File.WriteAllText(SpeedTestConfig.ConfigPath, DefaultYaml, Encoding.UTF8);
            }
        }
        catch { }
    }

    /// <summary>
    /// 归一化后比对：抹掉 CR、BOM 与首尾空白，只留内容。
    /// BOM 必须显式去掉 —— File.WriteAllText 用 Encoding.UTF8 会带上 BOM，
    /// 而 string.Trim() 不把 U+FEFF 当空白，不处理就永远比对不上。
    /// </summary>
    private static string Fingerprint(string s)
        => s.Replace("\u000D", "").Replace("﻿", "").Trim();

    /// <summary>
    /// 首次运行时落一份官方示例到 config.example.yaml，
    /// 内核升级后可据此比对用户缺了哪些键（DiffAgainstExample）。
    /// </summary>
    public static void EnsureExampleStub()
    {
        try
        {
            SpeedTestConfig.EnsureDirs();
            if (File.Exists(SpeedTestConfig.ExamplePath)) return;
            File.WriteAllText(SpeedTestConfig.ExamplePath, DefaultYaml, Encoding.UTF8);
        }
        catch { }
    }
}
