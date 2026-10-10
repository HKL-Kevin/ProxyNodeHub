using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace ProxyNodeHub;

/// <summary>
/// 存活节点轻量摘要（供 Web 界面、统计与筛选使用）。
/// 不含敏感私钥，确保前后端传输安全与秒级序列化。
/// </summary>
public class AliveNodeSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public int LatencyMs { get; set; }
    public int ConsecutiveAlive { get; set; } = 1;
    public string FirstSeenAt { get; set; } = "";
    public string LastAliveAt { get; set; } = "";
}

/// <summary>
/// 存活池统计画像。
/// </summary>
public class AlivePoolStats
{
    public int TotalCount { get; set; }
    public int StableCount { get; set; } // 连续存活 >= 2 轮
    public Dictionary<string, int> CountryDistribution { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? LastUpdatedAt { get; set; }
}

/// <summary>
/// 存活池单一事实来源存储（基于 SQLite 嵌入式单表）。
///
/// 架构设计要点：
/// 1. 强一致性保证（ACID 事务）：元数据与 raw_yaml 文本列存放在同一条记录中，
///    原子提交，杜绝多文件双写撕裂。
/// 2. 协议凭据 0 丢失：raw_yaml 列保存原生 Clash YAML 节点块（含 uuid、reality-opts 等），
///    不反序列化为有限 C# 实体，因此支持未来任意协议的任意专有字段。
/// 3. 下游零拷贝消费：导出端点直接 SELECT raw_yaml 并拼成 proxies 列表，
///    内核测速与客户端订阅可无缝直通消费。
/// 4. 连续存活打分：跨轮次自动累加 consecutive_alive 计数，形成节点健康画像。
/// </summary>
public static class AliveNodeStore
{
    public static string DbPath => Path.Combine(AppEnvironment.UserDataDir, "nodes.db");
    private static string ConnectionString => $"Data Source={DbPath}";
    private static readonly object _lock = new();
    private static bool _initialized = false;

