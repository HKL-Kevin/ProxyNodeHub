using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

// ── 加速代理信息 ──
public class ProxyMirror
{
    public string Name = "";
    public string Prefix = "";       // 代理前缀 (raw.githubusercontent.com/owner/repo/... → {prefix}https://raw.githubusercontent.com/owner/repo/...)
    public string Type = "";         // "直连" / "代理" / "CDN"
    public bool IsJsDelivr;          // jsDelivr 需要路径转换
    public long LatencyMs = -1;      // -1 = 未测速, -2 = 连接超时/失败
    public bool IsDefault;           // 是否为默认(直连)
}

public class GitHubService
{
    private readonly HttpClient _httpApi;
    private readonly HttpClient _httpRaw;
    private readonly string? _token;

    private const string ApiBase = "https://api.github.com";
    private const string RawBase = "https://raw.githubusercontent.com";
    private const string RawBaseLen = "https://raw.githubusercontent.com/";

    /// <summary>
    /// 统一 UA。历史上有两处硬编码 "4.9"/"4.5" 各写各的，导致不一致；
    /// 收口到这里，与 csproj 的版本号一致。
    /// </summary>
    private const string UserAgent = "ProxyNodeHub/0.0.1";

    public bool HasToken => !string.IsNullOrEmpty(_token);

    // 当前选中的代理 (null = 直连)
    public ProxyMirror? CurrentProxy { get; set; }

    // 所有可用代理列表
    public static readonly List<ProxyMirror> AllProxies = new()
    {
        new ProxyMirror { Name = "直连 (无代理)", Prefix = "", Type = "直连", IsDefault = true },
        new ProxyMirror { Name = "ghfast.top", Prefix = "https://ghfast.top/", Type = "代理" },
        new ProxyMirror { Name = "gh-proxy.com", Prefix = "https://gh-proxy.com/", Type = "代理" },
        new ProxyMirror { Name = "ghproxy.net", Prefix = "https://ghproxy.net/", Type = "代理" },
        new ProxyMirror { Name = "git.yylx.win", Prefix = "https://git.yylx.win/", Type = "代理" },
        new ProxyMirror { Name = "cdn.akaere.online", Prefix = "https://cdn.akaere.online/", Type = "CDN" },
        new ProxyMirror { Name = "gh.jasonzeng.dev", Prefix = "https://gh.jasonzeng.dev/", Type = "代理" },
        new ProxyMirror { Name = "down.mxw.xx.kg", Prefix = "https://down.mxw.xx.kg/", Type = "代理" },
        new ProxyMirror { Name = "github.tbap.top", Prefix = "https://github.tbap.top/", Type = "代理" },
        new ProxyMirror { Name = "ghm.078465.xyz", Prefix = "https://ghm.078465.xyz/", Type = "代理" },
        new ProxyMirror { Name = "gh.monlor.com", Prefix = "https://gh.monlor.com/", Type = "代理" },
        new ProxyMirror { Name = "jsDelivr (cdn)", Prefix = "https://cdn.jsdelivr.net/", Type = "CDN", IsJsDelivr = true },
        new ProxyMirror { Name = "jsDelivr (fastly)", Prefix = "https://fastly.jsdelivr.net/", Type = "CDN", IsJsDelivr = true },
        new ProxyMirror { Name = "jsDelivr (testing)", Prefix = "https://testingcf.jsdelivr.net/", Type = "CDN", IsJsDelivr = true },
    };

