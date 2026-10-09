using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

/// <summary>
/// 按节点出口 IP 查询详细信息（城市/省份/ASN/ISP），用 ipinfo.io 批量端点。
///
/// 内核的 GetProxyCountry 只解析 country_code，城市/ASN/ISP 字段被它丢弃。
/// 这里自己调 ipinfo.io /batch 端点补全 —— 一次最多 100 个 IP，
/// 50000 次/月免费额度对几千节点（去重后通常几百 IP）绰绰有余。
///
/// 结果缓存到 /data/ip_cache.json 跨重启复用，同一个 IP 不重复查。
/// </summary>
public static class IpInfoService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // 内存缓存：IP → 详情。启动时从盘上读回。
    private static readonly ConcurrentDictionary<string, IpInfo> _cache = new();
    private static string CachePath => Path.Combine(AppEnvironment.UserDataDir, "ip_cache.json");

    /// <summary>启动时从盘上把缓存读回来。</summary>
    public static void LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var dict = JsonSerializer.Deserialize(File.ReadAllText(CachePath),
                    AppJsonContext.Default.IpInfoDict);
                if (dict != null)
                    foreach (var kv in dict) _cache[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    /// <summary>从内存缓存中获取指定 IP 的信息，不存在或入参为空则返回 null。</summary>
    public static IpInfo? GetCached(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        return _cache.TryGetValue(ip, out var info) ? info : null;
    }

    /// <summary>把缓存落盘（原子写）。</summary>
    public static void SaveCache()
    {
        try
        {
            var dict = new Dictionary<string, IpInfo>(_cache);
            AtomicFile.Write(CachePath, JsonSerializer.Serialize(dict, AppJsonContext.Default.IpInfoDict));
        }
        catch { }
    }

    /// <summary>
    /// 批量查一组 IP，返回 IP → IpInfo 映射。
    /// 已缓存的不查，未缓存且 token 非空的才调 ipinfo.io。
    /// 每 100 个 IP 一批（ipinfo.io batch 端点上限）。
    /// </summary>
    public static async Task<Dictionary<string, IpInfo>> LookupAsync(
        IEnumerable<string> ips, string? token, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, IpInfo>();
        if (string.IsNullOrWhiteSpace(token)) return result;

        // 去重 + 过滤空值 + 过滤已缓存
        var todo = new List<string>();
        foreach (var ip in ips.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
        {
            if (_cache.TryGetValue(ip, out var info)) result[ip] = info;
            else todo.Add(ip);
        }

        if (todo.Count == 0)
        {
            if (log != null && result.Count > 0) log($"IP 信息全部命中缓存（{result.Count} 个）");
            return result;
        }

        // 按 100 个一批调 ipinfo.io /batch
        const int BATCH = 100;
        var batchNum = (todo.Count + BATCH - 1) / BATCH;
        for (int i = 0; i < todo.Count; i += BATCH)
        {
            ct.ThrowIfCancellationRequested();
            var batch = todo.Skip(i).Take(BATCH).ToList();
            try
            {
                var batchResult = await QueryBatchAsync(batch, token!, ct);
                foreach (var kv in batchResult)
                {
                    _cache[kv.Key] = kv.Value;
                    result[kv.Key] = kv.Value;
                }
                if (log != null) log($"  IP 信息 {i / BATCH + 1}/{batchNum}：{batchResult.Count} 个");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (log != null) log($"  IP 信息批次 {i / BATCH + 1} 查询失败：{ex.Message}");
            }
        }

        SaveCache();
        return result;
    }

    /// <summary>调 ipinfo.io batch 端点。</summary>
    private static async Task<Dictionary<string, IpInfo>> QueryBatchAsync(
        List<string> ips, string token, CancellationToken ct)
    {
        var result = new Dictionary<string, IpInfo>();

        // POST https://ipinfo.io/batch?token=...
        // body: ["1.2.3.4","5.6.7.8",...]
        // 返回: {"1.2.3.4": {...}, "5.6.7.8": {...}}
        var url = $"https://ipinfo.io/batch?token={Uri.EscapeDataString(token)}";
        var body = JsonSerializer.Serialize(ips);

        using var resp = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode) return result;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // 每个 IP 的返回值可能含 error 字段（查不到的 IP）
        foreach (var ip in ips)
        {
            if (!doc.RootElement.TryGetProperty(ip, out var el)) continue;
            if (el.ValueKind != JsonValueKind.Object) continue;

            // error 字段存在说明查不到
            if (el.TryGetProperty("error", out _)) continue;

            result[ip] = new IpInfo
            {
                Ip = ip,
                City = GetString(el, "city"),
                Region = GetString(el, "region"),
                Country = GetString(el, "country"),
                Org = GetString(el, "org"),
                Asn = GetString(el, "asn") ?? ParseAsnFromOrg(GetString(el, "org")),
                Loc = GetString(el, "loc"),
            };
        }

        return result;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>ipinfo.io 的 org 字段格式 "AS12345 Comcast Cable" → 提取 ASN。</summary>
    private static string? ParseAsnFromOrg(string? org)
    {
        if (string.IsNullOrWhiteSpace(org)) return null;
        var sp = org.TrimStart().Split(' ', 2);
        return sp[0].StartsWith("AS", StringComparison.Ordinal) ? sp[0] : null;
    }
}

/// <summary>一个 IP 的详细地理/归属信息。</summary>
public class IpInfo
{
    public string Ip { get; set; } = "";
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }
    public string? Org { get; set; }
    public string? Asn { get; set; }
    /// <summary>经纬度，格式 "lat,lng"（如 "38.0088,-122.1175"），来自 ipinfo.io 的 loc 字段。</summary>
    public string? Loc { get; set; }
}