    /// <summary>初始化 SQLite 表结构与索引（幂等执行）。</summary>
    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            Directory.CreateDirectory(AppEnvironment.UserDataDir);

            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS alive_nodes (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    protocol TEXT NOT NULL,
                    server TEXT NOT NULL,
                    port INTEGER NOT NULL,
                    country TEXT,
                    city TEXT,
                    latency_ms INTEGER NOT NULL DEFAULT 0,
                    raw_yaml TEXT NOT NULL,
                    consecutive_alive INTEGER NOT NULL DEFAULT 1,
                    first_seen_at TEXT NOT NULL,
                    last_alive_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_alive_nodes_last_alive ON alive_nodes(last_alive_at);
                CREATE INDEX IF NOT EXISTS idx_alive_nodes_country ON alive_nodes(country);
                CREATE INDEX IF NOT EXISTS idx_alive_nodes_consecutive ON alive_nodes(consecutive_alive);
            """;
            cmd.ExecuteNonQuery();
            try { MigrateExistingNodesToFingerprintInternal(conn); } catch { }
            _initialized = true;
        }
    }

    /// <summary>
    /// 从原生 Clash YAML 中无损提取各个独立的代理节点 YAML 块。
    /// 自动识别 proxies 下的项缩进（避免误切 alpn 等嵌套列表），并规范化为标准 2 空格缩进。
    /// </summary>
    public static List<string> ExtractProxyBlocks(string yaml)
    {
        var blocks = new List<string>();
        if (string.IsNullOrWhiteSpace(yaml)) return blocks;

        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        var currentLines = new List<string>();
        bool inProxies = false;
        int proxyIndent = -1;

        void FlushCurrent()
        {
            if (currentLines.Count == 0) return;
            var first = currentLines[0];
            int currentBaseIndent = first.Length - first.TrimStart().Length;
            int shift = 2 - currentBaseIndent;

            var sb = new StringBuilder();
            foreach (var l in currentLines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (shift > 0)
                    sb.AppendLine(new string(' ', shift) + l);
                else if (shift < 0 && l.Length >= -shift && l[..(-shift)].Trim().Length == 0)
                    sb.AppendLine(l[(-shift)..]);
                else
                    sb.AppendLine(l);
            }

            var res = sb.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(res))
                blocks.Add(res);
            currentLines.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("proxies:"))
            {
                inProxies = true;
                continue;
            }

            if (!inProxies) continue;

            // 如果遇到下一个顶级键（不缩进且以冒号结尾，如 proxy-groups: / rules:）
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Contains(':'))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = line.Length - trimmed.Length;

            if (trimmed.StartsWith("- "))
            {
                if (proxyIndent < 0)
                {
                    proxyIndent = indent;
                }

                // 只有当缩进严格等于首个代理项的缩进时，才视为新代理项的开始
                // 深度更深的 "- "（如 alpn: 下的列表）视为当前代理项的内容
                if (indent == proxyIndent)
                {
                    FlushCurrent();
                }
            }

            if (proxyIndent >= 0)
            {
                currentLines.Add(line);
            }
        }

        FlushCurrent();
        return blocks;
    }

    /// <summary>
    /// 基于节点全维度物理连接拓扑（协议 + 服务器 + 端口 + SNI/servername + Host + Path + Reality公钥 + 凭据/uuid/密码）计算确定性指纹 ID。
    /// 确保 CDN 优选 IP、反代分流等同 IP/同端口场景下不同 SNI/Host 的物理节点获得独立的指纹，杜绝冲突覆盖。
    /// </summary>
    public static string ComputeFingerprint(string protocol, string server, int port, string rawYaml)
    {
        var authVal = "";
        var uuidMatch = Regex.Match(rawYaml, @"uuid\s*:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
        if (uuidMatch.Success) authVal = uuidMatch.Groups[1].Value.Trim();
        else
        {
            var passMatch = Regex.Match(rawYaml, @"password\s*:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
            if (passMatch.Success) authVal = passMatch.Groups[1].Value.Trim();
        }

        var sniMatch = Regex.Match(rawYaml, @"(?:servername|sni)\s*:\s*[""']?([^""'\r\n\s]+)[""']?", RegexOptions.IgnoreCase);
        var sni = sniMatch.Success ? sniMatch.Groups[1].Value.Trim().ToLowerInvariant() : "";

        var hostMatch = Regex.Match(rawYaml, @"(?:^|\s|,)(?:Host|host)\s*:\s*[""']?([^""'\r\n\s]+)[""']?", RegexOptions.Multiline);
        var host = hostMatch.Success ? hostMatch.Groups[1].Value.Trim().ToLowerInvariant() : "";

        var pathMatch = Regex.Match(rawYaml, @"(?:path|grpc-service-name)\s*:\s*[""']?([^""'\r\n\s]+)[""']?", RegexOptions.IgnoreCase);
        var path = pathMatch.Success ? pathMatch.Groups[1].Value.Trim() : "";

        var pkMatch = Regex.Match(rawYaml, @"public-key\s*:\s*[""']?([^""'\r\n\s]+)[""']?", RegexOptions.IgnoreCase);
        var pubKey = pkMatch.Success ? pkMatch.Groups[1].Value.Trim() : "";

        var sb = new StringBuilder();
        sb.Append(protocol.ToLowerInvariant()).Append("://").Append(server.ToLowerInvariant()).Append(':').Append(port);

        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(sni)) queryParts.Add($"sni={sni}");
        if (!string.IsNullOrEmpty(host) && !string.Equals(host, sni, StringComparison.OrdinalIgnoreCase)) queryParts.Add($"host={host}");
        if (!string.IsNullOrEmpty(path)) queryParts.Add($"path={path}");
        if (!string.IsNullOrEmpty(pubKey)) queryParts.Add($"pbk={pubKey}");

        if (queryParts.Count > 0)
        {
            sb.Append('?').Append(string.Join("&", queryParts));
        }

        if (!string.IsNullOrEmpty(authVal))
        {
            sb.Append('#').Append(authVal);
        }

        var identity = sb.ToString();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 判断是否为已知的 CDN / 任播（Anycast）/ 域名前置边缘网络。
    /// 此类网络的入口 IP 仅代表接入边缘节点，绝不代表后端真实的落地地区与城市。
    /// </summary>
    public static bool IsAnycastCdn(string? org, string? asn, string? server = null)
    {
        if (!string.IsNullOrWhiteSpace(asn))
        {
            var uAsn = asn.Trim().ToUpperInvariant();
            if (uAsn is "AS13335" or "AS20940" or "AS54113" or "AS16509" or "AS8075")
                return true;
        }

        if (!string.IsNullOrWhiteSpace(org))
        {
            var lOrg = org.ToLowerInvariant();
            if (lOrg.Contains("cloudflare") || lOrg.Contains("akamai") || lOrg.Contains("fastly") ||
                lOrg.Contains("cloudfront") || lOrg.Contains("edgeone"))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(server))
        {
            var lServer = server.Trim().ToLowerInvariant();
            if (lServer.Contains("epicgames.com") || lServer.Contains("speedtest.net") ||
                lServer.Contains("csgo.net") || lServer.Contains("cloudflare") ||
                lServer.Contains("cloudfront") || lServer.Contains("edgeone") ||
                lServer.StartsWith("162.159.") || lServer.StartsWith("104.16.") ||
                lServer.StartsWith("104.17.") || lServer.StartsWith("104.18.") ||
                lServer.StartsWith("104.19.") || lServer.StartsWith("104.20.") ||
                lServer.StartsWith("104.21.") || lServer.StartsWith("172.67."))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 原子写入/更新一轮测活结果（ACID 事务保障）。
    /// 对已有节点自动累加连续存活轮数，对新节点建立初次记录。
    /// </summary>
    public static (int inserted, int updated) SaveRound(string yaml, IEnumerable<SpeedTestNode>? nodes = null)
    {
        Initialize();
        if (string.IsNullOrWhiteSpace(yaml)) return (0, 0);

        // 统一格式化为 (国旗地区_城市_N) 标准命名，并剥离速率与丢包标签
        var formattedYaml = SpeedTestRunner.FormatPassedYaml(yaml, nodes);
        var blocks = ExtractProxyBlocks(formattedYaml);
        if (blocks.Count == 0) return (0, 0);

        var nodeLookup = new Dictionary<string, SpeedTestNode>(StringComparer.OrdinalIgnoreCase);
        if (nodes != null)
        {
            foreach (var n in nodes)
            {
                if (!string.IsNullOrWhiteSpace(n.Name)) nodeLookup[n.Name] = n;
                if (!string.IsNullOrWhiteSpace(n.Server))
                {
                    nodeLookup[n.Server] = n;
                    if (int.TryParse(n.Port, out var p)) nodeLookup[$"{n.Server}:{p}"] = n;
                }
            }
        }

        var nowIso = DateTime.UtcNow.ToString("o");
        int inserted = 0;
        int updated = 0;

        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            // 预编译查询语句
            using var queryCmd = conn.CreateCommand();
            queryCmd.Transaction = tx;
            queryCmd.CommandText = "SELECT consecutive_alive, first_seen_at FROM alive_nodes WHERE id = @id";
            var idParam = queryCmd.Parameters.Add("@id", SqliteType.Text);

            // 预编译插入语句
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO alive_nodes (
                    id, name, protocol, server, port, country, city, latency_ms,
                    raw_yaml, consecutive_alive, first_seen_at, last_alive_at
                ) VALUES (
                    @id, @name, @protocol, @server, @port, @country, @city, @latency_ms,
                    @raw_yaml, 1, @first_seen_at, @last_alive_at
                )
            """;
            var insId = insertCmd.Parameters.Add("@id", SqliteType.Text);
            var insName = insertCmd.Parameters.Add("@name", SqliteType.Text);
            var insProto = insertCmd.Parameters.Add("@protocol", SqliteType.Text);
            var insServer = insertCmd.Parameters.Add("@server", SqliteType.Text);
            var insPort = insertCmd.Parameters.Add("@port", SqliteType.Integer);
            var insCountry = insertCmd.Parameters.Add("@country", SqliteType.Text);
            var insCity = insertCmd.Parameters.Add("@city", SqliteType.Text);
            var insLatency = insertCmd.Parameters.Add("@latency_ms", SqliteType.Integer);
            var insYaml = insertCmd.Parameters.Add("@raw_yaml", SqliteType.Text);
            var insFirst = insertCmd.Parameters.Add("@first_seen_at", SqliteType.Text);
            var insLast = insertCmd.Parameters.Add("@last_alive_at", SqliteType.Text);

            // 预编译更新语句
            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = """
                UPDATE alive_nodes SET
                    name = @name,
                    protocol = @protocol,
                    server = @server,
                    port = @port,
                    country = @country,
                    city = @city,
                    latency_ms = @latency_ms,
                    raw_yaml = @raw_yaml,
                    consecutive_alive = consecutive_alive + 1,
                    last_alive_at = @last_alive_at
                WHERE id = @id
            """;
            var upId = updateCmd.Parameters.Add("@id", SqliteType.Text);
            var upName = updateCmd.Parameters.Add("@name", SqliteType.Text);
            var upProto = updateCmd.Parameters.Add("@protocol", SqliteType.Text);
            var upServer = updateCmd.Parameters.Add("@server", SqliteType.Text);
            var upPort = updateCmd.Parameters.Add("@port", SqliteType.Integer);
            var upCountry = updateCmd.Parameters.Add("@country", SqliteType.Text);
            var upCity = updateCmd.Parameters.Add("@city", SqliteType.Text);
            var upLatency = updateCmd.Parameters.Add("@latency_ms", SqliteType.Integer);
            var upYaml = updateCmd.Parameters.Add("@raw_yaml", SqliteType.Text);
            var upLast = updateCmd.Parameters.Add("@last_alive_at", SqliteType.Text);

            foreach (var block in blocks)
            {
                var name = ExtractField(block, "name") ?? "Unknown";
                var protocol = ExtractField(block, "type") ?? "unknown";
                var server = ExtractField(block, "server") ?? "";
                var portStr = ExtractField(block, "port") ?? "0";
                int.TryParse(portStr, out var port);

                if (string.IsNullOrWhiteSpace(server) || port == 0) continue;

                var id = ComputeFingerprint(protocol, server, port, block);

                // 尝试反查国家、城市与延迟
                string? country = null;
                string? city = null;
                int latency = 0;

                if (nodeLookup.TryGetValue(name, out var stNode) ||
                    nodeLookup.TryGetValue(server, out stNode) ||
                    nodeLookup.TryGetValue($"{server}:{port}", out stNode))
                {
                    country = stNode.Country;
                    city = stNode.IpInfoCity;
                    latency = stNode.Speed; // 内核在测活阶段通常用延迟填充
                }

                // 优先使用实测出口 IP 的缓存信息（落地 IP 绝对真实）
                if (!string.IsNullOrWhiteSpace(stNode?.Ip))
                {
                    var cachedExit = IpInfoService.GetCached(stNode.Ip);
                    country ??= cachedExit?.Country;
                    city ??= cachedExit?.City;
                }

                // 优先信任原节点名中明确标注的国家/城市（避免 Anycast CDN 入口机房覆盖原作者标注）
                country ??= TryExtractCountry(name);
                city ??= TryExtractCity(name);

                // 最后兜底：只有当国家未知且 server 并非已知 Anycast CDN 时，才允许使用 server 入口 IP
                if (string.IsNullOrWhiteSpace(country))
                {
                    var cachedIp = IpInfoService.GetCached(server);
                    if (cachedIp != null && !IsAnycastCdn(cachedIp.Org, cachedIp.Asn, server))
                    {
                        country = cachedIp.Country;
                        city ??= cachedIp.City;
                    }
                }

                var shortFp = id.Length >= 5 ? id[..5].ToLowerInvariant() : id.ToLowerInvariant();
                var finalName = BuildStandardNodeName(name, country, city, shortFp);
                var finalBlock = ReplaceYamlName(block, finalName);

                idParam.Value = id;
                using var reader = queryCmd.ExecuteReader();
                if (reader.Read())
                {
                    reader.Close();
                    // 更新已有节点
                    upId.Value = id;
                    upName.Value = finalName;
                    upProto.Value = protocol;
                    upServer.Value = server;
                    upPort.Value = port;
                    upCountry.Value = (object?)country ?? DBNull.Value;
                    upCity.Value = (object?)city ?? DBNull.Value;
                    upLatency.Value = latency;
                    upYaml.Value = finalBlock;
                    upLast.Value = nowIso;
                    updateCmd.ExecuteNonQuery();
                    updated++;
                }
                else
                {
                    reader.Close();
                    // 插入新节点
                    insId.Value = id;
                    insName.Value = finalName;
                    insProto.Value = protocol;
                    insServer.Value = server;
                    insPort.Value = port;
                    insCountry.Value = (object?)country ?? DBNull.Value;
                    insCity.Value = (object?)city ?? DBNull.Value;
                    insLatency.Value = latency;
                    insYaml.Value = finalBlock;
                    insFirst.Value = nowIso;
                    insLast.Value = nowIso;
                    insertCmd.ExecuteNonQuery();
                    inserted++;
                }
            }

            tx.Commit();
        }

