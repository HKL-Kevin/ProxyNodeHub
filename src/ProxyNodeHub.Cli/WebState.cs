using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ProxyNodeHub;

namespace ProxyNodeHub.Cli;

/// <summary>
/// Web 界面的共享状态：仓库列表、日志、搜索与测速的生命周期。
///
/// 单例注入到所有 API 端点。搜索与测速各只有一个在跑（互斥），
/// 因为两者都吃满网络，且用户看到的界面也只有一套进度。
///
/// 日志走内存环形缓冲 + 订阅者推送（SSE），这样新打开的页面能立刻
/// 回填历史，而不会只看到连接之后的几行。
/// </summary>
public sealed class WebState
{
    private readonly object _gate = new();

    // ── 仓库 ──
    private List<RepoInfo> _repos = new();
    public IReadOnlyList<RepoInfo> Repos { get { lock (_gate) return _repos.ToList(); } }

    // ── 日志环形缓冲 ──
    private const int LogCap = 2000;
    private readonly Queue<LogLine> _log = new();
    public event Action<LogLine>? OnLog;

    // ── 搜索状态 ──
    private CancellationTokenSource? _searchCts;
    public bool Searching { get; private set; }
    public string SearchPhase { get; private set; } = "idle";
    public int SearchDone { get; private set; }
    public int SearchTotal { get; private set; }
    public string SearchMessage { get; private set; } = "";

    // ── 测速状态 ──
    private CancellationTokenSource? _speedCts;
    public bool SpeedTesting { get; private set; }
    public SpeedTestStatus? SpeedStatus { get; private set; }
    private List<SpeedTestNode> _nodes = new();
    public IReadOnlyList<SpeedTestNode> Nodes { get { lock (_gate) return _nodes.ToList(); } }
    public DateTime? SpeedFinishedAt { get; private set; }

    // ── 节点清单（只加载不测速）──
    private CancellationTokenSource? _catalogCts;
    public bool LoadingCatalog { get; private set; }
    public int CatalogDone { get; private set; }
    public int CatalogTotal { get; private set; }
    private List<NodeCatalog.NodeEntry> _catalog = new();
    public IReadOnlyList<NodeCatalog.NodeEntry> Catalog { get { lock (_gate) return _catalog.ToList(); } }
    public DateTime? CatalogLoadedAt { get; private set; }

    // ── 自动化流水线状态（搜索 ➔ 极速测活 ➔ 存活池入库）──
    private CancellationTokenSource? _pipelineCts;
    public bool PipelineRunning { get; private set; }
    public string PipelinePhase { get; private set; } = "idle";
    public string PipelineMessage { get; private set; } = "";

    // ── 设置 ──
    public AppSettings Settings => AppSettings.Load();

    public sealed record LogLine(DateTime At, string Text, string Level);

    // ══════════════════ 启动时回填：跨重启保留搜索/清单/测速结果 ══════════════════
    // 路径都在 UserDataDir（容器里 = /data，跟着命名卷走）。
    private static string StateDir => AppEnvironment.UserDataDir;
    private static string ReposPath => Path.Combine(StateDir, "web_repos.json");
    private static string CatalogPath => Path.Combine(StateDir, "web_catalog.json");
    private static string NodesPath => Path.Combine(StateDir, "web_nodes.json");
    private static string PassedYamlPath => Path.Combine(StateDir, "web_passed.yaml");

    /// <summary>缓存的最近一轮通过节点 Clash YAML，供导出端点用。
    /// SpeedTestRunner 在 finally 里被 Dispose，事后没法再调
    /// GetPassedSubscriptionAsync，所以测速完成时把 YAML 字符串存这里 + 落盘。</summary>
    private string? _lastPassedYaml;

    public string? GetPassedYaml()
    {
        // 优先返回内核原生生成的完整 YAML（含 uuid/password 等完整凭据）。
        // 绝不自行手写转换 —— SpeedTestNode 不含凭据，任何二次转换都会丢失 uuid。
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(_lastPassedYaml))
                return _lastPassedYaml;

            var kernelAllYaml = Path.Combine(SpeedTestConfig.OutputDir, "all.yaml");
            if (File.Exists(kernelAllYaml))
            {
                try
                {
                    var yaml = File.ReadAllText(kernelAllYaml);
                    if (!string.IsNullOrWhiteSpace(yaml))
                    {
                        yaml = SpeedTestRunner.FormatPassedYaml(yaml, _nodes);
                        _lastPassedYaml = yaml;
                        return yaml;
                    }
                }
                catch { }
            }

            if (File.Exists(PassedYamlPath))
            {
                try
                {
                    var yaml = File.ReadAllText(PassedYamlPath);
                    if (!string.IsNullOrWhiteSpace(yaml))
                    {
                        yaml = SpeedTestRunner.FormatPassedYaml(yaml, _nodes);
                        _lastPassedYaml = yaml;
                        return yaml;
                    }
                }
                catch { }
            }

