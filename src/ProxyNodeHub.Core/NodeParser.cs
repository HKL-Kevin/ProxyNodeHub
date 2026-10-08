using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ProxyNodeHub;

/// <summary>
/// 节点 URI 拆行 / 计数 / 去重合并。
///
/// 设计原则：本类只做「文本 → URI 行」的最薄一层，不做协议级解析。
/// 协议白名单、sing-box JSON → URI 还原这类容易出 bug 的手写逻辑一律不在这里做 ——
/// 格式归一由外部 subconverter（minisubconvert / sub-store）负责，它的协议覆盖面和
/// 维护活跃度都远胜于我们自己维护一份 scheme 列表。
///
/// 因此 SplitNodeLines 的口径是：能解 base64 整块就解，解完（或本身就是明文）后，
/// 凡是「这一行里有 ://」就当成一条节点 URI 收下。具体协议是否被后续测速内核支持，
/// 由 NodeCatalog / SpeedTestRunner 那一层判断，不在这里丢。
/// </summary>
public static class NodeParser
{
    /// <summary>统计内容中的节点数 (支持整块 Base64 订阅 / Clash YAML)</summary>
    public static int CountNodes(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        var lines = SplitNodeLines(content);
        if (lines.Count > 0) return lines.Count;
        // Clash YAML: 统计 proxies 下的 name 条目
        if (content.Contains("proxies:"))
            return content.Split('\n').Count(l => l.TrimStart().StartsWith("- name:") || l.TrimStart().StartsWith("- {name:"));
        return 0;
    }

    /// <summary>
    /// 把文本拆成节点 URI 行列表 (自动处理整块 Base64)。
    /// 不做协议白名单过滤：只要行内含 "://" 即视为节点行。
    /// 未识别格式（纯 Clash YAML 等）返回空，交给 CountNodes 的 YAML 分支或外部 subconverter。
    /// </summary>
    public static List<string> SplitNodeLines(string content)
    {
        var result = new List<string>();
        var text = content.Trim();

        // 整块 Base64 订阅：内容里没有任何 "://"，尝试解一次 base64。
        if (!text.Contains("://"))
        {
            var decoded = TryDecodeBase64(text);
            if (decoded != null && decoded.Contains("://"))
                text = decoded;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            // 长度门槛挡空行 / 噪声；含 "://" 即节点 URI，不限 scheme。
            if (line.Length > 10 && line.Contains("://"))
                result.Add(line);
        }
        return result;
    }

    /// <summary>提取去重键 (server:port)。无法解析返回 null。</summary>
    public static string? GetDedupKey(string uri)
    {
        try
        {
            uri = uri.Trim();

            // vmess://base64(JSON)，JSON 里 add/port 字段才是真正的服务器端口。
            if (uri.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var json = TryDecodeBase64(uri[8..]);
                if (json == null) return null;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var add = root.TryGetProperty("add", out var a) ? a.GetString() : null;
                var port = root.TryGetProperty("port", out var p) ? p.ToString() : null;
                if (!string.IsNullOrEmpty(add)) return $"{add}:{port}";
                return null;
            }

            // ssr://base64(host:port:...)，解开后前两段就是 host:port。
            if (uri.StartsWith("ssr://", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = TryDecodeBase64(uri[6..]);
                if (decoded != null)
                {
                    var parts = decoded.Split(':');
                    if (parts.Length > 1) return $"{parts[0]}:{parts[1]}";
                }
                return null;
            }

            // 其余所有 scheme（vless/trojan/ss/hy2/hysteria2/hysteria/tuic/anytls/socks/...）
            // 统一按「:// 之后、@ 之后、第一个 ? # / 之前」取 host:port。
            // 这覆盖了 subconverter target=uri 输出的所有常见形态，无需逐一列举 scheme。
            var rest = uri[(uri.IndexOf("://", StringComparison.Ordinal) + 3)..];
            if (rest.Contains('@')) rest = rest[(rest.LastIndexOf('@') + 1)..];
            foreach (var sep in new[] { '?', '#', '/' })
                if (rest.Contains(sep)) rest = rest[..rest.IndexOf(sep)];
            return rest.Contains(':') ? rest : null;
        }
        catch { }
        return null;
    }

    /// <summary>合并多个订阅内容: 解析 → 按 server:port 去重 → 输出</summary>
    public static (string plain, string base64, int total, int unique) MergeNodes(List<string> contents)
    {
        var seen = new HashSet<string>();
        var nodes = new List<string>();
        int total = 0;

        foreach (var content in contents)
        {
            if (string.IsNullOrEmpty(content)) continue;
            foreach (var line in SplitNodeLines(content))
            {
                total++;
                var key = GetDedupKey(line) ?? "h:" + line;
                if (seen.Add(key))
                    nodes.Add(line);
            }
        }

        var plain = string.Join("\n", nodes);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        return (plain, b64, total, nodes.Count);
    }

    private static string? TryDecodeBase64(string s)
    {
        try
        {
            s = s.Trim().Replace("-", "+").Replace("_", "/");
            if (s.Length == 0) return null;
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return null;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch { return null; }
    }
}