        return (inserted, updated);
    }

    /// <summary>
    /// 从 SQLite 存活池导出标准 Clash YAML。
    /// 0 协议类型损失，直接把原汁原味的 raw_yaml 文本块拼入 proxies: 列表。
    /// </summary>
    public static string ExportClashYaml(int? minConsecutive = null, string? country = null)
    {
        Initialize();
        var sb = new StringBuilder();
        sb.AppendLine("proxies:");

        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            var sql = new StringBuilder("SELECT raw_yaml FROM alive_nodes WHERE 1=1");
            if (minConsecutive.HasValue && minConsecutive.Value > 0)
            {
                sql.Append(" AND consecutive_alive >= @minConsecutive");
                cmd.Parameters.AddWithValue("@minConsecutive", minConsecutive.Value);
            }
            if (!string.IsNullOrWhiteSpace(country))
            {
                sql.Append(" AND country = @country");
                cmd.Parameters.AddWithValue("@country", country.Trim().ToUpperInvariant());
            }
            sql.Append(" ORDER BY country ASC, city ASC, consecutive_alive DESC, name ASC");
            cmd.CommandText = sql.ToString();

            using var reader = cmd.ExecuteReader();
            int count = 0;
            while (reader.Read())
            {
                var block = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(block)) continue;
                sb.AppendLine(block);
                count++;
            }

