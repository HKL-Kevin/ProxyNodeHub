using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProxyNodeHub;

namespace ProxyNodeHub.Cli;

/// <summary>
/// Web 管理界面的服务端。
///
/// 用 ASP.NET Core 最小 API + 静态文件，前端是单页原生 JS ——
/// 不引前端框架、不加构建步骤，整个界面随 CLI 一起发布。
///
/// 设计上刻意与桌面版对齐：同一套 Core 管线、同一套筛选口径、
/// 同一套 DOSSIER 配色，只是把 WinForms 控件换成 HTML。
/// </summary>
public static class WebServer
{
    public static async Task<int> RunAsync(WebOptions opt, CancellationToken ct)
    {
        var state = new WebState();
        // 启动时把上次的状态读回来，让重建容器后页面不是空的。
        state.RestoreFromDisk();
        // IP 信息缓存也读回来，同一 IP 不重复查
        IpInfoService.LoadCache();
        state.Log($"ProxyNodeHub Web 管理界面启动 · http://{opt.Bind}:{opt.Port}");
        state.Log($"数据目录: {AppEnvironment.UserDataDir}");
        state.Log($"测速内核: {SpeedTestConfig.KernelPath}");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();   // 用我们自己的日志通道，避免双份输出

        // 必须显式告诉 Kestrel 监听哪里。不设的话它用默认的 5000，
        // 我们解析出来的 --port/--bind 会被静默忽略 —— 界面看起来启动成功，
        // 但用户按提示的端口根本连不上。
        builder.WebHost.UseUrls($"http://{opt.Bind}:{opt.Port}");

        var app = builder.Build();
        MapApi(app, state, opt);
        MapStatic(app);

        try
        {
            await app.StartAsync(ct);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"无法绑定 {opt.Bind}:{opt.Port} — {ex.Message}");
            Console.Error.WriteLine("端口可能已被占用，用 --port 换一个。");
            return 1;
        }

        Console.Error.WriteLine($"ProxyNodeHub Web 管理界面已启动");
        Console.Error.WriteLine($"  监听: http://{opt.Bind}:{opt.Port}");
        if (opt.Bind == "0.0.0.0")
            Console.Error.WriteLine($"  容器外访问: http://localhost:{opt.Port}");
        Console.Error.WriteLine("  按 Ctrl+C 停止");

