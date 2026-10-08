using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyNodeHub;

// ══════════════════════ 特征库数据模型 ══════════════════════

/// <summary>仓库分类</summary>
public enum RepoCategory
{
    NodeProvider,      // 节点提供仓库
    LinkAggregator,    // 订阅链接聚合仓库
    IndirectRef,       // 间接引用（README 指向其他仓库）
    NotNodeRepo        // 非节点仓库（面板/脚本/工具）
}

/// <summary>仓库特征记录 (持久化学习)</summary>
public class RepoFeature
{
    public string FullName = "";
    public RepoCategory Category = RepoCategory.NodeProvider;
    public List<string> KnownSubPaths = new();     // 已确认的订阅文件路径
    public int TotalNodes = 0;
    public DateTime LastAnalyzed = DateTime.MinValue;
    public int AnalysisCount = 0;
    public bool IsReliable = false;                 // 多次分析成功 → 可靠
}

/// <summary>特征库 (本地持久化)</summary>
public static class FeatureLibrary
{
    private static readonly string LibPath = Path.Combine(
        AppEnvironment.UserDataDir, "features.json");

    private static Dictionary<string, RepoFeature> _features = new();
    private static bool _loaded = false;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(LibPath))
            {
                var json = File.ReadAllText(LibPath);
                _features = JsonSerializer.Deserialize<Dictionary<string, RepoFeature>>(json) ?? new();
            }
        }
        catch { _features = new(); }
        _loaded = true;
    }

    public static RepoFeature? Get(string fullName)
    {
        EnsureLoaded();
        return _features.TryGetValue(fullName, out var f) ? f : null;
    }

    public static void Save(RepoFeature feature)
    {
        EnsureLoaded();
        _features[feature.FullName] = feature;
        try
        {
            var dir = Path.GetDirectoryName(LibPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(LibPath, JsonSerializer.Serialize(_features, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static List<RepoFeature> GetReliableRepos()
    {
        EnsureLoaded();
        return _features.Values.Where(f => f.IsReliable && f.KnownSubPaths.Count > 0).ToList();
    }
}

// ══════════════════════ Layer 5: 非节点仓库排除 ══════════════════════

public static class RepoClassifier
{
    // 非节点仓库的文件特征 (出现这些文件 → 可能是工具/面板/脚本)
    private static readonly string[] ToolFilePatterns =
    {
        "main.go", "Dockerfile", "Makefile", "docker-compose.yml",
        "package.json", "setup.py", "CMakeLists.txt", "Cargo.toml",
        "go.mod", "requirements.txt", "pom.xml", "build.gradle"
    };

    // README 中的非节点仓库关键词
    private static readonly string[] NotNodeKeywords =
    {
        "panel", "deploy", "script", "installer", "management",
        "dashboard", "admin", "control panel", "one-click", "vps"
    };

    // 节点仓库的 README 关键词
    private static readonly string[] NodeRepoKeywords =
    {
        "subscription", "订阅", "节点", "free node", "free proxy",
        "v2ray", "clash", "trojan", "shadowsocks", "ssr",
        "vmess", "vless", "proxy pool", "节点池", "免费"
    };

    /// <summary>分类仓库: 判断是否为节点仓库</summary>
    public static RepoCategory Classify(List<string> fileTree, string? readmeContent)
    {
        if (fileTree.Count == 0) return RepoCategory.IndirectRef;

        // 检查是否有工具项目特征文件
        int toolFileCount = fileTree.Count(f => ToolFilePatterns.Any(p =>
            f.Equals(p, StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase)));

        // 检查是否有可能的节点文件
        bool hasNodeFiles = fileTree.Any(f =>
            SubscriptionFinder.IsCandidateNodeFile(f));

        // 检查 README 内容
        string readmeLower = (readmeContent ?? "").ToLower();

        bool hasNodeKeywords = NodeRepoKeywords.Any(k => readmeLower.Contains(k));
        bool hasNotNodeKeywords = NotNodeKeywords.Any(k => readmeLower.Contains(k));

        // 有工具文件 + 无节点文件 + 无节点关键词 → 非节点仓库
        if (toolFileCount >= 2 && !hasNodeFiles && !hasNodeKeywords)
            return RepoCategory.NotNodeRepo;

        // 有工具文件 + 有非节点关键词 + 无节点关键词 → 非节点仓库
        if (toolFileCount >= 1 && hasNotNodeKeywords && !hasNodeKeywords && !hasNodeFiles)
            return RepoCategory.NotNodeRepo;

        // 有节点文件 → 节点仓库
        if (hasNodeFiles) return RepoCategory.NodeProvider;

        // 有节点关键词但无节点文件 → 可能是间接引用
        if (hasNodeKeywords && !hasNodeFiles)
        {
            // 检查是否有链接列表特征
            var linkFiles = fileTree.Where(f =>
                f.ToLower().Contains("sub") || f.ToLower().Contains("link")).ToList();
            if (linkFiles.Count > 0) return RepoCategory.LinkAggregator;
            return RepoCategory.IndirectRef;
        }

        // 默认: 待定
        return RepoCategory.IndirectRef;
    }
}

// ══════════════════════ Layer 2: 文件特征匹配 ══════════════════════

public static partial class SubscriptionFinder
{
    // 节点文件扩展名
    public static readonly string[] NodeFileExtensions = { ".txt", ".yaml", ".yml", ".json", ".conf", ".text" };

    // 节点文件关键词 (文件名包含)
    public static readonly string[] NodeFileKeywords =
    {
        "sub", "node", "v2ray", "vless", "trojan", "ss", "clash",
        "proxy", "config", "mixed", "merge", "pool", "list", "free",
        "air", "shield", "star", "vip", "mix", "sub3", "8eb", "9pb",
        "rocket", "filter", "lifetime", "channel", "server", "clean"
    };

    // 排除的文件模式
    private static readonly string[] ExcludePatterns =
    {
        ".png", ".jpg", ".gif", ".svg", ".ico", ".md", ".lock",
        ".sum", ".mod", ".gitignore", ".gitattributes", ".csv",
        "license", "changelog", "dockerfile", "makefile",
        ".github/", "node_modules/", "test", "example", "sample",
        ".env", "config.yaml.example"
    };

    /// <summary>判断文件是否可能是节点文件</summary>
    public static bool IsCandidateNodeFile(string path)
    {
        var lower = path.ToLower();

        // 排除
        if (ExcludePatterns.Any(p => lower.Contains(p))) return false;

        // 扩展名匹配 (或无扩展名)
        bool extOk = NodeFileExtensions.Any(e => lower.EndsWith(e)) || !lower.Contains(".");
        if (!extOk) return false;

        // 关键词匹配
        bool kwOk = NodeFileKeywords.Any(k => lower.Contains(k));
        if (!kwOk) return false;

        // 排除源代码文件
        if (lower.EndsWith(".go") || lower.EndsWith(".py") || lower.EndsWith(".js") ||
            lower.EndsWith(".sh") || lower.EndsWith(".rs") || lower.EndsWith(".java"))
            return false;

        return true;
    }

    /// <summary>从文件树中筛选候选节点文件 (按优先级排序, 最多 8 个)</summary>
    public static List<string> GetCandidateFiles(List<string> fileTree)
    {
        return fileTree
            .Where(IsCandidateNodeFile)
            .OrderBy(f => f.Count(c => c == '/'))    // 浅层优先
            .ThenBy(f => f.Length)                     // 短文件名优先
            .Take(8)                                   // 最多 8 个候选 (减少 HTTP 请求)
            .ToList();
    }
}
