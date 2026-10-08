using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

public static partial class SubscriptionFinder
{
    // 常见订阅文件路径 (Layer 4: 快速探测) - 精简为最高频的 8 个
    private static readonly string[] CommonPaths =
    {
        "sub", "base64.txt", "v2ray.txt", "nodes.txt",
        "clash.yaml", "output/v2ray-base64.txt", "output/clash.yaml", "result/sub"
    };

    /// <summary>
    /// 六层探测机制:
    /// Layer 1: 特征库已知映射 (持久化学习)
    /// Layer 2: 硬编码已知仓库
    /// Layer 3: 仓库分类 + 文件树探测
    /// Layer 4: 常见路径快速探测 (并发执行)
    /// Layer 5: README 订阅链接解析
    /// Layer 6: 兜底候选
    /// </summary>
    public static async Task<List<SubscriptionLink>> FindLinksAsync(
        GitHubService github, string fullName, CancellationToken ct = default,
        Action<string>? log = null, string branch = "main")
    {
        void Log(string msg) => log?.Invoke(msg);

        var links = new List<SubscriptionLink>();
        var feature = new RepoFeature { FullName = fullName, LastAnalyzed = System.DateTime.Now, AnalysisCount = 1 };

        // 加载已有特征
        var existing = FeatureLibrary.Get(fullName);
        if (existing != null)
        {
            feature.AnalysisCount = existing.AnalysisCount + 1;
            feature.KnownSubPaths = existing.KnownSubPaths.ToList();
        }

        // ═══ Layer 1: 特征库已知映射 (最快, 直接使用已知路径) ═══
        if (feature.KnownSubPaths.Count > 0)
        {
            Log($"│  [L1] 特征库命中 {feature.KnownSubPaths.Count} 个已知路径");
            foreach (var path in feature.KnownSubPaths)
            {
                var content = await github.GetRawFileAsync(fullName, path, branch, ct);
                if (string.IsNullOrEmpty(content) || content.Length < 50) continue;
                var count = NodeParser.CountNodes(content);
                if (count > 0)
                {
                    links.Add(new SubscriptionLink
                    {
                        Name = path, Url = BuildUrl(fullName, path, branch),
                        Type = DetectType(path), NodeCount = count,
                        IsValid = true, IsAnalyzed = true
                    });
                }
            }
            if (links.Any(l => l.NodeCount > 0))
            {
                feature.TotalNodes = links.Max(l => l.NodeCount);
                feature.IsReliable = true;
                FeatureLibrary.Save(feature);
                return links;
            }
        }

        // ═══ Layer 2: 硬编码已知仓库 (从 JSON 配置动态加载) ═══
        var known = KnownRepoLoader.GetKnownLinks(fullName);
        if (known.Count > 0)
        {
            Log($"│  [L2] 硬编码映射命中 {known.Count} 个链接");
            foreach (var link in known)
            {
                var content = await github.GetUrlAsync(link.Url, ct);
                if (content != null)
                {
                    link.NodeCount = NodeParser.CountNodes(content);
                    link.IsValid = link.NodeCount > 0;
                    link.IsAnalyzed = true;
                }
                else
                {
                    link.NodeCount = -1;
                    link.IsValid = true;
                    link.IsAnalyzed = false;
                }
                links.Add(link);
            }
            if (links.Any(l => l.NodeCount > 0))
            {
                feature.KnownSubPaths = links.Where(l => l.NodeCount > 0)
                    .Select(l => l.Name).ToList();
                feature.TotalNodes = links.Max(l => l.NodeCount);
                feature.IsReliable = true;
                FeatureLibrary.Save(feature);
                return links;
            }
        }

        // ═══ Layer 3: 仓库分类 + 文件树探测 ═══
        var fileTree = await github.GetFileTreeAsync(fullName, ct);
        var readme = await github.GetReadmeAsync(fullName, branch, ct);
        var category = RepoClassifier.Classify(fileTree, readme);

        // 非节点仓库 → 返回空
        if (category == RepoCategory.NotNodeRepo)
        {
            Log($"│  [L3] 分类结果: 非节点仓库 (工具/面板项目), 跳过");
            feature.Category = RepoCategory.NotNodeRepo;
            FeatureLibrary.Save(feature);
            return new List<SubscriptionLink>();
        }

        // 从文件树探测候选节点文件 (并发执行, 最多 5 个结果)
        if (fileTree.Count > 0)
        {
            var candidates = GetCandidateFiles(fileTree);
            Log($"│  [L3] 文件树: {fileTree.Count} 个文件, 筛选出 {candidates.Count} 个候选 (并发探测)");
            var l3Links = new List<SubscriptionLink>();
            var l3Semaphore = new SemaphoreSlim(4, 4);
            var l3Tasks = candidates
                .Where(path => !links.Any(l => l.Name == path))
                .Select(async path =>
                {
                    await l3Semaphore.WaitAsync(ct);
                    try
                    {
                        var content = await github.GetRawFileAsync(fullName, path, branch, ct);
                        if (string.IsNullOrEmpty(content) || content.Length < 50) return null;
                        var count = NodeParser.CountNodes(content);
                        if (count <= 0) return null;
                        return new SubscriptionLink
                        {
                            Name = path, Url = BuildUrl(fullName, path, branch),
                            Type = DetectType(path), NodeCount = count,
                            IsValid = true, IsAnalyzed = true
                        };
                    }
                    catch { return null; }
                    finally { l3Semaphore.Release(); }
                });

            var l3Results = await Task.WhenAll(l3Tasks);
            l3Links = l3Results.Where(r => r != null).Cast<SubscriptionLink>().Take(5).ToList();
            links.AddRange(l3Links);

            if (links.Any(l => l.NodeCount > 0))
            {
                feature.KnownSubPaths = links.Where(l => l.NodeCount > 0)
                    .Select(l => l.Name).ToList();
                feature.TotalNodes = links.Max(l => l.NodeCount);
                feature.IsReliable = true;
                FeatureLibrary.Save(feature);
                return links;
            }
        }

        // ═══ Layer 4: 常见路径快速探测 (并发执行, 最多 5 个结果) ═══
        Log($"│  [L4] 并发探测 {CommonPaths.Length} 个常见路径...");
        var l4Links = new List<SubscriptionLink>();
        var l4Semaphore = new SemaphoreSlim(4, 4);
        var l4Tasks = CommonPaths
            .Where(path => !links.Any(l => l.Name == path))
            .Select(async path =>
            {
                await l4Semaphore.WaitAsync(ct);
                try
                {
                    var content = await github.GetRawFileAsync(fullName, path, branch, ct);
                    if (string.IsNullOrEmpty(content) || content.Length < 50) return null;
                    var count = NodeParser.CountNodes(content);
                    if (count <= 0) return null;
                    return new SubscriptionLink
                    {
                        Name = path, Url = BuildUrl(fullName, path, branch),
                        Type = DetectType(path), NodeCount = count,
                        IsValid = true, IsAnalyzed = true
                    };
                }
                catch { return null; }
                finally { l4Semaphore.Release(); }
            });

        var l4Results = await Task.WhenAll(l4Tasks);
        l4Links = l4Results.Where(r => r != null).Cast<SubscriptionLink>().Take(5).ToList();
        links.AddRange(l4Links);

        if (links.Any(l => l.NodeCount > 0))
        {
            feature.KnownSubPaths = links.Where(l => l.NodeCount > 0)
                .Select(l => l.Name).ToList();
            feature.TotalNodes = links.Max(l => l.NodeCount);
            feature.IsReliable = true;
            FeatureLibrary.Save(feature);
            return links;
        }

        // ═══ Layer 5: README 订阅链接解析 ═══
        Log($"│  [L5] 解析 README 中的订阅链接...");
        var readmeLinks = await ParseReadmeLinks(github, readme, ct);
        if (readmeLinks.Count > 0)
            Log($"│  [L5] README 中找到 {readmeLinks.Count} 个有效链接");
        foreach (var link in readmeLinks)
        {
            if (links.Any(l => l.Url == link.Url)) continue;
            links.Add(link);
            if (links.Count >= 5) break;
        }
        if (links.Any(l => l.NodeCount > 0))
        {
            feature.Category = RepoCategory.LinkAggregator;
            feature.TotalNodes = links.Max(l => l.NodeCount);
            FeatureLibrary.Save(feature);
            return links;
        }

        // ═══ Layer 6: 兜底候选 (未验证) ═══
        if (links.Count == 0)
        {
            foreach (var path in new[] { "sub", "base64.txt" })
            {
                links.Add(new SubscriptionLink
                {
                    Name = path, Url = BuildUrl(fullName, path, branch),
                    Type = "Base64", NodeCount = -1,
                    IsValid = true, IsAnalyzed = false
                });
            }
        }

        FeatureLibrary.Save(feature);
        return links;
    }