        await app.WaitForShutdownAsync(ct);
        return 0;
    }

    // ══════════════════ API ══════════════════

    private static void MapApi(WebApplication app, WebState state, WebOptions opt)
    {
        var api = app.MapGroup("/api");

        // ── 状态总览 ──
        api.MapGet("/state", () => Results.Json(new
        {
            searching = state.Searching,
            searchPhase = state.SearchPhase,
            searchDone = state.SearchDone,
            searchTotal = state.SearchTotal,
            searchMessage = state.SearchMessage,
            pipelineRunning = state.PipelineRunning,
            pipelinePhase = state.PipelinePhase,
            pipelineMessage = state.PipelineMessage,
            aliveCount = AliveNodeStore.GetStats().TotalCount,
            speedTesting = state.SpeedTesting,
            speedStatus = state.SpeedStatus,
            speedFinishedAt = state.SpeedFinishedAt,
            loadingCatalog = state.LoadingCatalog,
            catalogDone = state.CatalogDone,
            catalogTotal = state.CatalogTotal,
            catalogCount = state.Catalog.Count,
            // 完成时间戳：前端据此判断「有新清单可拉」，而不是拿筛选后的
            // 条数跟总数比较 —— 后者只要筛选生效就永远不相等，会导致
            // 轮询每次（1.2 秒）都重新拉一遍清单。
            catalogLoadedAt = state.CatalogLoadedAt,
            repoCount = state.Repos.Count,
            nodeCount = state.Nodes.Count,
            passedCount = state.Nodes.Count(n => n.Passed),
            version = SubCheckKernel.Display(SubCheckKernel.LocalVersion()),
            kernelPath = SpeedTestConfig.KernelPath,
            kernelInstalled = File.Exists(SpeedTestConfig.KernelPath),
            platform = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        }));

        // ── 日志：先回填历史，再实时推送（SSE） ──
        api.MapGet("/log", async (HttpContext ctx, CancellationToken ct2) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var queue = System.Threading.Channels.Channel.CreateUnbounded<WebState.LogLine>(
                new System.Threading.Channels.UnboundedChannelOptions
                { SingleReader = true, SingleWriter = false });

            void Handler(WebState.LogLine l) => queue.Writer.TryWrite(l);
            state.OnLog += Handler;

            try
            {
                // 回填已有的日志（新开页面能看到之前的进度）
                foreach (var l in state.LogSnapshot())
                    await WriteEvent(ctx, l, ct2);

                await foreach (var l in queue.Reader.ReadAllAsync(ct2))
                    await WriteEvent(ctx, l, ct2);
            }
            catch (OperationCanceledException) { }
            finally { state.OnLog -= Handler; }
        });

        api.MapPost("/log/clear", () => { state.ClearLog(); return Results.Ok(); });

        // ── 仓库列表 ──
        api.MapGet("/repos", (string? filter, int? inactiveDays, int? minCommits,
            int? minActiveDays, bool? autoClean, string? sort, bool? desc) =>
        {
            var filtered = SearchPipeline.ApplyFilters(
                state.Repos,
                inactiveDays ?? state.Settings.InactiveDays,
                minCommits ?? state.Settings.MinCommitsPerDay,
                minActiveDays ?? state.Settings.MinActiveDays,
                autoClean ?? state.Settings.AutoClean,
                filter);

            var sorted = SortRepos(filtered, sort ?? "Score", desc ?? true);
            return Results.Json(sorted.Select(ToDto));
        });

        // ── 收藏 ──
        api.MapGet("/favorites", () =>
        {
            var favs = FavoritesStore.Load();
            return Results.Json(favs.Select(ToDto));
        });

        api.MapPost("/favorites", (FavoriteRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.FullName))
                return Results.BadRequest(new { error = "缺少 fullName" });

            var repo = state.Repos.FirstOrDefault(r =>
                string.Equals(r.FullName, req.FullName, StringComparison.OrdinalIgnoreCase));
            if (repo == null) return Results.NotFound(new { error = $"未找到仓库 {req.FullName}" });

            var favs = FavoritesStore.Load();
            if (!favs.Any(f => string.Equals(f.FullName, repo.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                favs.Add(repo);
                FavoritesStore.Save(favs);
                state.Log($"已收藏 {repo.FullName}");
            }
            return Results.Ok();
        });

        // 用查询参数而非路径段：仓库名含 "/"，URL 编码成 %2F 后
        // Kestrel 默认不会把它解码回路径分隔符，路由匹配不到（实测 404）。
        api.MapDelete("/favorites", (string fullName) =>
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return Results.BadRequest(new { error = "缺少 fullName" });

            var favs = FavoritesStore.Load();
            var removed = favs.RemoveAll(f =>
                string.Equals(f.FullName, fullName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                FavoritesStore.Save(favs);
                state.Log($"已取消收藏 {fullName}");
            }
            return Results.Ok();
        });

        // ── 搜索 ──
        api.MapPost("/search", (SearchRequest req) =>
        {
            var ok = state.StartSearch(
                req.Count ?? state.Settings.RepoCount,
                req.SinceDays ?? 7,
                req.Concurrency ?? 6,
                req.SkipSearched ?? false,
                req.Proxy, req.AutoProxy ?? false, req.Token);

            return ok
                ? Results.Ok(new { started = true })
                : Results.Conflict(new { error = "已有搜索在进行中" });
        });

        api.MapPost("/search/cancel", () => { state.CancelSearch(); return Results.Ok(); });

        // ── 测速 ──

        // 节点清单：拉取解析订阅，只列节点，不启动内核、不产生测速流量。
        api.MapPost("/catalog/load", (CatalogRequest req) =>
        {
            var sources = CollectSources(state, req.Urls, req.IncludeResults, req.IncludeFavorites);
            if (sources.Count == 0)
                return Results.BadRequest(new { error = "没有可加载的订阅来源" });

            var ok = state.StartCatalogLoad(sources, req.Token);
            return ok
                ? Results.Ok(new { started = true, sources = sources.Count })
                : Results.Conflict(new { error = "清单正在加载中" });
        });

        api.MapPost("/catalog/cancel", () => { state.CancelCatalogLoad(); return Results.Ok(); });

        // 筛选参数用逗号分隔的多值：protocols=vless,vmess&repos=a/b,c/d
        // 单值形态（protocol=all）是历史调用方式，仍然兼容。
        api.MapGet("/catalog", (string? keyword, string? protocols, string? protocol,
            string? repos, string? repo) =>
        {
            var protoList = SplitList(protocols);
            if (protoList.Count == 0) protoList = SplitList(protocol);
            protoList.RemoveAll(p => string.Equals(p, "all", StringComparison.OrdinalIgnoreCase));

            var repoList = SplitList(repos);
            if (repoList.Count == 0) repoList = SplitList(repo);

            var (nodes, mode, error) = state.FilterCatalog(keyword, protoList, repoList);
            var (allProtocols, allRepos) = state.CatalogFacets();

            return Results.Json(new
            {
                total = state.Catalog.Count,
                filtered = nodes.Count,
                protocols = allProtocols,
                repos = allRepos,
                // 关键词是被当作正则还是退化为文本匹配，供界面提示
                matchMode = mode,
                matchError = error,
                loadedAt = state.CatalogLoadedAt,
                loading = state.LoadingCatalog,
                done = state.CatalogDone,
                totalSources = state.CatalogTotal,
                nodes = nodes.Select(n => new
                {
                    // uri 必须回传：前端勾选后要把选中的 URI 发回来测速，
                    // 少这个字段前端拿到的就是 undefined，测速请求会全空。
                    uri = n.Uri,
                    name = n.Name,
                    protocol = n.Protocol,
                    server = n.Server,
                    port = n.Port,
                    repo = n.Repo,
                    sourceName = n.SourceName,
                }),
            });
        });

        api.MapGet("/speedtest/nodes", () => Results.Json(state.Nodes.Select(ToNodeDto)));

        // 只测选中的节点。请求体里给 URI 列表（从清单里勾选），
        // 内核因此只测这一批，不会去拉全部订阅。
        api.MapPost("/speedtest/selected", (SelectedSpeedRequest req) =>
        {
            if (req.Uris == null || req.Uris.Count == 0)
                return Results.BadRequest(new { error = "没有选中的节点" });

            var ok = state.StartSpeedTestNodes(req.Uris, req.WithHistory ?? false, req.KeepRounds ?? 3);
            return ok
                ? Results.Ok(new { started = true, nodes = req.Uris.Count })
                : Results.Conflict(new { error = "已有测速在进行中" });
        });

        api.MapPost("/speedtest", (SpeedRequest req) =>
        {
            var sources = CollectSources(state, req.Urls, req.IncludeResults, req.IncludeFavorites);
            if (sources.Count == 0)
                return Results.BadRequest(new { error = "没有可测的订阅来源" });

            var ok = state.StartSpeedTest(sources, req.WithHistory ?? true, req.KeepRounds ?? 3);
            return ok
                ? Results.Ok(new { started = true, sources = sources.Count })
                : Results.Conflict(new { error = "已有测速在进行中" });
        });

        api.MapPost("/speedtest/cancel", () => { state.CancelSpeedTest(); return Results.Ok(); });

        // ── 导出测速结果（通过节点的 Clash YAML）──
        api.MapGet("/export/passed", (string? format) =>
        {
            var yaml = state.GetPassedYaml();
            if (string.IsNullOrWhiteSpace(yaml))
                return Results.NotFound(new { error = "还没有测速结果。先测一轮再导出。" });

            var outDir = opt.OutputDir;
            try { Directory.CreateDirectory(outDir); } catch { }

            // 同时写盘一份，跟 CLI speedtest 命令行为一致
            try
            {
                AtomicFile.Write(Path.Combine(outDir, "passed.yaml"), yaml);
                AtomicFile.Write(Path.Combine(outDir, "passed.b64"),
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)));
                state.Log($"已导出测速结果 → {outDir}/passed.yaml + passed.b64", "success");
            }
            catch { }

            if (string.Equals(format, "base64", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml));
                return Results.Text(b64, "text/plain; charset=utf-8");
            }
            return Results.Text(yaml, "text/yaml; charset=utf-8",
                Encoding.UTF8);
        });

        // ── 全自动流水线 (搜索 ➔ 极速测活 ➔ 存活池持久化) ──
        api.MapPost("/pipeline/start", (PipelineRequest? req) =>
        {
            var count = req?.Count ?? state.Settings.RepoCount;
            var days = req?.Days ?? 7;
            var conc = req?.Concurrency ?? 6;
            var skip = req?.SkipSearched ?? false;
            var hDays = req?.HistoryDays ?? 1;
            var ok = state.StartAutoPipeline(count, days, conc, skip, hDays, req?.Token);
            return ok
                ? Results.Ok(new { started = true })
                : Results.Conflict(new { error = "已有流水线/搜索/测速正在运行" });
        });

        api.MapPost("/pipeline/cancel", () =>
        {
            state.CancelAutoPipeline();
            return Results.Ok();
        });

        // ── 存活池端点 ──
        api.MapGet("/alive/nodes", (string? country, int? limit) =>
        {
            var nodes = AliveNodeStore.GetSummaries(country, limit ?? 500);
            var stats = AliveNodeStore.GetStats();
            return Results.Json(new { stats, nodes });
        });

        api.MapGet("/export/alive", (string? format, string? country, int? minConsecutive) =>
        {
            var yaml = AliveNodeStore.ExportClashYaml(minConsecutive, country);
            if (string.Equals(format, "base64", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml));
                return Results.Text(b64, "text/plain; charset=utf-8");
            }
            return Results.Text(yaml, "text/yaml; charset=utf-8", Encoding.UTF8);
        });

        api.MapPost("/speedtest/alive", (SpeedRequest? req) =>
        {
            var ok = state.StartSpeedTestFromAlivePool(req?.WithHistory ?? false, req?.KeepRounds ?? 3);
            return ok
                ? Results.Ok(new { started = true })
                : Results.Conflict(new { error = "已有测速/流水线在进行，或存活池为空" });
        });

        // ── 合并导出 ──
        api.MapPost("/merge", async (MergeRequest req, CancellationToken ct2) =>
        {
            try
            {
                var (b64, plain, total, unique) = await state.MergeAsync(req.Token, ct2);
                var outDir = opt.OutputDir;
                Directory.CreateDirectory(outDir);
                AtomicFile.Write(Path.Combine(outDir, "merged.txt"), plain + "\n");
                AtomicFile.Write(Path.Combine(outDir, "merged.b64"), b64 + "\n");
                state.Log($"已导出到 {outDir}/merged.txt 与 merged.b64", "success");
                return Results.Json(new { b64, total, unique });
            }
            catch (OperationCanceledException) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                state.Log($"合并失败: {ex.Message}", "error");
                return Results.Problem(ex.Message);
            }
        });

        // ── 导出链接清单 ──
        api.MapGet("/export/links", () =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ProxyNodeHub 订阅链接");
            sb.AppendLine($"# 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            var repos = state.Repos.Where(r => r.Links.Count > 0)
                .OrderByDescending(r => r.Score).ToList();
            sb.AppendLine($"# 仓库: {repos.Count} 个 (按活跃度排序)");
            sb.AppendLine("# 标注 [未验证] 的链接因网络原因未能确认, 请自行测试");
            sb.AppendLine();
            foreach (var r in repos)
            {
                sb.AppendLine($"# ── {r.FullName} ── 活跃度:{r.Score} | {r.StatusText} | {r.ProcessingType}");
                foreach (var l in r.Links)
                {
                    var mark = l.NodeCount > 0 ? $" {l.NodeCount}节点"
                             : l.NodeCount == -1 ? " [未验证]" : " [无效]";
                    sb.AppendLine($"{l.Url}    # {l.Name} ({l.Type}{mark})");
                }
                sb.AppendLine();
            }
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        });

        // ── 设置 ──
        api.MapGet("/settings", () =>
        {
            var s = state.Settings;
            return Results.Json(new
            {
                // 不回传完整 token，只回传是否已设置 —— 避免明文 token 出现在浏览器历史/日志里
                hasToken = !string.IsNullOrEmpty(s.GithubKey),
                repoCount = s.RepoCount,
                minCommitsPerDay = s.MinCommitsPerDay,
                minActiveDays = s.MinActiveDays,
                inactiveDays = s.InactiveDays,
                autoClean = s.AutoClean,
                kernelPath = s.KernelPath,
                subConverterUrl = s.SubConverterUrl,
                speedTimeout = s.SpeedTimeout,
                downloadMb = s.DownloadMb,
                minSpeed = s.MinSpeed,
                speedConcurrent = s.SpeedConcurrent,
                concurrent = s.Concurrent,
                speedTestUrl = s.SpeedTestUrl,
                mediaCheck = s.MediaCheck,
                mediaCheckTimeout = s.MediaCheckTimeout,
                platforms = s.Platforms,
                ipInfoToken = s.IpInfoToken,
                subUrls = s.SubUrls?.Select(u => new { name = u.Name, url = u.Url }),
                regionPresets = s.EnsureDefaultRegionPresets()
                    .Select(p => new { name = p.Name, pattern = p.Pattern }),
            });
        });

        api.MapPost("/settings", (SettingsRequest req) =>
        {
            var s = state.Settings;
            if (req.Token != null) s.GithubKey = req.Token.Trim();
            if (req.RepoCount.HasValue) s.RepoCount = Math.Clamp(req.RepoCount.Value, 1, 500);
            if (req.MinCommitsPerDay.HasValue) s.MinCommitsPerDay = Math.Max(0, req.MinCommitsPerDay.Value);
            if (req.MinActiveDays.HasValue) s.MinActiveDays = Math.Max(0, req.MinActiveDays.Value);
            if (req.InactiveDays.HasValue) s.InactiveDays = Math.Max(1, req.InactiveDays.Value);
            if (req.AutoClean.HasValue) s.AutoClean = req.AutoClean.Value;
            if (req.SubConverterUrl != null) s.SubConverterUrl = req.SubConverterUrl.Trim();
            if (req.SpeedTimeout.HasValue) s.SpeedTimeout = Math.Max(500, req.SpeedTimeout.Value);
            if (req.DownloadMb.HasValue) s.DownloadMb = Math.Max(0, req.DownloadMb.Value);
            if (req.MinSpeed.HasValue) s.MinSpeed = Math.Max(0, req.MinSpeed.Value);
            if (req.SpeedConcurrent.HasValue) s.SpeedConcurrent = Math.Clamp(req.SpeedConcurrent.Value, 1, 200);
            if (req.Concurrent.HasValue) s.Concurrent = Math.Clamp(req.Concurrent.Value, 1, 200);
            if (req.SpeedTestUrl != null) s.SpeedTestUrl = req.SpeedTestUrl.Trim();
            if (req.MediaCheck.HasValue) s.MediaCheck = req.MediaCheck.Value;
            if (req.MediaCheckTimeout.HasValue) s.MediaCheckTimeout = Math.Max(1, req.MediaCheckTimeout.Value);
            if (req.Platforms != null)
                s.Platforms = req.Platforms.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim()).Distinct().ToList();
            if (req.RegionPresets != null)
            {
                // 整体替换：前端发完整列表过来，空 pattern / 空 name 的丢掉
                s.RegionPresets = req.RegionPresets
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Pattern))
                    .Select(p => new RegexPreset { Name = p.Name!.Trim(), Pattern = p.Pattern!.Trim() })
                    .ToList();
            }
            if (req.IpInfoToken != null) s.IpInfoToken = req.IpInfoToken.Trim();
            if (req.SubUrls != null)
                s.SubUrls = req.SubUrls
                    .Where(u => u != null && !string.IsNullOrWhiteSpace(u.Url))
                    .Select(u => new SubUrlEntry { Name = (u.Name ?? "").Trim(), Url = u.Url!.Trim() })
                    .ToList();
            s.Save();
            state.Log("设置已保存", "success");
            return Results.Ok();
        });

        // ── 内核 ──
        api.MapPost("/kernel/install", () =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    state.Log("查询内核最新版本…");
                    var rel = await SubCheckKernel.FetchLatestAsync(u => "https://gh-proxy.com/" + u);
                    if (rel == null)
                    {
                        state.Log("查询 release 失败，请检查网络或稍后重试", "error");
                        return;
                    }
                    state.Log($"最新版本 {rel.Version} ({rel.SizeBytes / 1024 / 1024} MB)，开始下载…");
                    await SubCheckKernel.InstallAsync(rel, p =>
                    {
                        if (p.Phase == "下载" && (int)p.Percent % 10 == 0)
                            state.Log($"  下载 {p.Percent:F0}%");
                    }, default, u => "https://gh-proxy.com/" + u);
                    state.Log($"内核已安装: {rel.Version}", "success");
                }
                catch (Exception ex)
                {
                    state.Log($"内核安装失败: {ex.Message}", "error");
                }
            });
            return Results.Ok(new { started = true });
        });
    }

    /// <summary>逗号分隔的多值参数 → 列表，去空去重。</summary>
    private static List<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    /// <summary>
    /// 汇总订阅来源：手动粘贴的 URL + 搜索结果里验证到节点的链接 + 收藏仓库的链接。
    /// 后两者带仓库备注，测速结果才能映射回仓库。
    /// </summary>
    private static List<SubSource> CollectSources(
        WebState state, string? urls, bool? includeResults, bool? includeFavorites)
    {
        var sources = new List<SubSource>();

        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var raw in urls.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim();
                if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    sources.Add(new SubSource { Url = t.Split('#')[0].Trim(), Repo = "" });
            }
        }

        if (includeResults ?? false)
        {
            foreach (var r in state.Repos)
                foreach (var l in r.Links.Where(l => l.IsValid && l.NodeCount > 0))
                    sources.Add(new SubSource { Url = l.Url, Repo = r.FullName });
        }

        if (includeFavorites ?? false)
        {
            foreach (var r in FavoritesStore.Load())
                foreach (var l in r.Links.Where(l => l.IsValid && l.NodeCount > 0))
                    sources.Add(new SubSource { Url = l.Url, Repo = r.FullName });
        }

        return sources;
    }

    private static async Task WriteEvent(HttpContext ctx, WebState.LogLine l, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            at = l.At.ToString("HH:mm:ss"),
            text = l.Text,
            level = l.Level,
        });
        await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    // ══════════════════ 静态文件 ══════════════════

    private static void MapStatic(WebApplication app)
    {
        // 前端资源内嵌在程序集里：容器镜像只需一个可执行文件，
        // 不用额外拷贝 wwwroot，也不会因为工作目录不同而找不到。
        app.MapGet("/", () => ServeEmbedded("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => ServeEmbedded("app.js", "application/javascript; charset=utf-8"));
        app.MapGet("/style.css", () => ServeEmbedded("style.css", "text/css; charset=utf-8"));
        app.MapGet("/world.svg", () => ServeEmbedded("world.svg", "image/svg+xml; charset=utf-8"));

        // 图标：复用桌面版的 logo.png。它内嵌在 Core 程序集里（桌面版用它做
        // About 对话框的图），CLI 程序集里没有 —— 两个程序集都要找一遍。
        app.MapGet("/logo.png", () =>
        {
            foreach (var asm in new[] { typeof(WebServer).Assembly, typeof(AppEnvironment).Assembly })
            {
                var name = Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
                if (name != null)
                    return Results.Stream(asm.GetManifestResourceStream(name)!, "image/png");
            }
            return Results.NotFound();
        });
    }

    private static IResult ServeEmbedded(string file, string contentType)
    {
        var asm = typeof(WebServer).Assembly;
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            return Results.Text($"未找到内嵌资源 {file}", "text/plain", statusCode: 500);
        return Results.Stream(asm.GetManifestResourceStream(name)!, contentType);
    }

    // ══════════════════ 排序与序列化 ══════════════════

    private static List<RepoInfo> SortRepos(IEnumerable<RepoInfo> source, string field, bool desc)
    {
        Comparison<RepoInfo> cmp = field switch
        {
            "Repo" => (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal),
            "Stars" => (a, b) => a.Stars.CompareTo(b.Stars),
            "LastPush" or "Status" => (a, b) => a.DaysInactive.CompareTo(b.DaysInactive),
            "Commits" => (a, b) => a.CommitsLast7Days.CompareTo(b.CommitsLast7Days),
            "Age" => (a, b) => a.AgeDays.CompareTo(b.AgeDays),
            "Processing" => (a, b) => string.Compare(a.ProcessingType, b.ProcessingType, StringComparison.Ordinal),
            "Nodes" => (a, b) => a.TotalNodes.CompareTo(b.TotalNodes),
            "Links" => (a, b) => a.Links.Count.CompareTo(b.Links.Count),
            "Usable" => (a, b) => SpeedTestStore.SortScore(a.FullName).CompareTo(SpeedTestStore.SortScore(b.FullName)),
            "AvgSpeed" => (a, b) => SpeedTestStore.SortAvgSpeed(a.FullName).CompareTo(SpeedTestStore.SortAvgSpeed(b.FullName)),
            _ => (a, b) => a.Score.CompareTo(b.Score)
        };
        var list = source.ToList();
        list.Sort((a, b) => desc ? cmp(b, a) : cmp(a, b));
        return list;
    }

    private static object ToDto(RepoInfo r)
    {
        var stat = SpeedTestStore.Get(r.FullName);
        return new
        {
            fullName = r.FullName,
            description = r.Description,
            stars = r.Stars,
            lastPush = r.LastPush,
            daysInactive = r.DaysInactive,
            ageDays = r.AgeDays,
            commitsLast7Days = r.CommitsLast7Days,
            distinctActiveDays = r.DistinctActiveDays,
            processingType = r.ProcessingType,
            score = r.Score,
            totalNodes = r.TotalNodes,
            statusText = r.StatusText,
            branch = r.Branch,
            usableScore = stat?.Score ?? -1,
            avgSpeedKbps = stat?.AvgSpeedKbps ?? 0,
            demoted = stat?.Demoted ?? false,
            links = r.Links.Select(l => new
            {
                name = l.Name,
                url = l.Url,
                type = l.Type,
                nodeCount = l.NodeCount,
                valid = l.IsValid,
                analyzed = l.IsAnalyzed,
            }),
        };
    }

    private static object ToNodeDto(SpeedTestNode n) => new
    {
        name = n.Name,
        baseName = n.BaseName,
        type = n.Type,
        server = n.Server,
        port = n.Port,
        speed = n.Speed,
        country = n.Country,
        ip = n.Ip,
        ipRisk = n.IpRisk,
        displayName = n.DisplayName,
        ipInfoCity = n.IpInfoCity,
        ipInfoRegion = n.IpInfoRegion,
        ipInfoOrg = n.IpInfoOrg,
        ipInfoAsn = n.IpInfoAsn,
        ipInfoLoc = n.IpInfoLoc,
        isResidential = n.IsResidential,
        subTag = n.SubTag,
        tlsUdp = n.TlsUdpLabel,
        passed = n.Passed,
        media = n.Media.Select(m => new
        {
            platform = m.Platform,
            short_ = SpeedResultAggregator.ShortPlatform(m.Platform),
            tag = m.Tag,
        }),
    };
}

// ══════════════════ 请求模型 ══════════════════

public sealed class WebOptions
{
    public string Bind = "0.0.0.0";
    public int Port = 8080;
    public string OutputDir = "";
}

public sealed record SearchRequest(
    int? Count, int? SinceDays, int? Concurrency, bool? SkipSearched,
    string? Proxy, bool? AutoProxy, string? Token);

public sealed record SpeedRequest(
    string? Urls, bool? IncludeResults, bool? IncludeFavorites,
    bool? WithHistory, int? KeepRounds);

/// <summary>只测选中节点：URIs 是从清单里勾选的节点 URI 列表。</summary>
public sealed record SelectedSpeedRequest(
    List<string>? Uris, bool? WithHistory, int? KeepRounds);

/// <summary>加载节点清单：来源与测速相同，但只拉取解析、不测速。</summary>
public sealed record CatalogRequest(
    string? Urls, bool? IncludeResults, bool? IncludeFavorites, string? Token);

public sealed record MergeRequest(string? Token);

public sealed record FavoriteRequest(string? FullName);

public sealed record SettingsRequest(
    string? Token, int? RepoCount, int? MinCommitsPerDay,
    int? MinActiveDays, int? InactiveDays, bool? AutoClean,
    string? SubConverterUrl,
    int? SpeedTimeout, int? DownloadMb, int? MinSpeed,
    int? SpeedConcurrent, int? Concurrent, string? SpeedTestUrl,
    bool? MediaCheck, int? MediaCheckTimeout, List<string>? Platforms,
    List<RegexPresetInput>? RegionPresets, string? IpInfoToken,
    List<SubUrlEntryInput>? SubUrls);

public sealed record SubUrlEntryInput(string? Name, string? Url);

public sealed record RegexPresetInput(string? Name, string? Pattern);

public sealed record PipelineRequest(
    int? Count, int? Days, int? Concurrency, bool? SkipSearched, int? HistoryDays, string? Token);
