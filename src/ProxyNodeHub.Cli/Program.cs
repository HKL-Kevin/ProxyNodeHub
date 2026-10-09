using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyNodeHub;

namespace ProxyNodeHub.Cli;

/// <summary>
/// ProxyNodeHub 的命令行入口（容器主进程）。
///
/// 复用 ProxyNodeHub.Core 的同一套逻辑：搜索关键词、活跃度评分、六层探测、
/// 节点去重、测速内核驱动。桌面版与 CLI 唯一的差别是「谁来展示」——
/// 这里把结果写到 stdout 与文件，供 Docker 场景消费。
/// </summary>
public static class Program
{
    private static Options _opt = new();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            _opt = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"参数错误: {ex.Message}");
            Console.Error.WriteLine();
            Options.PrintUsage();
            return 2;
        }

        if (_opt.ShowHelp) { Options.PrintUsage(); return 0; }
        if (_opt.ShowVersion)
        {
            Console.WriteLine($"ProxyNodeHub CLI {CliVersion} ({AppEnvironment.KernelAssetName})");
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return _opt.Command switch
            {
                "web" => await RunWebAsync(cts.Token),
                "search" => await RunSearchAsync(cts.Token),
                "merge" => await RunMergeAsync(cts.Token),
                "speedtest" => await RunSpeedTestAsync(cts.Token),
                "auto" or "pipeline" => await RunAutoPipelineAsync(cts.Token),
                "alive" => await RunAliveAsync(cts.Token),
                "kernel" => await RunKernelAsync(cts.Token),
                "doctor" => RunDoctor(),
                _ => UnknownCommand()
            };
        }
        catch (OperationCanceledException)
        {
            Log("\n已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            if (_opt.Verbose) Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private const string CliVersion = "0.0.1";

    // ══════════════════ web：常驻管理界面 ══════════════════

    private static async Task<int> RunWebAsync(CancellationToken ct)
    {
        // 默认监听 0.0.0.0：容器里必须这样，否则宿主访问不到。
        // 若用户显式传 --bind 则尊重用户选择（本地跑可以设 127.0.0.1 更安全）。
        return await WebServer.RunAsync(new WebOptions
        {
            Bind = _opt.Bind,
            Port = _opt.Port,
            OutputDir = _opt.OutputDir,
        }, ct);
    }

    private static int UnknownCommand()
    {
        Console.Error.WriteLine($"未知命令: {_opt.Command}");
        Console.Error.WriteLine();
        Options.PrintUsage();
        return 2;
    }

    // ══════════════════ search：搜索 + 分析 + 导出 ══════════════════

    private static async Task<int> RunSearchAsync(CancellationToken ct)
    {
        var token = _opt.Token ?? AppSettings.Load().GetToken();
        var github = new GitHubService(string.IsNullOrEmpty(token) ? null : token);

        if (github.HasToken)
            Log("GitHub Token: 已设置 (限额 5000 次/小时)");
        else
            Log("GitHub Token: 未设置 (匿名限额 60 次/小时，建议用 --token 或 GITHUB_TOKEN 环境变量)");

        // 代理：显式指定则用指定项，否则按 --auto-proxy 自动测速挑选
        if (!string.IsNullOrEmpty(_opt.Proxy))
            ApplyProxyByName(github, _opt.Proxy);
        else if (_opt.AutoProxy)
            await AutoDetectProxyAsync(github, ct);

        Log("");
        int lastLogged = 0;
        var found = await SearchPipeline.RunAsync(github, new SearchPipeline.Options
        {
            TargetCount = _opt.Count,
            SinceDays = _opt.SinceDays,
            Concurrency = _opt.Concurrency,
            SkipSearched = _opt.SkipSearched,
            HistoryDays = _opt.HistoryDays,
        }, progress =>
        {
            // 分析阶段每完成一个仓库打一行（与改造前的输出节奏一致）
            if (progress.Phase == "analyze" && progress.Done > lastLogged)
            {
                lastLogged = progress.Done;
                Log($"[{progress.Done}/{progress.Total}] {progress.Repo?.FullName} · " +
                    $"评分 {progress.Repo?.Score} · {progress.Repo?.TotalNodes} 节点");
            }
        }, log: null, ct);

        return await EmitResultsAsync(github, found, ct);
    }

    /// <summary>输出分析结果，并按需合并导出订阅。</summary>
    private static async Task<int> EmitResultsAsync(
        GitHubService github, List<RepoInfo> repos, CancellationToken ct)
    {
        // 过滤：只要验证到节点的仓库（除非用户显式要求全部）
        var usable = repos
            .Where(r => r.Links.Any(l => l.IsValid))
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.TotalNodes)
            .ToList();

        if (usable.Count == 0)
        {
            Console.Error.WriteLine("未找到任何可用的订阅链接。");
            return 1;
        }

        // 链接清单
        var sb = new StringBuilder();
        sb.AppendLine("# ProxyNodeHub 订阅链接");
        sb.AppendLine($"# 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# 仓库: {usable.Count} 个 (按活跃度排序)");
        sb.AppendLine("# 标注 [未验证] 的链接因网络原因未能确认, 请自行测试");
        sb.AppendLine();
        foreach (var r in usable)
        {
            sb.AppendLine($"# ── {r.FullName} ── 活跃度:{r.Score} | {r.StatusText} | {r.ProcessingType}");
            foreach (var link in r.Links)
            {
                var mark = link.NodeCount > 0 ? $" {link.NodeCount}节点"
                         : link.NodeCount == -1 ? " [未验证]" : " [无效]";
                sb.AppendLine($"{link.Url}    # {link.Name} ({link.Type}{mark})");
            }
            sb.AppendLine();
        }

        var linksPath = Path.Combine(_opt.OutputDir, "links.txt");
        Directory.CreateDirectory(_opt.OutputDir);
        AtomicFile.Write(linksPath, sb.ToString());
        Log($"\n链接清单 → {linksPath}");

        // 合并去重订阅（默认开启）
        if (!_opt.NoMerge)
        {
            var validLinks = usable.SelectMany(r => r.Links.Where(l => l.IsValid)).ToList();
            Log($"\n合并 {validLinks.Count} 条链接…");

            var contents = new List<string>();
            int i = 0;
            var converter = string.IsNullOrWhiteSpace(_opt.SubConverterUrl) ? null
                : _opt.SubConverterUrl!.Trim();
            if (converter == null)
                Log("⚠  未配置 --sub-converter，非纯 URI 订阅（Clash YAML / sing-box JSON / "
                    + "新协议）将识别不出。");
            foreach (var link in validLinks)
            {
                ct.ThrowIfCancellationRequested();
                // 非纯 URI 订阅过 subconverter 归一成 target=uri；端点已带查询串用 & 接续。
                var fetchUrl = converter != null && link.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? $"{converter}{(converter.Contains('?') ? "&" : "?")}target=uri&url={Uri.EscapeDataString(link.Url)}"
                    : link.Url;
                var content = await github.GetUrlAsync(fetchUrl, ct);
                if (!string.IsNullOrEmpty(content)) contents.Add(content);
                if (_opt.Verbose) Log($"  [{++i}/{validLinks.Count}] {link.Name}");
            }

            var (plain, b64, total, unique) = NodeParser.MergeNodes(contents);
            Log($"去重: 原始 {total} 节点 → 唯一 {unique} 个");

            AtomicFile.Write(Path.Combine(_opt.OutputDir, "merged.txt"), plain + "\n");
            AtomicFile.Write(Path.Combine(_opt.OutputDir, "merged.b64"), b64 + "\n");
            Log($"合并订阅 → {Path.Combine(_opt.OutputDir, "merged.txt")}");
            Log($"         → {Path.Combine(_opt.OutputDir, "merged.b64")}");

            if (_opt.Stdout) Console.WriteLine(b64);
        }
        else if (_opt.Stdout)
        {
            Console.WriteLine(sb.ToString());
        }

        // JSON 便于程序化消费
        if (_opt.Json)
        {
            var json = BuildJson(usable);
            var jsonPath = Path.Combine(_opt.OutputDir, "repos.json");
            AtomicFile.Write(jsonPath, json);
            Log($"结构化结果 → {jsonPath}");
        }

        Log($"\n完成: {usable.Count} 个仓库可用。");
        return 0;
    }

    private static string BuildJson(List<RepoInfo> repos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int i = 0; i < repos.Count; i++)
        {
            var r = repos[i];
            sb.AppendLine("  {");
            sb.AppendLine($"    \"fullName\": {JsonStr(r.FullName)},");
            sb.AppendLine($"    \"score\": {r.Score},");
            sb.AppendLine($"    \"stars\": {r.Stars},");
            sb.AppendLine($"    \"daysInactive\": {r.DaysInactive},");
            sb.AppendLine($"    \"totalNodes\": {r.TotalNodes},");
            sb.AppendLine($"    \"processingType\": {JsonStr(r.ProcessingType)},");
            sb.AppendLine("    \"links\": [");
            for (int j = 0; j < r.Links.Count; j++)
            {
                var l = r.Links[j];
                sb.AppendLine("      {");
                sb.AppendLine($"        \"name\": {JsonStr(l.Name)},");
                sb.AppendLine($"        \"url\": {JsonStr(l.Url)},");
                sb.AppendLine($"        \"type\": {JsonStr(l.Type)},");
                sb.AppendLine($"        \"nodeCount\": {l.NodeCount},");
                sb.AppendLine($"        \"valid\": {(l.IsValid ? "true" : "false")}");
                sb.AppendLine(j == r.Links.Count - 1 ? "      }" : "      },");
            }
            sb.AppendLine("    ]");
            sb.AppendLine(i == repos.Count - 1 ? "  }" : "  },");
        }
        sb.AppendLine("]");
        return sb.ToString();
    }

    private static string JsonStr(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c < 0x20 ? $"\\u{(int)c:x4}" : c.ToString()
            });
        }
        return sb.Append('"').ToString();
    }

    // ══════════════════ merge：仅合并已有链接 ══════════════════

    private static async Task<int> RunMergeAsync(CancellationToken ct)
    {
        if (_opt.Inputs.Count == 0)
        {
            Console.Error.WriteLine("merge 需要至少一个输入文件或 URL。");
            return 2;
        }

        var github = new GitHubService(_opt.Token);
        var contents = new List<string>();
        var converter = string.IsNullOrWhiteSpace(_opt.SubConverterUrl) ? null
            : _opt.SubConverterUrl!.Trim();
        if (converter == null)
            Log("⚠  未配置 --sub-converter，非纯 URI 订阅将识别不出。");

        foreach (var input in _opt.Inputs)
        {
            ct.ThrowIfCancellationRequested();
            string? content = null;

            if (File.Exists(input))
            {
                content = await File.ReadAllTextAsync(input, ct);
                Log($"读取本地文件: {input}");
            }
            else if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 非纯 URI 订阅过 subconverter 归一；端点已带查询串用 & 接续。
                var fetchUrl = converter != null
                    ? $"{converter}{(converter.Contains('?') ? "&" : "?")}target=uri&url={Uri.EscapeDataString(input)}"
                    : input;
                content = await github.GetUrlAsync(fetchUrl, ct);
                Log($"下载: {input}");
            }
            else
            {
                Console.Error.WriteLine($"跳过（既不是文件也不是 URL）: {input}");
                continue;
            }

            if (!string.IsNullOrEmpty(content)) contents.Add(content);
        }

        if (contents.Count == 0) { Console.Error.WriteLine("没有读到任何内容。"); return 1; }

        var (plain, b64, total, unique) = NodeParser.MergeNodes(contents);
        Log($"去重: 原始 {total} 节点 → 唯一 {unique} 个");

        Directory.CreateDirectory(_opt.OutputDir);
        AtomicFile.Write(Path.Combine(_opt.OutputDir, "merged.txt"), plain + "\n");
        AtomicFile.Write(Path.Combine(_opt.OutputDir, "merged.b64"), b64 + "\n");

        if (_opt.Stdout) Console.WriteLine(b64);
        else
        {
            Log($"→ {Path.Combine(_opt.OutputDir, "merged.txt")}");
            Log($"→ {Path.Combine(_opt.OutputDir, "merged.b64")}");
        }
        return 0;
    }

    // ══════════════════ speedtest：驱动 subs-check 内核 ══════════════════

    private static async Task<int> RunSpeedTestAsync(CancellationToken ct)
    {
        var kernel = SpeedTestConfig.KernelPath;
        if (!File.Exists(kernel))
        {
            Console.Error.WriteLine($"未找到测速内核: {kernel}");
            Console.Error.WriteLine("请先运行 `pnodes kernel install`，或在镜像构建时预装。");
            return 1;
        }

        var sources = new List<SubSource>();

        // 从合并结果或显式参数收集订阅来源
        foreach (var input in _opt.Inputs)
        {
            if (File.Exists(input))
            {
                var text = await File.ReadAllTextAsync(input, ct);
                foreach (var line in text.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        sources.Add(new SubSource { Url = t.Split('#')[0].Trim(), Repo = "" });
                }
            }
            else if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(new SubSource { Url = input, Repo = "" });
            }
        }

        string? customYaml = null;
        if (_opt.FromAlive)
        {
            customYaml = AliveNodeStore.ExportClashYaml();
            if (string.IsNullOrWhiteSpace(customYaml) || customYaml == "proxies: []\n")
            {
                Console.Error.WriteLine("存活池为空，请先运行 pnodes auto 或在 Web 界面启动自动化流水线。");
                return 1;
            }
            var stats = AliveNodeStore.GetStats();
            Log($"测速内核: {kernel}");
            Log($"数据来源: SQLite 存活池（共 {stats.TotalCount} 个存活节点，稳定节点 {stats.StableCount} 个）");
        }
        else
        {
            if (sources.Count == 0)
            {
                var mergedPath = Path.Combine(_opt.OutputDir, "merged.txt");
                if (File.Exists(mergedPath))
                {
                    Console.Error.WriteLine("speedtest 需要订阅来源。");
                    Console.Error.WriteLine("提示: merged.txt 是节点 URI 列表（不是订阅 URL），");
                    Console.Error.WriteLine("      请把上游订阅 URL 作为参数传入，例如：");
                    Console.Error.WriteLine("      pnodes speedtest https://example.com/sub");
                    return 2;
                }
                Console.Error.WriteLine("speedtest 需要至少一个订阅 URL 或含 URL 的文件，或使用 --from-alive 直接测存活池。");
                return 2;
            }

            Log($"测速内核: {kernel}");
            Log($"订阅来源: {sources.Count} 个");
        }

        SpeedTestConfig.EnsureDirs();
        EnsureDefaultConfig();

        using var runner = new SpeedTestRunner();
        runner.OnLog += line => { if (_opt.Verbose) Log($"  [kernel] {line}"); };

        await runner.PrepareAsync(
            _opt.FromAlive ? new List<string>() : SubSourceHelper.ToTaggedUrls(sources),
            withHistory: false, keepRounds: 0, nodeUris: null, aliveOnly: false, customYamlBody: customYaml);
        await runner.StartAsync(Log, ct);
        await runner.TriggerAsync(ct);

        Log("\n测速进行中…（可用 Ctrl+C 取消）");

        // 进度口径与桌面版一致：Pipeline.SpeedDone / Total。
        // 内核的 progress 字段不是 0-100 的百分比（实测会超过 100），
        // 用它当百分比会显示成 2638% 这种数字。
        //
        // 完成判据用「Checking 由 true 翻回 false」而不是进度达标：
        // 订阅为空（Total=0）或全部节点未通过测活（SpeedDone=0）时，
        // 内核其实已经跑完，用进度判据会一直空转到超时。
        var deadline = DateTime.UtcNow.AddMinutes(_opt.TimeoutMinutes);
        bool finished = false, sawChecking = false;
        int nullStreak = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(3000, ct);
            var st = await runner.GetStatusAsync(ct);
            if (st == null)
            {
                if (++nullStreak >= 6) { finished = true; break; }
                continue;
            }
            nullStreak = 0;

            var p = st.Pipeline;
            int total = p?.Total ?? st.ProxyCount;
            int alivePass = p?.AlivePass ?? st.Available;
            int speedDone = p?.SpeedDone ?? 0;
            int speedPass = p?.SpeedPass ?? 0;
            int pct = total > 0 ? (int)Math.Min(100, 100.0 * speedDone / total) : 0;

            Console.Error.WriteLine($"\r  进度 {pct}% · 节点 {total} · 存活 {alivePass} · 测速通过 {speedPass}    ");

            if (st.Checking) sawChecking = true;
            else if (sawChecking) { finished = true; break; }
        }
        Console.Error.WriteLine();

        if (!finished && !ct.IsCancellationRequested)
            Console.Error.WriteLine("警告: 测速未在超时内完成，导出当前已有结果。");

        var snap = await runner.GetResultsAsync(ct);
        if (snap == null || snap.Nodes.Count == 0)
        {
            // 超时但已有部分结果时，/api/results 可能还没就绪；
            // 退一步用内核的订阅接口，至少把已通过的节点导出来。
            var partial = await runner.GetPassedSubscriptionAsync(ct);
            if (!string.IsNullOrWhiteSpace(partial))
            {
                partial = SpeedTestRunner.FormatPassedYaml(partial);
                Directory.CreateDirectory(_opt.OutputDir);
                var partialPath = Path.Combine(_opt.OutputDir, "passed.yaml");
                AtomicFile.Write(partialPath, partial);
                Log($"\n未完成全量测速，已导出部分通过节点 → {partialPath}");
                if (_opt.Stdout) Console.WriteLine(partial);
                await runner.StopAsync();
                return 0;
            }
            Console.Error.WriteLine("\n未取到测速结果。");
            await runner.StopAsync();
            return 1;
        }

        var passed = snap.Nodes.Where(n => n.Passed).ToList();
        Log($"\n测速完成: {snap.Nodes.Count} 个节点，{passed.Count} 个通过。");

        // 导出通过节点（Clash YAML，内核直接给的就是这个格式）
        var yaml = await runner.GetPassedSubscriptionAsync(ct);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            var diskPath = Path.Combine(SpeedTestConfig.OutputDir, "all.yaml");
            if (File.Exists(diskPath))
            {
                try { yaml = File.ReadAllText(diskPath); } catch { }
            }
        }
        if (!string.IsNullOrEmpty(yaml))
        {
            yaml = SpeedTestRunner.FormatPassedYaml(yaml, snap.Nodes);
            try { AliveNodeStore.SaveRound(yaml, snap.Nodes); } catch { }
            var outPath = Path.Combine(_opt.OutputDir, "passed.yaml");
            Directory.CreateDirectory(_opt.OutputDir);
            AtomicFile.Write(outPath, yaml);
            Log($"通过节点 → {outPath}");
            if (_opt.Stdout) Console.WriteLine(yaml);
        }

        runner.SaveRound(snap, yaml);
        await runner.StopAsync();
        return 0;
    }

    // ══════════════════ auto：全自动化流水线（搜索 ➔ 极速测活 ➔ 存活池入库） ══════════════════

    private static async Task<int> RunAutoPipelineAsync(CancellationToken ct)
    {
        Log("══════════════════ 全自动化流水线 ══════════════════");
        var kernel = SpeedTestConfig.KernelPath;
        if (!File.Exists(kernel))
        {
            Console.Error.WriteLine($"未找到测速内核: {kernel}");
            return 1;
        }

        SpeedTestConfig.EnsureDirs();
        EnsureDefaultConfig();

        var github = new GitHubService(_opt.Token);

        // 阶段 1: 搜索活跃仓库
        Log("\n[1/3] 搜索 GitHub 活跃仓库并探测订阅…");
        int lastLogged = 0;
        var found = await SearchPipeline.RunAsync(github, new SearchPipeline.Options
        {
            TargetCount = _opt.Count,
            SinceDays = _opt.SinceDays,
            Concurrency = _opt.Concurrency,
            SkipSearched = _opt.SkipSearched,
            HistoryDays = _opt.HistoryDays,
        }, progress =>
        {
            if (progress.Phase == "analyze" && progress.Done > lastLogged)
            {
                lastLogged = progress.Done;
                Log($"  [{progress.Done}/{progress.Total}] {progress.Repo?.FullName} · " +
                    $"评分 {progress.Repo?.Score} · {progress.Repo?.TotalNodes} 节点");
            }
        }, log: line => { if (_opt.Verbose) Log(line); }, ct: ct);

        var usable = found
            .Where(r => r.Links.Any(l => l.IsValid && l.NodeCount > 0))
            .ToList();

        var speedTestSources = new List<SubSource>();
        foreach (var r in usable)
        {
            foreach (var l in r.Links.Where(l => l.IsValid && l.NodeCount > 0))
            {
                speedTestSources.Add(new SubSource { Url = l.Url, Repo = r.FullName });
            }
        }

        if (speedTestSources.Count == 0)
        {
            Console.Error.WriteLine("未发现有效订阅来源，流水线终止。");
            return 1;
        }

        // 阶段 2: 聚合候选节点
        Log($"\n[2/3] 从 {speedTestSources.Count} 个订阅源提取候选节点…");
        var entries = await NodeCatalog.LoadAsync(github, speedTestSources,
            (done, total) => { },
            log: m => { if (_opt.Verbose) Log(m); },
            ct: ct,
            subConverterUrl: _opt.SubConverterUrl
        );

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("未能解析出任何候选节点，流水线终止。");
            return 1;
        }

        var candidateUris = entries.Select(e => e.Uri).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
        Log($"提取完成: {candidateUris.Count} 个去重候选节点");

        // 阶段 3: 极速测活（0 字节探针，并发 50）
        Log($"\n[3/3] 启动极速测活（并发 50，跳过真实下载与流媒体检测）…");
        using var runner = new SpeedTestRunner();
        runner.OnLog += line => { if (_opt.Verbose) Log($"  [kernel] {line}"); };

        await runner.PrepareAsync(new List<string>(), withHistory: false, keepRounds: 0,
            nodeUris: candidateUris, aliveOnly: true);
        await runner.StartAsync(Log, ct);
        await runner.TriggerAsync(ct);

        var deadline = DateTime.UtcNow.AddMinutes(_opt.TimeoutMinutes);
        bool sawChecking = false;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            var st = await runner.GetStatusAsync(ct);
            if (st == null) continue;
            var p = st.Pipeline;
            int total = p?.Total ?? st.ProxyCount;
            int alivePass = p?.AlivePass ?? st.Available;
            Console.Error.Write($"\r  测活进度: 节点 {total} · 存活 {alivePass}    ");
            if (st.Checking) sawChecking = true;
            else if (sawChecking) break;
        }
        Console.Error.WriteLine();

        var snap = await runner.GetResultsAsync(ct);
        var kernelYaml = await runner.GetPassedSubscriptionAsync(ct);
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
            Log($"\n✓ 存活池已更新并持久化到 SQLite ({AliveNodeStore.DbPath})");
            Log($"  本轮新增: {inserted} 个 · 更新: {updated} 个 · 存活池总计: {stats.TotalCount} 个（稳定节点 {stats.StableCount} 个）");

            // 同步输出 alive.yaml 到输出目录
            var aliveYaml = AliveNodeStore.ExportClashYaml();
            Directory.CreateDirectory(_opt.OutputDir);
            var outPath = Path.Combine(_opt.OutputDir, "alive.yaml");
            AtomicFile.Write(outPath, aliveYaml);
            Log($"  导出 YAML → {outPath}");
            if (_opt.Stdout) Console.WriteLine(aliveYaml);
        }
        else
        {
            Console.Error.WriteLine("未发现存活节点。");
            await runner.StopAsync();
            return 1;
        }

        await runner.StopAsync();
        return 0;
    }

    // ══════════════════ alive：查看与管理 SQLite 存活池 ══════════════════

    private static Task<int> RunAliveAsync(CancellationToken ct)
    {
        var stats = AliveNodeStore.GetStats();
        Log($"══════════════════ SQLite 存活池状态 ══════════════════");
        Log($"数据库路径: {AliveNodeStore.DbPath}");
        Log($"存活节点总数: {stats.TotalCount} 个");
        Log($"稳定节点(>=2轮): {stats.StableCount} 个");
        if (stats.LastUpdatedAt.HasValue)
            Log($"最近更新时间: {stats.LastUpdatedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        Log("\n地区分布:");
        foreach (var (cc, count) in stats.CountryDistribution)
            Log($"  {cc}: {count} 个");

        if (_opt.Stdout || _opt.Inputs.Contains("export") || _opt.Inputs.Contains("yaml"))
        {
            var yaml = AliveNodeStore.ExportClashYaml();
            Console.WriteLine(yaml);
        }

        return Task.FromResult(0);
    }

    /// <summary>首次运行时生成内核默认 config.yaml（内容与桌面版一致）。</summary>
    private static void EnsureDefaultConfig()
    {
        try
        {
            if (!File.Exists(SpeedTestConfig.ConfigPath))
            {
                AtomicFile.Write(SpeedTestConfig.ConfigPath, SpeedTestDefaults.DefaultYaml);
                Log($"已生成默认配置: {SpeedTestConfig.ConfigPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"写入默认配置失败: {ex.Message}");
        }
    }

    // ══════════════════ kernel：内核版本查询 / 安装 ══════════════════

    private static async Task<int> RunKernelAsync(CancellationToken ct)
    {
        var action = _opt.Inputs.FirstOrDefault() ?? "status";

        switch (action)
        {
            case "status":
            {
                var path = SpeedTestConfig.KernelPath;
                var ver = SubCheckKernel.LocalVersion();
                Console.WriteLine($"内核路径: {path}");
                Console.WriteLine($"已安装:  {(File.Exists(path) ? "是" : "否")}");
                Console.WriteLine($"版本:    {SubCheckKernel.Display(ver)}");
                Console.WriteLine($"平台资产: {AppEnvironment.KernelAssetName}");
                return 0;
            }
            case "install":
            {
                Log($"查询最新版本 ({AppEnvironment.KernelAssetName})…");
                // 直连 api.github.com 在受限网络下会失败（匿名限额 60/小时，
                // 且常被墙）。这里同时尝试镜像，与桌面版行为一致。
                var proxy = ResolveProxyPrefix();
                var rel = await SubCheckKernel.FetchLatestAsync(proxy, ct);
                if (rel == null)
                {
                    Console.Error.WriteLine("查询 release 失败。");
                    Console.Error.WriteLine("提示: 可用 --proxy <名称> 指定加速镜像，例如 --proxy ghfast.top");
                    return 1;
                }
                Log($"最新版本: {rel.Version} ({rel.SizeBytes / 1024 / 1024} MB)");

                await SubCheckKernel.InstallAsync(rel, prog =>
                {
                    if (prog.Phase == "下载")
                        Console.WriteLine($"\r  下载 {prog.Percent:F1}%  ({prog.BytesDone / 1024 / 1024} MB)    ");
                }, ct, proxy);

                Console.WriteLine();
                Log($"安装完成 → {SpeedTestConfig.KernelPath}");
                return 0;
            }
            default:
                Console.Error.WriteLine($"未知的 kernel 动作: {action}（可用: status, install）");
                return 2;
        }
    }

    // ══════════════════ doctor：环境自检 ══════════════════

    private static int RunDoctor()
    {
        Console.WriteLine("ProxyNodeHub 容器环境自检");
        Console.WriteLine("─────────────────────────────");
        Console.WriteLine($"平台:          {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"架构:          {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"内核资产:      {AppEnvironment.KernelAssetName}");
        Console.WriteLine($"数据目录:      {AppEnvironment.UserDataDir}");
        Console.WriteLine($"配置目录:      {AppEnvironment.ConfigDir}");
        Console.WriteLine($"内核目录:      {SpeedTestConfig.RootDir}");
        Console.WriteLine($"内核路径:      {SpeedTestConfig.KernelPath}");
        Console.WriteLine($"内核已安装:    {(File.Exists(SpeedTestConfig.KernelPath) ? "是" : "否")}");

        var settings = AppSettings.Load();
        var token = _opt.Token ?? settings.GetToken();
        Console.WriteLine($"GitHub Token:  {(string.IsNullOrEmpty(token) ? "未设置" : "已设置")}");

        try
        {
            Directory.CreateDirectory(AppEnvironment.UserDataDir);
            var probe = Path.Combine(AppEnvironment.UserDataDir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Console.WriteLine("数据目录可写:  是");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"数据目录可写:  否 ({ex.Message})");
        }

        return 0;
    }

    // ══════════════════ 代理辅助 ══════════════════

    /// <summary>
    /// 内核下载/查询用的「前缀 + 完整 URL」函数。
    ///
    /// 注意：并非所有镜像都能代理 api.github.com —— 实测 ghfast.top /
    /// ghproxy.net 对 API 请求返回 403（它们只代理 raw 与 release 下载），
    /// gh-proxy.com 可以。所以默认选 gh-proxy.com 而不是更常见的 ghfast.top。
    /// 用户显式指定 --proxy 时尊重用户选择。
    /// </summary>
    private static Func<string, string> ResolveProxyPrefix()
    {
        var name = _opt.Proxy;
        if (string.IsNullOrEmpty(name))
            return url => "https://gh-proxy.com/" + url;

        if (name.Contains("直连", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return url => url;

        var match = GitHubService.AllProxies.FirstOrDefault(p =>
            !p.IsDefault && p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match == null) return url => "https://gh-proxy.com/" + url;

        return url => match.IsJsDelivr ? url : match.Prefix + url;
    }

    private static void ApplyProxyByName(GitHubService github, string name)
    {
        var match = GitHubService.AllProxies.FirstOrDefault(p =>
            p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            Console.Error.WriteLine($"未知代理: {name}（可用: 直连, ghfast.top, gh-proxy.com, jsDelivr…）");
            return;
        }
        github.CurrentProxy = match;
        Log($"使用代理: {match.Name}");
    }

    private static async Task AutoDetectProxyAsync(GitHubService github, CancellationToken ct)
    {
        Log("自动测速所有加速镜像…");
        var sorted = await GitHubService.TestAllProxiesAsync(ct);
        var best = sorted.FirstOrDefault(p => p.LatencyMs > 0);
        if (best != null)
        {
            github.CurrentProxy = best;
            Log($"选中代理: {best.Name} ({best.LatencyMs}ms)");
        }
        else
        {
            Log("所有镜像均不可用，使用直连。");
        }
    }

    // ══════════════════ 日志 ══════════════════

    private static void Log(string msg)
    {
        // 结果走 stdout（便于管道），进度与诊断走 stderr
        Console.Error.WriteLine(msg);
    }
}