            return null;
        }
    }

    /// <summary>
    /// 启动时从盘上把上次的状态读回来，让重建容器后页面不是空的。
    ///
    /// 为什么自己存一份 web_repos.json 而不直接用 ResultCache：
    /// ResultCache.cache.json 是桌面版的历史格式（含 SavedAt 时间戳包裹），
    /// Web 版直接读写同一份会让两边互相覆盖；Web 自己存一份独立文件，
    /// 桌面版不影响，且结构就是 List&lt;RepoInfo&gt;，更简单。
    /// </summary>
    public void RestoreFromDisk()
    {
        try
        {
            if (File.Exists(ReposPath))
            {
                var repos = JsonSerializer.Deserialize(File.ReadAllText(ReposPath),
                    WebJsonContext.Default.WebRepoList);
                if (repos != null) { lock (_gate) _repos = repos; }
            }
        }
        catch { }

        try
        {
            if (File.Exists(CatalogPath))
            {
                var cat = JsonSerializer.Deserialize(File.ReadAllText(CatalogPath),
                    WebJsonContext.Default.WebNodeEntryList);
                if (cat != null && cat.Count > 0)
                {
                    lock (_gate) { _catalog = cat; CatalogLoadedAt = DateTime.Now; }
                }
            }
        }
        catch { }

        try
        {
            if (File.Exists(NodesPath))
            {
                var snap = JsonSerializer.Deserialize(File.ReadAllText(NodesPath),
                    WebJsonContext.Default.WebSpeedTestSnapshot);
                if (snap?.Nodes != null && snap.Nodes.Count > 0)
                {
                    lock (_gate) _nodes = snap.Nodes;
                    SpeedFinishedAt = snap.CheckedAt;
                }
            }
        }
        catch { }

        try
        {
            var kernelAllYaml = Path.Combine(SpeedTestConfig.OutputDir, "all.yaml");
            if (File.Exists(kernelAllYaml))
            {
                var yaml = File.ReadAllText(kernelAllYaml);
                if (!string.IsNullOrWhiteSpace(yaml))
                {
                    yaml = SpeedTestRunner.FormatPassedYaml(yaml, _nodes);
                    lock (_gate) _lastPassedYaml = yaml;
                }
            }
            else if (File.Exists(PassedYamlPath))
            {
                var yaml = File.ReadAllText(PassedYamlPath);
                if (!string.IsNullOrWhiteSpace(yaml))
                {
                    yaml = SpeedTestRunner.FormatPassedYaml(yaml, _nodes);
                    lock (_gate) _lastPassedYaml = yaml;
                }
            }
        }
        catch { }

        try
        {
            AliveNodeStore.Initialize();
        }
        catch { }
    }

    private void SaveRepos()
    {
        try
        {
            List<RepoInfo> snap;
            lock (_gate) snap = _repos.ToList();
            // 不要用空列表覆盖上次的有数据结果 ——
            // 进程被 SIGTERM / 异常路径把 _repos 重置成空时，
            // 盘上的数据是唯一的恢复来源，盖掉就永久丢了。
            if (snap.Count == 0) return;
            AtomicFile.Write(ReposPath, JsonSerializer.Serialize(snap, WebJsonContext.Default.WebRepoList));
        }
        catch { }
    }

    private void SaveCatalog()
    {
        try
        {
            List<NodeCatalog.NodeEntry> snap;
            lock (_gate) snap = _catalog.ToList();
            // 同 SaveRepos：空清单不写盘，避免覆盖上次的有效清单。
            if (snap.Count == 0) return;
            AtomicFile.Write(CatalogPath, JsonSerializer.Serialize(snap, WebJsonContext.Default.WebNodeEntryList));
        }
        catch { }
    }

    private void SaveNodes(SpeedTestSnapshot snap, string? kernelYaml)
    {
        try
        {
            snap.CheckedAt = DateTime.Now;
            AtomicFile.Write(NodesPath, JsonSerializer.Serialize(snap, WebJsonContext.Default.WebSpeedTestSnapshot));

            // 使用内核原生生成的完整 YAML（含 uuid/password 等凭据），供导出端点用。
            // 绝不自行手写转换 —— SpeedTestNode 不含凭据字段，转换必丢 uuid。
            var yaml = kernelYaml;
            if (string.IsNullOrWhiteSpace(yaml))
            {
                var kernelAllYaml = Path.Combine(SpeedTestConfig.OutputDir, "all.yaml");
                if (File.Exists(kernelAllYaml))
                {
                    try { yaml = File.ReadAllText(kernelAllYaml); } catch { }
                }
            }

            if (!string.IsNullOrWhiteSpace(yaml))
            {
                yaml = SpeedTestRunner.FormatPassedYaml(yaml, snap.Nodes);
                lock (_gate) _lastPassedYaml = yaml;
                AtomicFile.Write(PassedYamlPath, yaml);

                // 同时持久化至 SQLite 强一致存活池（累加存活轮数、更新健康画像）
                try { AliveNodeStore.SaveRound(yaml, snap.Nodes); } catch { }
            }
        }
        catch { }
    }

    public void Log(string text, string level = "info")
    {
        var line = new LogLine(DateTime.Now, text, level);
        lock (_gate)
        {
            _log.Enqueue(line);
            while (_log.Count > LogCap) _log.Dequeue();
        }
        try { OnLog?.Invoke(line); } catch { }
    }

    public List<LogLine> LogSnapshot()
    {
        lock (_gate) return _log.ToList();
    }

    public void ClearLog()
    {
        lock (_gate) _log.Clear();
    }

    // ══════════════════ 搜索 ══════════════════

    /// <summary>
    /// 确保 GitHubService 有一个可用的加速镜像。
    ///
    /// 直连 raw.githubusercontent.com 在受限网络下基本不可用，而
    /// GetUrlAsync 的兜底代理只挑「测过速且可用」的镜像（LatencyMs &gt; 0）。
    /// 没先测速的话兜底列表是空的，直连失败就彻底失败 —— 清单加载踩过这个坑。
    /// </summary>
    private async Task EnsureProxyAsync(GitHubService github, CancellationToken ct)
    {
        if (github.CurrentProxy is { IsDefault: false }) return;

        Log("探测加速镜像…");
        var sorted = await GitHubService.TestAllProxiesAsync(ct);
        var best = sorted.FirstOrDefault(p => p.LatencyMs > 0);
        if (best != null)
        {
            github.CurrentProxy = best;
            Log($"使用镜像: {best.Name} ({best.LatencyMs}ms)");
        }
        else
        {
            // 全不可用时保持直连，但仍把测速结果留在 AllProxies 里
            // （LatencyMs 已更新），GetUrlAsync 的兜底会按延迟顺序重试。
            Log("所有镜像均不可用，使用直连。", "warn");
        }
    }

    public bool StartSearch(int count, int sinceDays, int concurrency, bool skipSearched,
        string? proxyName, bool autoProxy, string? token)
    {
        lock (_gate)
        {
            if (Searching) return false;
            Searching = true;
            SearchPhase = "starting";
            SearchDone = 0;
            SearchTotal = 0;
            SearchMessage = "准备中";
            _searchCts = new CancellationTokenSource();
        }

        var ct = _searchCts!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSearchAsync(count, sinceDays, concurrency, skipSearched, proxyName, autoProxy, token, ct);
            }
            catch (OperationCanceledException)
            {
                Log("[取消] 搜索已取消", "warn");
                lock (_gate) SearchPhase = "cancelled";
            }
            catch (Exception ex)
            {
                Log($"搜索失败: {ex.Message}", "error");
                lock (_gate) SearchPhase = "error";
            }
            finally
            {
                lock (_gate)
                {
                    Searching = false;
                    if (SearchPhase is not ("cancelled" or "error")) SearchPhase = "idle";
                }
            }
        }, ct);

        return true;
    }

    private async Task RunSearchAsync(int count, int sinceDays, int concurrency, bool skipSearched,
        string? proxyName, bool autoProxy, string? token, CancellationToken ct)
    {
        var settings = Settings;
        var effectiveToken = token ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                             ?? settings.GetToken();
        var github = new GitHubService(string.IsNullOrEmpty(effectiveToken) ? null : effectiveToken);

        Log(github.HasToken
            ? "GitHub Token: 已设置 (限额 5000 次/小时)"
            : "GitHub Token: 未设置 (匿名限额 60 次/小时，建议在设置里填写)");

        if (!string.IsNullOrEmpty(proxyName))
        {
            var match = GitHubService.AllProxies.FirstOrDefault(p =>
                p.Name.Contains(proxyName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                github.CurrentProxy = match;
                Log($"使用代理: {match.Name}");
            }
        }
        else if (autoProxy)
        {
            await EnsureProxyAsync(github, ct);
        }

        var favorites = new HashSet<string>(
            FavoritesStore.Load().Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);

        var found = await SearchPipeline.RunAsync(github, new SearchPipeline.Options
        {
            TargetCount = count,
            SinceDays = sinceDays,
            Concurrency = concurrency,
            SkipSearched = skipSearched,
            ExcludeRepos = favorites,
        }, progress =>
        {
            lock (_gate)
            {
                SearchPhase = progress.Phase;
                SearchDone = progress.Done;
                SearchTotal = progress.Total;
                SearchMessage = progress.Message;
            }
        }, log: m => Log(m), ct);

        // 搜索阶段拉不到内容 / 解析不出节点的仓库，链接 nodeCount=-1（未验证）或
        // totalNodes=0。这些仓库对清单加载和测速毫无价值（拉过来还是 0），
        // 留着只占界面、拖慢后续筛选。这里直接从结果里剔除，下次搜索靠
        // searched_repos.json 的历史过滤自然不再命中。
        var liveRepos = found.Where(r => r.TotalNodes > 0).ToList();
        var dropped = found.Count - liveRepos.Count;
        if (dropped > 0)
            Log($"已剔除 {dropped} 个无节点仓库（订阅 404 或解析为空）");
        lock (_gate) _repos = liveRepos;
        SaveRepos();
        Log($"完成: {liveRepos.Count} 个仓库，全部验证到节点", "success");
    }

    public void CancelSearch()
    {
        lock (_gate) _searchCts?.Cancel();
    }

    // ══════════════════ 节点清单（只加载，不测速）══════════════════

    /// <summary>
    /// 拉取并解析订阅，把节点列出来。不启动内核、不产生任何测速流量。
    /// </summary>
    public bool StartCatalogLoad(List<SubSource> sources, string? token)
    {
        lock (_gate)
        {
            if (LoadingCatalog) return false;
            LoadingCatalog = true;
            CatalogDone = 0;
            CatalogTotal = sources.Count;
            _catalogCts = new CancellationTokenSource();
        }

        var ct = _catalogCts!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var settings = Settings;
                var tk = token ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                         ?? settings.GetToken();
                var github = new GitHubService(string.IsNullOrEmpty(tk) ? null : tk);

                // 直连 raw.githubusercontent.com 在受限网络下会失败。
                // GetUrlAsync 的兜底代理只挑「测过速且可用」的镜像，而清单加载
                // 没跑过代理测速（LatencyMs 全是 -1），兜底列表为空 → 直连失败
                // 就整体失败。这里先探一次，把可用镜像填进去。
                await EnsureProxyAsync(github, ct);

                Log($"加载节点清单 · {sources.Count} 个订阅来源…");
                if (string.IsNullOrWhiteSpace(settings.SubConverterUrl))
                    Log("⚠  未配置 SubConverterUrl，纯 Clash YAML / sing-box JSON 节点行 / "
                        + "新协议（hysteria2/anytls/socks 等）将识别不出。请在设置里填写 minisubconvert 端点。");
                var nodes = await NodeCatalog.LoadAsync(github, sources, (i, n) =>
                {
                    lock (_gate) { CatalogDone = i; CatalogTotal = n; }
                }, log: m => Log(m), ct,
                subConverterUrl: settings.SubConverterUrl);

                lock (_gate)
                {
                    _catalog = nodes;
                    CatalogLoadedAt = DateTime.Now;
                }
                SaveCatalog();
                Log($"清单就绪: {nodes.Count} 个节点（去重后）", "success");
            }
            catch (OperationCanceledException)
            {
                Log("[取消] 清单加载已取消", "warn");
            }
            catch (Exception ex)
            {
                Log($"清单加载失败: {ex.Message}", "error");
            }
            finally
            {
                lock (_gate) LoadingCatalog = false;
            }
        }, ct);

        return true;
    }

    public void CancelCatalogLoad()
    {
        lock (_gate) _catalogCts?.Cancel();
    }

    /// <summary>
    /// 按协议/来源/关键词筛选清单。协议与来源都是多选（空集 = 不限制）。
    ///
    /// 关键词按正则解释，作用于名称、服务器、来源三个字段。正则非法时
    /// 退化为子串匹配而不是报错 —— 用户输入 "[2001:db8" 这类含特殊字符的
    /// 片段是常事，直接让筛选失效比退化为文本匹配糟糕得多。
    /// </summary>
    public (List<NodeCatalog.NodeEntry> nodes, string mode, string? error) FilterCatalog(
        string? keyword, List<string>? protocols, List<string>? repos)
    {
        IEnumerable<NodeCatalog.NodeEntry> q = Catalog;

        if (protocols is { Count: > 0 })
            q = q.Where(n => protocols.Contains(n.Protocol, StringComparer.OrdinalIgnoreCase));

        if (repos is { Count: > 0 })
            q = q.Where(n => repos.Contains(n.Repo, StringComparer.OrdinalIgnoreCase));

        var mode = "none";
        string? error = null;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = keyword.Trim();

            try
            {
                // 200ms 匹配上限：用户可能误写灾难性回溯的正则
                // （如 (a+)+$），没有超时会把请求线程卡死。
                var rx = new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
                q = q.Where(n => SafeIsMatch(rx, n));
                mode = "regex";
            }
            catch (ArgumentException ex)
            {
                q = q.Where(n => Contains(n, pattern));
                mode = "literal";
                error = ex.Message;
            }
        }

        return (q.ToList(), mode, error);
    }

    private static bool SafeIsMatch(Regex rx, NodeCatalog.NodeEntry n)
    {
        try
        {
            return rx.IsMatch(n.Name) || rx.IsMatch(n.Server) || rx.IsMatch(n.Repo);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;   // 回溯超时视为不匹配，不让整个请求失败
        }
    }

    private static bool Contains(NodeCatalog.NodeEntry n, string s)
        => n.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
        || n.Server.Contains(s, StringComparison.OrdinalIgnoreCase)
        || n.Repo.Contains(s, StringComparison.OrdinalIgnoreCase);

    /// <summary>清单里出现过的协议与来源，供界面做筛选下拉。</summary>
    public (List<string> protocols, List<string> repos) CatalogFacets()
    {
        var nodes = Catalog;
        return (
            nodes.Select(n => n.Protocol).Where(p => p.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList(),
            nodes.Select(n => n.Repo).Where(r => r.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r).ToList()
        );
    }

    // ══════════════════ 测速 ══════════════════

    /// <summary>
    /// 只测指定的节点 URI。与 StartSpeedTest 的区别：不接管上游订阅，
    /// 而是把选中的节点作为本地订阅注入，内核因此只测这一批。
    /// </summary>
    public bool StartSpeedTestNodes(List<string> nodeUris, bool withHistory, int keepRounds)
    {
        if (nodeUris.Count == 0) return false;

        lock (_gate)
        {
            if (SpeedTesting) return false;
            SpeedTesting = true;
            SpeedStatus = null;
            _speedCts = new CancellationTokenSource();
        }

        var ct = _speedCts!.Token;
        _ = Task.Run(async () =>
        {
            SpeedTestRunner? runner = null;
            try
            {
                var kernel = SpeedTestConfig.KernelPath;
                if (!File.Exists(kernel))
                {
                    Log($"未找到测速内核: {kernel}", "error");
                    return;
                }

                SpeedTestConfig.EnsureDirs();
                if (!File.Exists(SpeedTestConfig.ConfigPath))
                {
                    AtomicFile.Write(SpeedTestConfig.ConfigPath, SpeedTestDefaults.DefaultYaml);
                    Log($"已生成默认配置: {SpeedTestConfig.ConfigPath}");
                }

                Log($"开始测速 · 选中 {nodeUris.Count} 个节点 · " +
                    $"内核 {SubCheckKernel.Display(SubCheckKernel.LocalVersion())}");

                runner = new SpeedTestRunner();
                runner.OnLog += line => Log(line, KernelLevel(line));

                await runner.PrepareAsync(new List<string>(), withHistory, keepRounds, nodeUris);
                await runner.StartAsync(m => Log(m), ct);
                await runner.TriggerAsync(ct);

                await PollUntilDoneAsync(runner, ct);
                await CollectResultsAsync(runner, ct);
            }
            catch (OperationCanceledException)
            {
                Log("[取消] 测速已取消", "warn");
            }
            catch (Exception ex)
            {
                Log($"测速失败: {ex.Message}", "error");
            }
            finally
            {
                if (runner != null)
                {
                    try { await runner.StopAsync(); } catch { }
                    runner.Dispose();
                }
                lock (_gate)
                {
                    SpeedTesting = false;
                    SpeedStatus = null;
                }
            }
        }, ct);

        return true;
    }

    private static string KernelLevel(string line)
        => line.Contains("WARN") ? "warn"
         : line.Contains("ERROR") || line.Contains("error") ? "error"
         : "kernel";

    /// <summary>
    /// 轮询内核直到一轮测速结束。
    ///
    /// 判据是「Checking 由 true 翻回 false」而不是进度达标：后者在订阅为空
    /// 或全部节点未通过测活时永远不成立，会一直空转到超时。
    /// 另一个退出条件是状态接口连续取不到（内核已收工或进程退出）。
    /// </summary>
    private async Task PollUntilDoneAsync(SpeedTestRunner runner, CancellationToken ct)
    {
        bool sawChecking = false;
        int nullStreak = 0;
        var deadline = DateTime.UtcNow.AddHours(3);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1500, ct);
            var st = await runner.GetStatusAsync(ct);
            if (st == null)
            {
                if (++nullStreak >= 6) return;
                continue;
            }
            nullStreak = 0;
            lock (_gate) SpeedStatus = st;

            // 实时同步：测速过程中定期拉 /api/results，把已通过的节点灌进
            // state._nodes，前端 pollState 检测到 nodeCount 变化就能实时刷新
            // 列表和地图。内核 /api/results 在测速进行中也会返回当前已完成
            // 测速的节点（passed = speed > 0），不是非要等整轮结束。
            if (sawChecking)
            {
                try
                {
                    var live = await runner.GetResultsAsync(ct);
                    if (live?.Nodes.Count > 0)
                    {
                        // 只更新有速度数据的节点（避免覆盖完整结果的 IP 信息）
                        var passed = live.Nodes.Where(n => n.Speed > 0).ToList();
                        if (passed.Count > 0)
                        {
                            lock (_gate) _nodes = live.Nodes;
                            // 实时触发前端刷新（SpeedFinishedAt 不动，
                            // 前端靠 nodeCount 变化判断）
                        }
                    }
                }
                catch { /* 中途拉取失败不中断测速 */ }
            }

            if (st.Checking) sawChecking = true;
            else if (sawChecking) return;
        }
    }

    private async Task CollectResultsAsync(SpeedTestRunner runner, CancellationToken ct)
    {
        var snap = await runner.GetResultsAsync(CancellationToken.None);
        if (snap?.Nodes.Count > 0)
        {
            // 趁内核还在运行，从 /sub/all.yaml 拿完整的节点 YAML（含 uuid/password 等凭据）。
            var kernelYaml = await runner.GetPassedSubscriptionAsync(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(kernelYaml))
            {
                var diskPath = Path.Combine(SpeedTestConfig.OutputDir, "all.yaml");
                if (File.Exists(diskPath))
                {
                    try { kernelYaml = File.ReadAllText(diskPath); } catch { }
                }
            }

            // 内核可能没填 IP（media-check 关了）；只对「通过测速」的节点查 IP 详情
            // —— 未通过的节点不会进导出，查了也是浪费 ipinfo.io 额度
            var token = Settings.IpInfoToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                var ips = snap.Nodes.Where(n => n.Passed && !string.IsNullOrWhiteSpace(n.Ip))
                    .Select(n => n.Ip!).Distinct().ToList();
                if (ips.Count > 0)
                {
                    Log($"查询 {ips.Count} 个 IP 的详细信息…");
                    // 用 CancellationToken.None 而非 ct：IP 查询是测速完成后的
                    // 补充步骤，用户点取消时不应中断它（测速结果已经拿到了，
                    // 中途取消 IP 查询会让城市/经纬度全空，白跑一轮测速）。
                    var ipInfo = await IpInfoService.LookupAsync(ips, token, m => Log(m),
                        CancellationToken.None);
                    // 把查到的城市/ASN/ISP 灌回节点。Name/BaseName 保持内核原值不动，
                    // 展示用 DisplayName 属性统一截断 | + 插城市。
                    foreach (var n in snap.Nodes)
                    {
                        if (n.Ip != null && ipInfo.TryGetValue(n.Ip, out var info))
                        {
                            n.IpInfoCity = info.City;
                            n.IpInfoRegion = info.Region;
                            n.IpInfoOrg = info.Org;
                            n.IpInfoAsn = info.Asn;
                            n.IpInfoLoc = info.Loc;
                        }
                    }
                }
            }

            lock (_gate) _nodes = snap.Nodes;
            var changed = SpeedResultAggregator.ApplyRound(snap);
            runner.SaveRound(snap, kernelYaml);
            SaveNodes(snap, kernelYaml);
            Log($"测速完成: {snap.Nodes.Count} 个节点，{snap.Nodes.Count(n => n.Passed)} 个通过" +
                (changed > 0 ? $" · 已更新 {changed} 个仓库的可用分" : ""), "success");
        }
        else
        {
            Log("内核未返回节点结果", "warn");
        }
        lock (_gate) SpeedFinishedAt = DateTime.Now;
    }

    /// <summary>
    /// 测速全部上游订阅（大流量，谨慎用）。几千个节点每个要真实下载
    /// 20MB，可能跑几小时 —— 界面默认走 StartSpeedTestNodes 让用户先选。
    /// </summary>
    public bool StartSpeedTest(List<SubSource> sources, bool withHistory, int keepRounds)
    {
        lock (_gate)
        {
            if (SpeedTesting) return false;
            SpeedTesting = true;
            SpeedStatus = null;
            _speedCts = new CancellationTokenSource();
        }

        var ct = _speedCts!.Token;
        _ = Task.Run(async () =>
        {
            SpeedTestRunner? runner = null;
            try
            {
                var kernel = SpeedTestConfig.KernelPath;
                if (!File.Exists(kernel))
                {
                    Log($"未找到测速内核: {kernel}", "error");
                    return;
                }

                SpeedTestConfig.EnsureDirs();
                if (!File.Exists(SpeedTestConfig.ConfigPath))
                {
                    AtomicFile.Write(SpeedTestConfig.ConfigPath, SpeedTestDefaults.DefaultYaml);
                    Log($"已生成默认配置: {SpeedTestConfig.ConfigPath}");
                }

                Log($"开始测速 · {sources.Count} 个订阅来源 · " +
                    $"内核 {SubCheckKernel.Display(SubCheckKernel.LocalVersion())}");

                runner = new SpeedTestRunner();
                runner.OnLog += line => Log(line, KernelLevel(line));

                await runner.PrepareAsync(
                    SubSourceHelper.ToTaggedUrls(sources), withHistory, keepRounds);
                await runner.StartAsync(m => Log(m), ct);
                await runner.TriggerAsync(ct);

                await PollUntilDoneAsync(runner, ct);
                await CollectResultsAsync(runner, ct);
            }
            catch (OperationCanceledException)
            {
                Log("[取消] 测速已取消", "warn");
            }
            catch (Exception ex)
            {
                Log($"测速失败: {ex.Message}", "error");
            }
            finally
            {
                if (runner != null)
                {
                    try { await runner.StopAsync(); } catch { }
                    runner.Dispose();
                }
                lock (_gate)
                {
                    SpeedTesting = false;
                    SpeedStatus = null;
                }
            }
        }, ct);

        return true;
    }

    public void CancelSpeedTest()
    {
        lock (_gate) _speedCts?.Cancel();
    }

    // ══════════════════ 合并导出 ══════════════════

    /// <summary>
    /// 把当前仓库的所有有效链接合并去重。返回 (base64, 明文, 原始数, 唯一数)。
    /// </summary>
    public async Task<(string b64, string plain, int total, int unique)> MergeAsync(
        string? token, CancellationToken ct = default)
    {
        var settings = Settings;
        var effectiveToken = token ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                             ?? settings.GetToken();
        var github = new GitHubService(string.IsNullOrEmpty(effectiveToken) ? null : effectiveToken);

        List<RepoInfo> repos;
        lock (_gate) repos = _repos.ToList();

        var links = repos.SelectMany(r => r.Links).Where(l => l.IsValid).ToList();
        Log($"合并 {links.Count} 条链接…");

        var result = await SearchPipeline.MergeAsync(github, links,
            (i, n) => { if (i % 10 == 0 || i == n) Log($"  合并进度 {i}/{n}"); }, ct,
            subConverterUrl: settings.SubConverterUrl);

        Log($"去重: 原始 {result.total} 节点 → 唯一 {result.unique} 个", "success");
        return (result.b64, result.plain, result.total, result.unique);
    }

    // ══════════════════ 全自动化流水线（搜索 ➔ 极速测活 ➔ 存活池入库） ══════════════════

    /// <summary>
    /// 全自动化流水线：
    /// 1. 自动搜索活跃仓库并探测订阅链接；
    /// 2. 自动拉取并合并去重为候选节点；
    /// 3. 启动轻量内核执行「仅测活」（跳过流媒体与大流量下载测速，仅发 0 字节 204 探针）；
    /// 4. 将测活通过的节点原子入库到 SQLite 存活池（nodes.db），累加连续存活轮数。
    /// </summary>
    public bool StartAutoPipeline(int count, int days, int concurrency, bool skipSearched, int historyDays, string? token)
    {
        lock (_gate)
        {
            if (PipelineRunning || Searching || SpeedTesting) return false;
            PipelineRunning = true;
            PipelinePhase = "searching";
            PipelineMessage = "正在启动自动化流水线…";
            _pipelineCts = new CancellationTokenSource();
        }

        var ct = _pipelineCts!.Token;
        _ = Task.Run(async () =>
        {
            SpeedTestRunner? runner = null;
            try
            {
                Log("══════════════════ [自动化流水线] 启动 ══════════════════", "info");
                var settings = Settings;
                var tk = token ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? settings.GetToken();
                var github = new GitHubService(string.IsNullOrEmpty(tk) ? null : tk);

                // 阶段 1: 搜索仓库
                lock (_gate)
                {
                    PipelinePhase = "searching";
                    PipelineMessage = "阶段 1/3: 正在搜索 GitHub 仓库与探测订阅…";
                }
                Log("[流水线 1/3] 正在搜索 GitHub 仓库…");
                await EnsureProxyAsync(github, ct);

                var favorites = new HashSet<string>(
                    FavoritesStore.Load().Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);

                var found = await SearchPipeline.RunAsync(github, new SearchPipeline.Options
                {
                    TargetCount = count,
                    SinceDays = days,
                    Concurrency = concurrency,
                    SkipSearched = skipSearched,
                    HistoryDays = historyDays,
                    ExcludeRepos = favorites,
                },
                progress =>
                {
                    lock (_gate)
                    {
                        SearchPhase = progress.Phase;
                        SearchDone = progress.Done;
                        SearchTotal = progress.Total;
                        SearchMessage = progress.Message;
                        PipelineMessage = $"阶段 1/3: [{progress.Phase}] {progress.Message} ({progress.Done}/{progress.Total})";
                    }
                },
                log: line => Log(line),
                ct: ct);

                var liveRepos = found.Where(r => r.TotalNodes > 0).ToList();
                lock (_gate) _repos = liveRepos;
                SaveRepos();

                var speedTestSources = new List<SubSource>();

                // 1. 本次搜索出的有效仓库订阅
                foreach (var r in liveRepos)
                {
                    foreach (var l in r.Links.Where(l => l.IsValid && l.NodeCount > 0))
                    {
                        speedTestSources.Add(new SubSource { Url = l.Url, Repo = r.FullName });
                    }
                }

                // 2. 预存常用订阅 (Settings.SubUrls)
                if (settings.SubUrls is { Count: > 0 })
                {
                    foreach (var sub in settings.SubUrls)
                    {
                        if (!string.IsNullOrWhiteSpace(sub.Url))
                        {
                            speedTestSources.Add(new SubSource { Url = sub.Url.Trim(), Repo = $"常用订阅:{sub.Name}" });
                        }
                    }
                }

                // 3. 收藏夹中的仓库订阅 (Favorites)
                foreach (var r in FavoritesStore.Load())
                {
                    foreach (var l in r.Links.Where(l => l.IsValid && l.NodeCount > 0))
                    {
                        speedTestSources.Add(new SubSource { Url = l.Url, Repo = $"收藏:{r.FullName}" });
                    }
                }

                if (speedTestSources.Count == 0)
                {
                    Log("[流水线] 未发现有效订阅来源，流水线终止", "warn");
                    return;
                }

                // 阶段 2: 提取并聚合候选节点
                lock (_gate)
                {
                    PipelinePhase = "aggregating";
                    PipelineMessage = "阶段 2/3: 正在提取与合并订阅节点…";
                }
                Log($"[流水线 2/3] 从 {speedTestSources.Count} 个订阅源提取候选节点…");
                var candidateEntries = await NodeCatalog.LoadAsync(github, speedTestSources,
                    (done, total) => { },
                    log: m => Log(m),
                    ct: ct,
                    subConverterUrl: settings.SubConverterUrl
                );

                if (candidateEntries.Count == 0)
                {
                    Log("[流水线] 未能解析出任何候选节点，流水线终止", "warn");
                    return;
                }

                lock (_gate)
                {
                    _catalog = candidateEntries;
                    CatalogLoadedAt = DateTime.Now;
                }
                SaveCatalog();
                Log($"[流水线] 聚合完成: 获得 {candidateEntries.Count} 个候选节点", "success");

                // 阶段 3: 极速仅测活 (Alive-Only)
                lock (_gate)
                {
                    PipelinePhase = "alive_testing";
                    PipelineMessage = $"阶段 3/3: 正在对 {candidateEntries.Count} 个节点执行极速测活（0 字节探针）…";
                }
                Log($"[流水线 3/3] 启动极速测活（并发 50，跳过大流量测速与媒体检测）…");

                var candidateUris = candidateEntries.Select(e => e.Uri).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();

                var kernel = SpeedTestConfig.KernelPath;
                if (!File.Exists(kernel))
                {
                    Log($"未找到测速内核: {kernel}", "error");
                    return;
                }

                SpeedTestConfig.EnsureDirs();
                if (!File.Exists(SpeedTestConfig.ConfigPath))
                {
                    AtomicFile.Write(SpeedTestConfig.ConfigPath, SpeedTestDefaults.DefaultYaml);
                }

                runner = new SpeedTestRunner();
                runner.OnLog += line => Log(line, KernelLevel(line));

                // 传入 aliveOnly: true
                await runner.PrepareAsync(new List<string>(), withHistory: false, keepRounds: 0,
                    nodeUris: candidateUris, aliveOnly: true);
                await runner.StartAsync(m => Log(m), ct);
                await runner.TriggerAsync(ct);

                await PollUntilDoneAsync(runner, ct);

                // 收集结果并入库 SQLite
                var snap = await runner.GetResultsAsync(CancellationToken.None);
                var kernelYaml = await runner.GetPassedSubscriptionAsync(CancellationToken.None);
                if (string.IsNullOrWhiteSpace(kernelYaml))
                {
                    var diskPath = Path.Combine(SpeedTestConfig.OutputDir, "all.yaml");
                    if (File.Exists(diskPath))
                    {
                        try { kernelYaml = File.ReadAllText(diskPath); } catch { }
                    }
                }

                if (!string.IsNullOrWhiteSpace(kernelYaml))
                {
                    var (inserted, updated) = AliveNodeStore.SaveRound(kernelYaml, snap?.Nodes);
                    var stats = AliveNodeStore.GetStats();
                    Log($"══════════════════ [自动化流水线] 完成 ══════════════════", "success");
                    Log($"✓ 存活池已更新: 新增 {inserted} 个，更新 {updated} 个 · 当前存活池总计 {stats.TotalCount} 个可用节点", "success");
                }
                else
                {
                    Log("[流水线] 本轮测活未发现可用存活节点", "warn");
                }
            }
            catch (OperationCanceledException)
            {
                Log("[取消] 自动化流水线已取消", "warn");
            }
            catch (Exception ex)
            {
                Log($"[流水线失败] {ex.Message}", "error");
            }
            finally
            {
                if (runner != null)
                {
                    try { await runner.StopAsync(); } catch { }
                    runner.Dispose();
                }
                lock (_gate)
                {
                    PipelineRunning = false;
                    PipelinePhase = "idle";
                    PipelineMessage = "";
                }
            }
        }, ct);

        return true;
    }

    public void CancelAutoPipeline()
    {
        lock (_gate) _pipelineCts?.Cancel();
    }

    /// <summary>
    /// 从 SQLite 存活池中读取所有可用节点，直接启动完整测速（测带宽、测流媒体、查 IP）。
    /// 不再测未经筛选的数千个废节点，测速时间缩短 90% 以上。
    /// </summary>
    public bool StartSpeedTestFromAlivePool(bool withHistory, int keepRounds)
    {
        var aliveYaml = AliveNodeStore.ExportClashYaml();
        if (string.IsNullOrWhiteSpace(aliveYaml) || aliveYaml == "proxies: []\n")
        {
            Log("存活池为空，请先运行自动化搜索测活流水线生成存活池。", "warn");
            return false;
        }

        lock (_gate)
        {
            if (SpeedTesting || PipelineRunning) return false;
            SpeedTesting = true;
            SpeedStatus = null;
            _speedCts = new CancellationTokenSource();
        }

        var ct = _speedCts!.Token;
        _ = Task.Run(async () =>
        {
            SpeedTestRunner? runner = null;
            try
            {
                var kernel = SpeedTestConfig.KernelPath;
                if (!File.Exists(kernel))
                {
                    Log($"未找到测速内核: {kernel}", "error");
                    return;
                }

                SpeedTestConfig.EnsureDirs();
                if (!File.Exists(SpeedTestConfig.ConfigPath))
                {
                    AtomicFile.Write(SpeedTestConfig.ConfigPath, SpeedTestDefaults.DefaultYaml);
                }

                var stats = AliveNodeStore.GetStats();
                Log($"开始对存活池测速 · 输入存活节点 {stats.TotalCount} 个 · " +
                    $"内核 {SubCheckKernel.Display(SubCheckKernel.LocalVersion())}");

                runner = new SpeedTestRunner();
                runner.OnLog += line => Log(line, KernelLevel(line));

                // customYamlBody 传入存活池中的 Clash YAML，aliveOnly: false 跑完整测速
                await runner.PrepareAsync(new List<string>(), withHistory, keepRounds,
                    nodeUris: null, aliveOnly: false, customYamlBody: aliveYaml);
                await runner.StartAsync(m => Log(m), ct);
                await runner.TriggerAsync(ct);

                await PollUntilDoneAsync(runner, ct);
                await CollectResultsAsync(runner, ct);
            }
            catch (OperationCanceledException)
            {
                Log("[取消] 测速已取消", "warn");
            }
            catch (Exception ex)
            {
                Log($"测速失败: {ex.Message}", "error");
            }
            finally
            {
                if (runner != null)
                {
                    try { await runner.StopAsync(); } catch { }
                    runner.Dispose();
                }
                lock (_gate)
                {
                    SpeedTesting = false;
                    SpeedStatus = null;
                }
            }
        }, ct);

        return true;
    }
}
