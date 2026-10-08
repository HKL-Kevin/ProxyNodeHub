using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ProxyNodeHub;

/// <summary>已知仓库配置模型</summary>
public class KnownRepoConfig
{
    public List<KnownRepoEntry> known_repos { get; set; } = new();
}

public class KnownRepoEntry
{
    public string full_name { get; set; } = "";
    public string path { get; set; } = "";
    public string type { get; set; } = "Base64";
    public bool is_absolute_url { get; set; }
}

/// <summary>已知仓库配置加载器 (从 JSON 文件动态加载)</summary>
public static class KnownRepoLoader
{
    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "known_repos.json");

    private static Dictionary<string, List<KnownRepoEntry>>? _cache;

    /// <summary>加载已知仓库映射 (文件名 → 条目列表)</summary>
    private static Dictionary<string, List<KnownRepoEntry>> LoadConfig()
    {
        if (_cache != null) return _cache;

        _cache = new Dictionary<string, List<KnownRepoEntry>>(StringComparer.OrdinalIgnoreCase);

        // 优先读磁盘文件（用户可编辑，桌面版沿用此路径）；
        // 读不到再退回内嵌资源 —— 容器镜像里只有单个可执行文件，
        // 不带外置 json，没有回退就完全丢失这份映射。
        var json = ReadConfigJson();
        try
        {
            if (!string.IsNullOrEmpty(json))
            {
                var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.KnownRepoConfig);
                if (config != null)
                {
                    foreach (var entry in config.known_repos)
                    {
                        if (string.IsNullOrEmpty(entry.full_name)) continue;
                        if (!_cache.TryGetValue(entry.full_name, out var list))
                        {
                            list = new List<KnownRepoEntry>();
                            _cache[entry.full_name] = list;
                        }
                        list.Add(entry);
                    }
                }
            }
        }
        catch { /* 解析失败时返回空配置 */ }

        return _cache;
    }

    private static string? ReadConfigJson()
    {
        try
        {
            if (File.Exists(ConfigPath)) return File.ReadAllText(ConfigPath);
        }
        catch { }

        try
        {
            var asm = typeof(KnownRepoLoader).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("known_repos.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>获取指定仓库的已知订阅链接</summary>
    public static List<SubscriptionLink> GetKnownLinks(string fullName)
    {
        var config = LoadConfig();
        if (!config.TryGetValue(fullName, out var entries))
            return new List<SubscriptionLink>();

        return entries.Select(e => new SubscriptionLink
        {
            Name = e.path,
            Url = e.is_absolute_url ? e.path : BuildUrl(fullName, e.path),
            Type = e.type,
            NodeCount = -1,
            IsValid = true,
            IsAnalyzed = false
        }).ToList();
    }

    private static string BuildUrl(string fullName, string path)
    {
        return $"https://raw.githubusercontent.com/{fullName}/main/{path}";
    }

    /// <summary>强制重新加载配置 (编辑后调用)</summary>
    public static void Reload() => _cache = null;
}
