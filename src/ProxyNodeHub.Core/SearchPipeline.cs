using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

/// <summary>
/// 搜索 → 分析 → 结果聚合的完整管线。
///
/// 收口到 Core 的原因：桌面版、容器 CLI、Web 管理界面都要跑这一套，
/// 三处各写一份必然漂移（关键词列表、评分口径、过滤规则只要有一处改动
/// 就会不一致）。这里只做逻辑，不碰任何 UI 或输出格式 —— 谁来展示、
/// 展示成什么样子由调用方决定。
/// </summary>
public static class SearchPipeline
{
    /// <summary>
    /// 八个搜索关键词。与桌面版完全一致 —— 少一个都会让结果集缩水。
    /// </summary>
    public static string[] BuildQueries(int sinceDays)
    {
        var since = DateTime.UtcNow.AddDays(-sinceDays).ToString("yyyy-MM-dd");
        return new[]
        {
            $"v2ray nodes pushed:>{since}",
            $"free proxy subscription pushed:>{since}",
            $"clash nodes pushed:>{since}",
            $"免费节点 pushed:>{since}",
            $"v2ray 订阅 pushed:>{since}",
            $"free v2ray pushed:>{since}",
            $"trojan nodes pushed:>{since}",
            $"sing-box subscription pushed:>{since}"
        };
    }

    public class Options
    {
        public int TargetCount = 30;
        public int SinceDays = 7;
        public int Concurrency = 6;
        public bool SkipSearched;
        public int HistoryDays = 1;
        /// <summary>收藏仓库名，搜索时跳过（它们已在收藏夹里）。</summary>
        public HashSet<string> ExcludeRepos = new(StringComparer.OrdinalIgnoreCase);
    }

    public class Progress
    {
        /// <summary>搜索 / 分析 / 完成。</summary>
        public string Phase = "search";
        public int Done;
        public int Total;
        public string Message = "";
        /// <summary>分析阶段刚完成的仓库，便于调用方流式追加。</summary>
        public RepoInfo? Repo;
    }

