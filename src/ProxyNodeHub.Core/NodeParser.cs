using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ProxyNodeHub;

/// <summary>节点 URI 解析 / 计数 / 去重合并</summary>
public static class NodeParser
{
    private static readonly string[] Protocols =
        { "vmess://", "vless://", "trojan://", "ss://", "ssr://", "hy2://", "tuic://" };

    /// <summary>统计内容中的节点数 (支持 Base64 订阅 / Clash YAML)</summary>
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

    /// <summary>把文本拆成节点 URI 行列表 (自动处理整块 Base64)</summary>
    public static List<string> SplitNodeLines(string content)
    {
        var result = new List<string>();
        var text = content.Trim();

        if (!Protocols.Any(p => text.Contains(p)))
        {
            var decoded = TryDecodeBase64(text);
            if (decoded != null && Protocols.Any(p => decoded.Contains(p)))
                text = decoded;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 10 && Protocols.Any(p => line.StartsWith(p)))
                result.Add(line);
        }
        return result;
    }

    /// <summary>提取去重键 (server:port)</summary>
    public static string? GetDedupKey(string uri)
    {
        try
        {
            uri = uri.Trim();

            if (uri.StartsWith("vmess://"))
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

            if (uri.StartsWith("vless://") || uri.StartsWith("trojan://") ||
                uri.StartsWith("hy2://") || uri.StartsWith("tuic://"))
            {
                var rest = uri[(uri.IndexOf("://") + 3)..];
                if (rest.Contains('@')) rest = rest[(rest.LastIndexOf('@') + 1)..];
                foreach (var sep in new[] { '?', '#', '/' })
                    if (rest.Contains(sep)) rest = rest[..rest.IndexOf(sep)];
                return rest.Contains(':') ? rest : null;
            }

            if (uri.StartsWith("ss://"))
            {
                var rest = uri[5..];
                if (rest.Contains('@')) rest = rest[(rest.LastIndexOf('@') + 1)..];
                foreach (var sep in new[] { '#', '/' })
                    if (rest.Contains(sep)) rest = rest[..rest.IndexOf(sep)];
                return rest.Contains(':') ? rest : null;
            }

            if (uri.StartsWith("ssr://"))
            {
                var decoded = TryDecodeBase64(uri[6..]);
                if (decoded != null)
                {
                    var parts = decoded.Split(':');
                    if (parts.Length > 1) return $"{parts[0]}:{parts[1]}";
                }
            }
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