    public GitHubService(string? token = null)
    {
        _token = token;
        _httpApi = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _httpRaw = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var h in new[] { _httpApi, _httpRaw })
        {
            h.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            h.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        }
        if (!string.IsNullOrEmpty(token))
            _httpApi.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }

    // ── REST: 搜索 ──
    public async Task<List<GitHubRepo>> SearchReposAsync(string query, int perPage, CancellationToken ct = default)
    {
        try
        {
            var url = $"{ApiBase}/search/repositories?q={Uri.EscapeDataString(query)}&sort=updated&order=desc&per_page={perPage}";
            var resp = await _httpApi.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");
            return items.EnumerateArray()
                .Select(i => JsonSerializer.Deserialize(i.GetRawText(), AppJsonContext.Default.GitHubRepo)!)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new List<GitHubRepo>(); }  // 超时
        catch { return new List<GitHubRepo>(); }
    }

    // ── REST: 最近提交 ──
    public async Task<List<GitHubCommit>> GetRecentCommitsAsync(string fullName, int days = 7, CancellationToken ct = default)
    {
        try
        {
            var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var resp = await _httpApi.GetAsync($"{ApiBase}/repos/{fullName}/commits?since={since}&per_page=100", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<GitHubCommit>>(json) ?? new();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new List<GitHubCommit>(); }  // 超时
        catch { return new List<GitHubCommit>(); }
    }

    // ── REST: 获取仓库文件树 ──
    public async Task<List<string>> GetFileTreeAsync(string fullName, CancellationToken ct = default)
    {
        try
        {
            var resp = await _httpApi.GetAsync($"{ApiBase}/repos/{fullName}/git/trees/main?recursive=1", ct);
            if (!resp.IsSuccessStatusCode)
                resp = await _httpApi.GetAsync($"{ApiBase}/repos/{fullName}/git/trees/master?recursive=1", ct);
            if (!resp.IsSuccessStatusCode) return new List<string>();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var tree = doc.RootElement.GetProperty("tree");
            var files = new List<string>();
            foreach (var item in tree.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "blob")
                {
                    var path = item.GetProperty("path").GetString();
                    if (!string.IsNullOrEmpty(path) && path.Length < 200)
                        files.Add(path);
                }
            }
            return files;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new List<string>(); }
    }

    // ── REST: 获取 README.md 内容 ──
    public async Task<string?> GetReadmeAsync(string fullName, string branch = "main", CancellationToken ct = default)
    {
        return await GetRawFileAsync(fullName, "README.md", branch, ct);
    }

    // ── 代理测速 ──
    public static async Task<ProxyMirror> TestProxyLatencyAsync(ProxyMirror proxy, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

            string testUrl;
            if (proxy.IsDefault)
                testUrl = "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub";
            else if (proxy.IsJsDelivr)
                testUrl = proxy.Prefix + "gh/Pawdroid/Free-servers@main/sub";
            else
                testUrl = proxy.Prefix + "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub";

            var resp = await http.GetAsync(testUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                await resp.Content.ReadAsStringAsync(ct);
                proxy.LatencyMs = sw.ElapsedMilliseconds;
            }
            else
            {
                proxy.LatencyMs = -2;  // HTTP 错误
            }
        }
        catch (OperationCanceledException)
        {
            proxy.LatencyMs = -1;  // 取消
        }
        catch
        {
            proxy.LatencyMs = -2;  // 超时/连接失败
        }
        sw.Stop();
        return proxy;
    }

    // ── 批量测速所有代理 ──
    public static async Task<List<ProxyMirror>> TestAllProxiesAsync(CancellationToken ct = default)
    {
        var tasks = AllProxies.Select(p => TestProxyLatencyAsync(p, ct));
        await Task.WhenAll(tasks);
        // 按延迟排序: 可用的在前 (延迟升序), 超时的在后
        return AllProxies
            .OrderBy(p => p.LatencyMs < 0 ? int.MaxValue : p.LatencyMs)
            .ThenBy(p => p.IsDefault ? 0 : 1)
            .ToList();
    }

    // ── 下载 raw 文件 (使用当前选中的代理) ──
    public async Task<string?> GetUrlAsync(string url, CancellationToken ct = default)
    {
        // 转换后端 / 非 raw URL 不走加速镜像：那些地址本来就不在
        // raw.githubusercontent.com 上，套代理前缀只会让它 404。
        bool useProxy = url.StartsWith(RawBaseLen, StringComparison.OrdinalIgnoreCase);

        // 1. 如果选了代理, 优先用代理
        if (useProxy && CurrentProxy != null && !CurrentProxy.IsDefault)
        {
            var proxiedUrl = BuildProxiedUrl(url, CurrentProxy);
            if (proxiedUrl != null)
            {
                var content = await TryGetAsync(proxiedUrl, ct);
                if (content != null) return content;
            }
        }

        // 2. 直连
        var direct = await TryGetAsync(url, ct);
        if (direct != null) return direct;

        // 3. 直连失败 → 自动尝试所有代理 (按延迟排序)
        if (useProxy)
        {
            var sortedProxies = AllProxies
                .Where(p => !p.IsDefault && p.LatencyMs > 0)
                .OrderBy(p => p.LatencyMs);

            foreach (var proxy in sortedProxies)
            {
                var proxiedUrl = BuildProxiedUrl(url, proxy);
                if (proxiedUrl == null) continue;
                var content = await TryGetAsync(proxiedUrl, ct);
                if (content != null) return content;
            }
        }

        return null;
    }

    private static string? BuildProxiedUrl(string rawUrl, ProxyMirror proxy)
    {
        if (!rawUrl.StartsWith(RawBaseLen, StringComparison.OrdinalIgnoreCase))
            return null;

        if (proxy.IsJsDelivr)
        {
            // raw.githubusercontent.com/{owner}/{repo}/{branch}/{path} → {prefix}gh/{owner}/{repo}@{branch}/{path}
            var rest = rawUrl[RawBaseLen.Length..];
            var parts = rest.Split(new[] { '/' }, 4);
            if (parts.Length == 4)
                return $"{proxy.Prefix}gh/{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}";
            return null;
        }

        // 代理型: {prefix}{原始URL}
        return proxy.Prefix + rawUrl;
    }

    private async Task<string?> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await _httpRaw.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 真正的用户取消
        }
        catch (OperationCanceledException)
        {
            return null;  // HttpClient 超时, 视为网络失败而非取消
        }
        catch { return null; }
    }

    public async Task<string?> GetRawFileAsync(string fullName, string path, string branch = "main", CancellationToken ct = default)
    {
        // 真实分支优先。仓库默认分支是 master 时，先前实现会先浪费一次
        // main 的 404 再回退；且调用方拿到的 URL 仍写死 main，得到 404 死链。
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(branch)) candidates.Add(branch);
        foreach (var b in new[] { "main", "master" })
            if (!candidates.Contains(b)) candidates.Add(b);

        foreach (var b in candidates)
        {
            var content = await GetUrlAsync($"{RawBase}/{fullName}/{b}/{path}", ct);
            if (!string.IsNullOrEmpty(content)) return content;
            if (ct.IsCancellationRequested) return null;
        }
        return null;
    }
}