    /// <summary>
    /// 跑完整管线：并发搜索 → 并发分析 → 记录搜索历史。
    /// onProgress 会在搜索与每个仓库分析完成时被调用。
    /// </summary>
    public static async Task<List<RepoInfo>> RunAsync(
        GitHubService github,
        Options opt,
        Action<Progress>? onProgress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        void Log(string m) => log?.Invoke(m);
        void Report(Progress p) { try { onProgress?.Invoke(p); } catch { } }

        var queries = BuildQueries(opt.SinceDays);
        Log($"搜索最近 {opt.SinceDays} 天更新的仓库 (目标 {opt.TargetCount} 个)…");
        Report(new Progress { Phase = "search", Total = queries.Length, Message = "搜索仓库中" });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<RepoInfo>();

        // 并发搜索：八个关键词同时打，与桌面版「极速/标准/深度」模式同构
        var results = await Task.WhenAll(queries.Select(q => github.SearchReposAsync(q, 30, ct)));
        int searched = 0;
        foreach (var list in results)
        {
            searched++;
            foreach (var r in list)
            {
                if (found.Count >= opt.TargetCount) break;
                if (seen.Contains(r.FullName)) continue;
                if (r.Fork) continue;
                if (opt.ExcludeRepos.Contains(r.FullName)) continue;
                if (opt.SkipSearched && SearchHistory.IsExcluded(r.FullName)) continue;
                if (GitHubAnalyzer.IsStale(r)) continue;

                seen.Add(r.FullName);
                found.Add(GitHubAnalyzer.ToRepoInfo(r));
            }
            Report(new Progress
            {
                Phase = "search", Done = searched, Total = queries.Length,
                Message = $"已找到 {found.Count} 个候选"
            });
        }

        Log($"共 {found.Count} 个候选仓库，开始分析…");
        Report(new Progress { Phase = "analyze", Total = found.Count, Message = "分析中" });

        int done = 0;
        var sem = new SemaphoreSlim(Math.Max(1, opt.Concurrency));
        await Task.WhenAll(found.Select(async repo =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await AnalyzeRepoAsync(github, repo, ct, log);
                var n = Interlocked.Increment(ref done);
                Report(new Progress
                {
                    Phase = "analyze", Done = n, Total = found.Count,
                    Message = repo.FullName, Repo = repo
                });
            }
            finally { sem.Release(); }
        }));

        if (opt.SkipSearched && found.Count > 0)
            SearchHistory.AddRepos(found.Select(r => r.FullName).ToList(), opt.HistoryDays);

        Report(new Progress { Phase = "done", Done = found.Count, Total = found.Count, Message = "完成" });
        return found;
    }

    /// <summary>分析单个仓库：提交历史 + 订阅链接探测 + 评分。</summary>
    public static async Task AnalyzeRepoAsync(
        GitHubService github, RepoInfo repo, CancellationToken ct, Action<string>? log = null)
    {
        void Log(string m) => log?.Invoke(m);
        Log($"┌─ {repo.FullName}");

        try
        {
            var commits = await github.GetRecentCommitsAsync(repo.FullName, 7, ct);
            repo.CommitsLast7Days = commits.Count;
            repo.DistinctActiveDays = GitHubAnalyzer.CountDistinctDays(commits);
            repo.ProcessingType = GitHubAnalyzer.DetectProcessingType(commits);
            repo.CommitsAnalyzed = true;
            Log($"│  ✓ 提交: {commits.Count} 次 (活跃 {repo.DistinctActiveDays} 天) [{repo.ProcessingType}]");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log($"│  ✗ 提交分析失败: {ex.Message}");
            repo.CommitsLast7Days = 0;
            repo.ProcessingType = "分析失败";
            repo.CommitsAnalyzed = true;
        }

        try
        {
            repo.Links = await SubscriptionFinder.FindLinksAsync(github, repo.FullName, ct, log, repo.Branch);
            repo.TotalNodes = GitHubAnalyzer.BestNodeCount(repo);
            var valid = repo.Links.Count(l => l.NodeCount > 0);
            Log(valid > 0
                ? $"│  ✓ 链接: {repo.Links.Count} 条 (有效 {valid}), 节点: {repo.TotalNodes}"
                : $"│  ○ 链接: {repo.Links.Count} 条 (均未验证到节点)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log($"│  ✗ 链接分析失败: {ex.Message}");
            repo.Links = new List<SubscriptionLink>();
            repo.TotalNodes = 0;
        }

        repo.Score = GitHubAnalyzer.CalculateScore(repo);
        Log($"└─ 完成 [评分: {repo.Score}]");
    }

    /// <summary>
    /// 桌面版「筛选」的等价实现。Web 端与 CLI 都用这一份，
    /// 保证三个界面对同一组数据给出相同的可见结果。
    /// </summary>
    public static IEnumerable<RepoInfo> ApplyFilters(
        IEnumerable<RepoInfo> source, int inactiveLimit, int minCommitsPerDay, int minActiveDays,
        bool autoClean, string? filterText)
    {
        var query = source.Where(r =>
        {
            if (autoClean && r.DaysInactive >= inactiveLimit) return false;
            if (minCommitsPerDay > 0 && r.CommitsAnalyzed && r.CommitsLast7Days / 7.0 < minCommitsPerDay) return false;
            if (minActiveDays > 0 && r.AgeDays < minActiveDays) return false;
            return true;
        });

        if (!string.IsNullOrWhiteSpace(filterText))
        {
            var t = filterText.Trim();
            query = query.Where(r =>
                r.FullName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (r.Description ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        return query;
    }

    /// <summary>
    /// 桌面版「合并订阅」的等价实现：抓取所有有效链接内容 → 去重合并。
    /// </summary>
    public static async Task<(string plain, string b64, int total, int unique)> MergeAsync(
        GitHubService github, IEnumerable<SubscriptionLink> links,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var list = links.Where(l => l.IsValid).ToList();
        var contents = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke(i + 1, list.Count);
            var content = await github.GetUrlAsync(list[i].Url, ct);
            if (!string.IsNullOrEmpty(content)) contents.Add(content);
        }
        return NodeParser.MergeNodes(contents);
    }
}