            if (count == 0) return "proxies: []\n";
        }

        return sb.ToString();
    }

    /// <summary>获取存活节点轻量元数据列表（供前端表格渲染）。</summary>
    public static List<AliveNodeSummary> GetSummaries(string? country = null, int limit = 500)
    {
        Initialize();
        var result = new List<AliveNodeSummary>();

        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            var sql = new StringBuilder("""
                SELECT id, name, protocol, server, port, country, city,
                       latency_ms, consecutive_alive, first_seen_at, last_alive_at
                FROM alive_nodes WHERE 1=1
            """);

            if (!string.IsNullOrWhiteSpace(country))
            {
                sql.Append(" AND country = @country");
                cmd.Parameters.AddWithValue("@country", country.Trim().ToUpperInvariant());
            }
            sql.Append(" ORDER BY consecutive_alive DESC, latency_ms ASC, name ASC LIMIT @limit");
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.CommandText = sql.ToString();

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new AliveNodeSummary
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Protocol = reader.GetString(2),
                    Server = reader.GetString(3),
                    Port = reader.GetInt32(4),
                    Country = reader.IsDBNull(5) ? null : reader.GetString(5),
                    City = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LatencyMs = reader.GetInt32(7),
                    ConsecutiveAlive = reader.GetInt32(8),
                    FirstSeenAt = reader.GetString(9),
                    LastAliveAt = reader.GetString(10)
                });
            }
        }

        return result;
    }

    /// <summary>获取存活池统计信息。</summary>
    public static AlivePoolStats GetStats()
    {
        Initialize();
        var stats = new AlivePoolStats();

        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN consecutive_alive >= 2 THEN 1 ELSE 0 END),
                    MAX(last_alive_at)
                FROM alive_nodes
            """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                stats.TotalCount = reader.GetInt32(0);
                stats.StableCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                if (!reader.IsDBNull(2) && DateTime.TryParse(reader.GetString(2), out var dt))
                {
                    stats.LastUpdatedAt = dt;
                }
            }
            reader.Close();

            // 统计地区分布
            using var distCmd = conn.CreateCommand();
            distCmd.CommandText = """
                SELECT COALESCE(country, 'UNKNOWN'), COUNT(*)
                FROM alive_nodes
                GROUP BY country
                ORDER BY COUNT(*) DESC
            """;
            using var distReader = distCmd.ExecuteReader();
            while (distReader.Read())
            {
                var cc = distReader.GetString(0);
                var cnt = distReader.GetInt32(1);
                stats.CountryDistribution[cc] = cnt;
            }
        }

        return stats;
    }

    /// <summary>清理超过指定时间未再存活的失效节点。</summary>
    public static int PruneInactive(TimeSpan maxInactive)
    {
        Initialize();
        var cutoff = DateTime.UtcNow.Subtract(maxInactive).ToString("o");
        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM alive_nodes WHERE last_alive_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private static readonly Dictionary<string, string> _cnCountryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["香港"] = "HK", ["台湾"] = "TW", ["日本"] = "JP", ["新加坡"] = "SG",
        ["美国"] = "US", ["韩国"] = "KR", ["德国"] = "DE", ["英国"] = "GB",
        ["法国"] = "FR", ["俄罗斯"] = "RU", ["加拿大"] = "CA", ["澳大利亚"] = "AU",
        ["爱尔兰"] = "IE", ["波兰"] = "PL", ["土耳其"] = "TR", ["荷兰"] = "NL",
        ["印度"] = "IN", ["印尼"] = "ID", ["泰国"] = "TH", ["马来西亚"] = "MY",
        ["菲律宾"] = "PH", ["越南"] = "VN", ["阿根廷"] = "AR", ["巴西"] = "BR",
        ["瑞士"] = "CH", ["瑞典"] = "SE", ["挪威"] = "NO", ["芬兰"] = "FI",
        ["意大利"] = "IT", ["西班牙"] = "ES", ["葡萄牙"] = "PT", ["希腊"] = "GR"
    };

    public static string CountryCodeToFlag(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2) return "";
        var c1 = char.ToUpperInvariant(code[0]);
        var c2 = char.ToUpperInvariant(code[1]);
        if (c1 >= 'A' && c1 <= 'Z' && c2 >= 'A' && c2 <= 'Z')
        {
            return $"{char.ConvertFromUtf32(0x1F1E6 + (c1 - 'A'))}{char.ConvertFromUtf32(0x1F1E6 + (c2 - 'A'))}";
        }
        return "";
    }

    public static string? TryExtractCountry(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cps = new List<int>();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                int cp = char.ConvertToUtf32(name, i);
                if (cp >= 0x1F1E6 && cp <= 0x1F1FF) cps.Add(cp);
                i++;
            }
        }
        if (cps.Count >= 2)
        {
            return $"{(char)(cps[0] - 0x1F1E6 + 'A')}{(char)(cps[1] - 0x1F1E6 + 'A')}";
        }

        foreach (var (cn, code) in _cnCountryMap)
        {
            if (name.Contains(cn, StringComparison.OrdinalIgnoreCase)) return code;
        }

        var parts = name.Split('_');
        if (parts.Length > 0)
        {
            var m = Regex.Match(parts[0], @"[A-Z]{2}");
            if (m.Success) return m.Value;
        }
        return null;
    }

    public static string? TryExtractCity(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var parts = name.Split('_');
        if (parts.Length >= 3 && !int.TryParse(parts[1], out _) && !Regex.IsMatch(parts[1], @"^[0-9a-fA-F]{5}$"))
        {
            var c = parts[1].Trim();
            if (c.Length > 0 && c != "—" && c != "-") return c;
        }
        return null;
    }

    /// <summary>
    /// 标准化生成节点名称：移除原有的 _序号 / -序号，强制以 5 位主键哈希指纹（_{key[..5]}）作为唯一后缀避免碰撞。
    /// 格式：[国旗代码]_[城市]_[5位指纹] 或 [国旗代码]_[5位指纹] 或 [清洗原名]_[5位指纹]。
    /// </summary>
    public static string BuildStandardNodeName(string currentName, string? country, string? city, string fingerprintKey)
    {
        var shortFp = fingerprintKey.Length >= 5 ? fingerprintKey[..5].ToLowerInvariant() : fingerprintKey.ToLowerInvariant();
        var cleanCity = !string.IsNullOrWhiteSpace(city) && city != "—" && city != "-" && !city.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? city.Trim().Trim('"', '\'')
            : null;

        var decoded = SpeedTestRunner.DecodeUnicode(currentName);
        var pipe = decoded.IndexOf('|');
        var core = pipe > 0 ? decoded[..pipe].Trim() : decoded.Trim();

        // 去除开头的延迟信息，如 [483.2ms +/-451ms] 或 [511.6ms]
        core = Regex.Replace(core, @"^\[[0-9.]+\s*ms[^\]]*\]\s*", "").Trim();

        // 剔除末尾原有的数字序号（如 _1, _14, -14）或已有的 5 位 hex 指纹
        core = Regex.Replace(core, @"(?:[_-]\d{1,4}|_[0-9a-fA-F]{5})$", "").Trim();

        if (!string.IsNullOrWhiteSpace(country) && country.Length == 2)
        {
            var code = country.ToUpperInvariant();
            var flag = CountryCodeToFlag(code);
            var prefix = $"{flag}{code}";

            core = cleanCity != null ? $"{prefix}_{cleanCity}" : prefix;
        }
        else
        {
            if (cleanCity != null && !core.Contains(cleanCity, StringComparison.OrdinalIgnoreCase))
            {
                core = $"{core}_{cleanCity}";
            }
        }

        // 以 5 位指纹作为后缀避免碰撞
        return $"{core}_{shortFp}";
    }

    /// <summary>
    /// 无损替换 Clash YAML 代理块中的 name 属性。
    /// </summary>
    public static string ReplaceYamlName(string block, string newName)
    {
        if (string.IsNullOrWhiteSpace(block)) return block;

        var multilineRegex = new Regex(@"^(?<indent>\s*(?:-\s+)?name\s*:\s*)(?:[""']?[^""'\r\n]+[""']?)", RegexOptions.Multiline);
        if (multilineRegex.IsMatch(block))
        {
            return multilineRegex.Replace(block, m => $"{m.Groups["indent"].Value}\"{newName}\"", 1);
        }

        var inlineRegex = new Regex(@"(?<prefix>(?:\{|,)\s*name\s*:\s*)(?:[""']?[^""',}\r\n]+[""']?)");
        if (inlineRegex.IsMatch(block))
        {
            return inlineRegex.Replace(block, m => $"{m.Groups["prefix"].Value}\"{newName}\"", 1);
        }

        return block;
    }

    private static int MigrateExistingNodesToFingerprintInternal(SqliteConnection conn)
    {
        var rows = new List<(string id, string name, string protocol, string server, int port, string? country, string? city, string rawYaml)>();
        using (var selCmd = conn.CreateCommand())
        {
            selCmd.CommandText = "SELECT id, name, protocol, server, port, country, city, raw_yaml FROM alive_nodes";
            using var reader = selCmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7)
                ));
            }
        }

        int changed = 0;
        using var upCmd = conn.CreateCommand();
        upCmd.CommandText = "UPDATE alive_nodes SET id = @newId, name = @name, country = @country, city = @city, raw_yaml = @raw_yaml WHERE id = @oldId";
        var pNewId = upCmd.Parameters.Add("@newId", SqliteType.Text);
        var pName = upCmd.Parameters.Add("@name", SqliteType.Text);
        var pCountry = upCmd.Parameters.Add("@country", SqliteType.Text);
        var pCity = upCmd.Parameters.Add("@city", SqliteType.Text);
        var pYaml = upCmd.Parameters.Add("@raw_yaml", SqliteType.Text);
        var pOldId = upCmd.Parameters.Add("@oldId", SqliteType.Text);

        foreach (var r in rows)
        {
            var country = r.country ?? TryExtractCountry(r.name);
            var city = r.city ?? TryExtractCity(r.name);

            if (string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(r.server))
            {
                var isAnycast = IsAnycastCdn(null, null, r.server);
                var cachedIp = IpInfoService.GetCached(r.server);
                if (!isAnycast && cachedIp != null && IsAnycastCdn(cachedIp.Org, cachedIp.Asn, r.server))
                {
                    isAnycast = true;
                }

                if (!isAnycast && cachedIp != null)
                {
                    city = cachedIp.City;
                    country ??= cachedIp.Country;
                }
            }

            var newId = ComputeFingerprint(r.protocol, r.server, r.port, r.rawYaml);
            var shortFp = newId.Length >= 5 ? newId[..5].ToLowerInvariant() : newId.ToLowerInvariant();
            var finalName = BuildStandardNodeName(r.name, country, city, shortFp);
            var finalYaml = ReplaceYamlName(r.rawYaml, finalName);

            if (newId != r.id || finalName != r.name || finalYaml != r.rawYaml || country != r.country || city != r.city)
            {
                pOldId.Value = r.id;
                pNewId.Value = newId;
                pName.Value = finalName;
                pCountry.Value = (object?)country ?? DBNull.Value;
                pCity.Value = (object?)city ?? DBNull.Value;
                pYaml.Value = finalYaml;
                upCmd.ExecuteNonQuery();
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// 异步主动补全存活池中因历史原因（如关闭了 IP 探测）缺失地区或城市的节点。
    /// 自动将域名 server 解析为公网 IP 并批量调用 ipinfo.io 查询，更新节点标准名称与地理信息。
    /// </summary>
    public static async Task<int> EnrichMissingGeoInfoAsync(string? token, Action<string>? log = null)
    {
        Initialize();
        var targets = new List<(string id, string name, string server, int port, string rawYaml)>();
        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, server, port, raw_yaml FROM alive_nodes WHERE country IS NULL OR city IS NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                targets.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));
            }
        }

        if (targets.Count == 0) return 0;
        log?.Invoke($"发现 {targets.Count} 个存活节点缺少地区/城市，正在尝试补全…");

        // 收集待查 IP（域名自动 DNS 解析）
        var hostToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (IPAddress.TryParse(t.server, out _))
            {
                hostToIp[t.server] = t.server;
            }
            else
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(t.server);
                    var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString()
                             ?? addrs.FirstOrDefault()?.ToString();
                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        hostToIp[t.server] = ip;
                    }
                }
                catch { }
            }
        }

        var ipsToLookup = hostToIp.Values.Distinct().ToList();
        if (ipsToLookup.Count == 0) return 0;

        var ipInfoMap = await IpInfoService.LookupAsync(ipsToLookup, token, log, CancellationToken.None);
        if (ipInfoMap.Count == 0) return 0;

        int updated = 0;
        lock (_lock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            using var upCmd = conn.CreateCommand();
            upCmd.CommandText = "UPDATE alive_nodes SET name = @name, country = @country, city = @city, raw_yaml = @raw_yaml WHERE id = @id";
            var pName = upCmd.Parameters.Add("@name", SqliteType.Text);
            var pCountry = upCmd.Parameters.Add("@country", SqliteType.Text);
            var pCity = upCmd.Parameters.Add("@city", SqliteType.Text);
            var pYaml = upCmd.Parameters.Add("@raw_yaml", SqliteType.Text);
            var pId = upCmd.Parameters.Add("@id", SqliteType.Text);

            foreach (var t in targets)
            {
                if (!hostToIp.TryGetValue(t.server, out var ip) || !ipInfoMap.TryGetValue(ip, out var info))
                    continue;

                var country = TryExtractCountry(t.name);
                var city = TryExtractCity(t.name);

                if (string.IsNullOrWhiteSpace(country) && !IsAnycastCdn(info.Org, info.Asn, t.server))
                {
                    country = info.Country;
                    city ??= info.City;
                }

                var shortFp = t.id.Length >= 5 ? t.id[..5].ToLowerInvariant() : t.id.ToLowerInvariant();
                var finalName = BuildStandardNodeName(t.name, country, city, shortFp);
                var finalYaml = ReplaceYamlName(t.rawYaml, finalName);

                pId.Value = t.id;
                pName.Value = finalName;
                pCountry.Value = (object?)country ?? DBNull.Value;
                pCity.Value = (object?)city ?? DBNull.Value;
                pYaml.Value = finalYaml;
                upCmd.ExecuteNonQuery();
                updated++;
            }
        }

        if (updated > 0)
        {
            log?.Invoke($"已成功补全 {updated} 个节点的地区与城市信息。");
        }
        return updated;
    }

    private static string? ExtractField(string block, string key)
    {
        var m = Regex.Match(block, $@"(?:^|\s|,){Regex.Escape(key)}\s*:\s*[""']?([^""',}}\r\n]+)[""']?", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
