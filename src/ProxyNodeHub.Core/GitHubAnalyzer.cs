using System;
using System.Collections.Generic;
using System.Linq;

namespace ProxyNodeHub;

/// <summary>
/// GitHub 仓库的纯分析逻辑：评分、bot 检测、处理类型判定、日期解析等。
///
/// 之前这些都堆在 MainForm 里（2844 行的巨型类），与 UI 控件强耦合、无法单测。
/// 这里全是无状态的纯函数，收口后可直接对 CalculateScore / NodeParser 补单元测试。
/// </summary>
public static class GitHubAnalyzer
{
    /// <summary>GitHub 的时间戳都是 UTC 且可能带偏移，解析时统一用这个样式。</summary>
    public static readonly System.Globalization.DateTimeStyles GitHubDateStyles =
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal;

    /// <summary>GitHub API 返回的仓库 → RepoInfo（含 DaysInactive/AgeDays）。</summary>
    public static RepoInfo ToRepoInfo(GitHubRepo r)
    {
        var info = new RepoInfo
        {
            FullName = r.FullName,
            Description = r.Description,
            Stars = r.Stars,
            LastPush = r.PushedAt,
            CreatedAt = r.CreatedAt,
            Branch = string.IsNullOrEmpty(r.DefaultBranch) ? "main" : r.DefaultBranch
        };
        if (DateTime.TryParse(r.PushedAt, null, GitHubDateStyles, out var pushedAt))
            info.DaysInactive = Math.Max(0, (DateTime.UtcNow - pushedAt).Days);
        if (DateTime.TryParse(r.CreatedAt, null, GitHubDateStyles, out var created))
            info.AgeDays = Math.Max(0, (DateTime.UtcNow - created).Days);
        return info;
    }

    /// <summary>仓库是否超过 60 天未更新（搜索阶段预过滤）。</summary>
    public static bool IsStale(GitHubRepo r)
        => DateTime.TryParse(r.PushedAt, null, GitHubDateStyles, out var pushed)
           && Math.Max(0, (DateTime.UtcNow - pushed).Days) > 60;

    public static int BestNodeCount(RepoInfo r)
    {
        var counts = r.Links.Where(l => l.IsAnalyzed && l.IsValid && l.NodeCount > 0).Select(l => l.NodeCount);
        return counts.DefaultIfEmpty(0).Max();
    }

    public static string DetectProcessingType(List<GitHubCommit> commits)
    {
        if (commits.Count == 0) return "无提交";
        bool hasBot = false, hasHuman = false;
        foreach (var c in commits)
        {
            if (IsBotCommit(c)) hasBot = true; else hasHuman = true;
        }
        if (hasBot && hasHuman) return "自动+人工";
        if (hasBot) return "自动处理";
        return "人工处理";
    }

    public static bool IsBotCommit(GitHubCommit c)
    {
        var name = (c.Commit?.Author?.Name ?? "").ToLowerInvariant();
        var login = (c.Author?.Login ?? "").ToLowerInvariant();
        var msg = (c.Commit?.Message ?? "").ToLowerInvariant();
        var botKeys = new[] { "github-actions", "[bot]", "actions-user", "dependabot", "renovate", "cron" };
        if (botKeys.Any(k => name.Contains(k) || login.Contains(k))) return true;
        if (name.Contains("action") || login.Contains("action")) return true;
        if (msg.Contains("cron") || msg.Contains("github actions")) return true;
        return false;
    }

    public static int CountDistinctDays(List<GitHubCommit> commits)
    {
        return commits
            .Select(c => c.Commit?.Author?.Date)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => DateTime.TryParse(d, out var dt) ? dt.Date : (DateTime?)null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .Distinct()
            .Count();
    }

    /// <summary>多因子活跃度 (0-100): 提交频率 35 + 均匀度 15 + 新鲜度 15 + 自动化 10 + 订阅验证 10 + 节点规模 10 + 隐蔽度 ±</summary>
    public static int CalculateScore(RepoInfo r)
    {
        int score = 0;
        // 提交频率 (0-35): 周提交 ≥9 次满分
        score += Math.Min(r.CommitsLast7Days, 9) * 35 / 9;
        // 更新均匀度 (0-15): 7 天里有多少天在更新
        if (r.DistinctActiveDays >= 6) score += 15;
        else if (r.DistinctActiveDays >= 4) score += 10;
        else if (r.DistinctActiveDays >= 2) score += 5;
        // 新鲜度 (0-15)
        if (r.DaysInactive <= 0) score += 15;
        else if (r.DaysInactive == 1) score += 10;
        else if (r.DaysInactive == 2) score += 6;
        else if (r.DaysInactive == 3) score += 3;
        // 自动化 (0-10)
        if (r.ProcessingType == "自动处理") score += 10;
        else if (r.ProcessingType == "自动+人工") score += 6;
        // 订阅验证 (0-10)
        if (r.Links.Any(l => l.NodeCount > 0)) score += 10;
        else if (r.Links.Count > 0) score += 4;
        // 节点规模 (0-10)
        if (r.TotalNodes >= 100) score += 10;
        else if (r.TotalNodes >= 30) score += 6;
        else if (r.TotalNodes >= 10) score += 3;
        // 隐蔽度 (-10 ~ +5)
        if (r.Stars < 50) score += 5;
        else if (r.Stars < 200) score += 3;
        else if (r.Stars > 8000) score -= 10;
        else if (r.Stars > 2000) score -= 5;
        return Math.Clamp(score, 0, 100);
    }

    public static string RelativeTime(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "?";
        if (DateTime.TryParse(iso, null, GitHubDateStyles, out var t))
        {
            var span = DateTime.UtcNow - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays}天前";
            return t.ToString("MM-dd");
        }
        return iso.Length > 10 ? iso[..10] : iso;
    }
}