    /// <summary>解析 README 中的 raw.githubusercontent.com 订阅链接</summary>
    private static async Task<List<SubscriptionLink>> ParseReadmeLinks(
        GitHubService github, string? readme, CancellationToken ct)
    {
        var result = new List<SubscriptionLink>();
        if (string.IsNullOrEmpty(readme) || readme.Length < 50) return result;

        var regex = new Regex(
            @"https?://raw\.githubusercontent\.com/([a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)/([a-zA-Z0-9_.-]+)/([^\s\)\]""'<>,;]+)",
            RegexOptions.IgnoreCase);

        var seen = new HashSet<string>();
        foreach (Match match in regex.Matches(readme))
        {
            if (seen.Count >= 5) break;
            var url = match.Value.TrimEnd('.', ')');
            if (seen.Contains(url)) continue;
            seen.Add(url);

            var path = match.Groups[3].Value + "/" + match.Groups[4].Value;
            if (IsNonNodeFile(path)) continue;

            var content = await github.GetUrlAsync(url, ct);
            if (string.IsNullOrEmpty(content) || content.Length < 50) continue;

            var count = NodeParser.CountNodes(content);
            if (count <= 0) continue;

            result.Add(new SubscriptionLink
            {
                Name = match.Groups[4].Value,
                Url = url, Type = DetectType(path),
                NodeCount = count, IsValid = true, IsAnalyzed = true
            });
        }

        return result;
    }

    private static bool IsNonNodeFile(string path)
    {
        var lower = path.ToLower();
        return lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".gif") ||
               lower.EndsWith(".svg") || lower.EndsWith(".ico") || lower.EndsWith(".md") ||
               lower.EndsWith(".lock") || lower.EndsWith(".sum") || lower.EndsWith(".mod") ||
               lower.EndsWith(".gitignore") || lower.EndsWith(".gitattributes") ||
               lower.Contains("license") || lower.Contains("changelog") ||
               lower.Contains("dockerfile") || lower.Contains("makefile");
    }

    private static string DetectType(string path)
    {
        var lower = path.ToLower();
        if (lower.Contains("clash")) return "Clash";
        if (lower.Contains("singbox") || lower.Contains("sing-box")) return "Singbox";
        return "Base64";
    }

    private static string BuildUrl(string fullName, string path, string branch)
    {
        var b = string.IsNullOrEmpty(branch) ? "main" : branch;
        return $"https://raw.githubusercontent.com/{fullName}/{b}/{path}";
    }
}
