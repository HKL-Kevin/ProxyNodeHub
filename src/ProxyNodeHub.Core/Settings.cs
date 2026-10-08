using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ProxyNodeHub;

/// <summary>一条正则预置：Name 显示在 chip 上，Pattern 是正则片段。</summary>
public class RegexPreset
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";

    /// <summary>内置默认地区预置。来自 SpeedTestDefaults.cs 的地区段 + 常见扩展。</summary>
    public static List<RegexPreset> Defaults => new()
    {
        new() { Name = "香港", Pattern = "香港|HK|Hong Kong" },
        new() { Name = "台湾", Pattern = "台湾|TW|Taiwan" },
        new() { Name = "新加坡", Pattern = "新加坡|SG|Singapore" },
        new() { Name = "日本", Pattern = "日本|JP|Japan" },
        new() { Name = "美国", Pattern = "美国|US|United States" },
        new() { Name = "韩国", Pattern = "韩国|KR|Korea" },
        new() { Name = "英国", Pattern = "英国|UK|United Kingdom" },
        new() { Name = "德国", Pattern = "德国|DE|Germany" },
    };
}

/// <summary>一条持久化的手动订阅 URL。</summary>
public class SubUrlEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class AppSettings
{
    /// <summary>
    /// GitHub 个人访问令牌。明文存 exe 同目录 settings.json。
    ///
    /// 为什么不用 DPAPI 加密：DPAPI 绑定"当前用户 + 当前机器"，把 json 拷到
    /// 别的机器或换用户就解不出来（GetToken 返回空）。用户要的是"作为配置
    /// 放在 exe 同目录"——那意味着可随工具迁移、可手动查看编辑，明文才成立。
    /// 敏感字段放同目录本身是取舍：软件不联网时风险有限，明文换来的是可迁移。
    /// 首次打开默认 "",即未设置。
    /// </summary>
    public string GithubKey { get; set; } = "";
    public int RepoCount { get; set; } = 30;
    public int MinCommitsPerDay { get; set; } = 0;
    public int MinActiveDays { get; set; } = 0;
    public int InactiveDays { get; set; } = 7;
    public bool AutoClean { get; set; } = true;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowW { get; set; } = 1180;
    public int WindowH { get; set; } = 740;
    public bool Maximized { get; set; }
    public bool InspectorOpen { get; set; }

    /// <summary>内核可执行文件路径。空 = 用默认位置。</summary>
    public string KernelPath { get; set; } = "";

    /// <summary>
    /// 订阅转换后端地址（MiniSubConvert / subconverter）。
    /// 留空 = 不走转换，直接拉原始订阅。原始订阅若是 Clash YAML
    /// （proxies: 段）则解析不出节点 —— NodeParser 只认 URI 列表。
    /// 填写后，清单加载会把每个订阅 URL 改写成
    ///   {SubConverterUrl}/sub?target=uri&amp;url={原URL 编码}
    /// 拿到的就是纯 URI 列表，所有格式统一被解析。
    /// 地址含 secret 路径，如 http://127.0.0.1:3000/minisubconvert
    /// </summary>
    public string SubConverterUrl { get; set; } = "";

    /// <summary>自动检测内核新版本并静默安装，免去手动下载。</summary>
    public bool KernelAutoUpdate { get; set; } = true;

    /// <summary>用户已知悉内核为 GPL-3.0 且体积约 55MB（首次提示过就不再打扰）。</summary>
    public bool KernelNoticeShown { get; set; }

    // ── 测速快捷参数（写入 config.yaml，每次测速会读出来注入 session.yaml）──
    // 这些是用户最常调的旋钮；其余高级键请直接编辑 config.yaml。
    // 用 nullable int 而非 int，这样「未设置」与「显式设为 0」能区分。
    /// <summary>节点最大延迟，毫秒。超时即视为不可达。</summary>
    public int? SpeedTimeout { get; set; }
    /// <summary>每节点测速下载数据量上限，MB。0=不限。</summary>
    public int? DownloadMb { get; set; }
    /// <summary>最低测速，KB/s。低于此值丢弃。</summary>
    public int? MinSpeed { get; set; }
    /// <summary>测速并发数。建议低于 concurrent 避免抢带宽。</summary>
    public int? SpeedConcurrent { get; set; }
    /// <summary>测活并发数。</summary>
    public int? Concurrent { get; set; }
    /// <summary>测速下载文件 URL。很多节点会屏蔽 GitHub 大文件，建议自建。</summary>
    public string SpeedTestUrl { get; set; } = "";

    // ── 流媒体检测 ──
    /// <summary>是否对流媒体解锁+IP 欺诈检测。关闭则 ip/ipRisk/media 字段全空。</summary>
    public bool? MediaCheck { get; set; }
    /// <summary>流媒体检测超时（秒）。默认 5。</summary>
    public int? MediaCheckTimeout { get; set; }
    /// <summary>要检测的平台列表。空 = 用 config.yaml 原值。</summary>
    public List<string> Platforms { get; set; } = new();

    /// <summary>
    /// 节点清单筛选页的地区预置正则分组。空列表 = 用内置默认。
    /// 每条 {Name, Pattern}，Name 显示在 chip 上，Pattern 是正则片段。
    /// 多选时前端把各 Pattern OR-join 后塞进 /api/catalog 的 keyword。
    /// </summary>
    public List<RegexPreset> RegionPresets { get; set; } = new();

    /// <summary>
    /// ipinfo.io 的 token，用于测速结果按出口 IP 批量查城市/省份/ASN/ISP。
    /// 留空 = 不查详细 IP 信息（只显示内核给的国家码）。
    /// 免费版 50000 次/月，注册即得：https://ipinfo.io/signup
    /// </summary>
    public string IpInfoToken { get; set; } = "";

    /// <summary>
    /// 持久化的手动订阅 URL 清单。每条 {Name, Url}。
    /// 测速页「加载节点清单」时勾选要用的订阅，而不是每次重新粘贴 URL。
    /// </summary>
    public List<SubUrlEntry> SubUrls { get; set; } = new();

    /// <summary>首次加载时填入内置默认地区预置（若用户没自定义过）。</summary>
    public List<RegexPreset> EnsureDefaultRegionPresets() => RegionPresets.Count == 0
        ? RegexPreset.Defaults
        : RegionPresets;

    private static string Dir
    {
        get
        {
            // 用户要求 key 作为配置放 exe 同目录的 json 文件，所以设置文件
            // 跟 exe 走 —— 工具拷到哪，设置就到哪（便携）。
            // 桌面版由 AppEnvironment 解析为 exe 所在目录；容器版走 XDG/环境变量。
            return AppEnvironment.ConfigDir;
        }
    }

    private static string SettingsPath => Path.Combine(Dir, "settings.json");

    /// <summary>加载后的单例缓存。KernelPath 等 getter 每次都 Load，之前是每次
    /// 读盘反序列化；缓存后 Load 只读一次盘，之后全部走内存。</summary>
    private static AppSettings? _cache;

    public static AppSettings Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(SettingsPath))
                _cache = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), AppJsonContext.Default.AppSettings) ?? new();
        }
        catch { }
        _cache ??= new AppSettings();
        return _cache;
    }

    /// <summary>显式失效缓存，强制下次 Load 重新读盘（理论上只有外部改文件才需要）。</summary>
    public static void Invalidate() => _cache = null;

    public void Save()
    {
        _cache = this;   // 同步缓存，避免 Save 后又被旧缓存覆盖
        try
        {
            AtomicFile.Write(SettingsPath, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
        }
        catch { }
    }

    public string GetToken() => GithubKey ?? "";

    public void SetToken(string token) => GithubKey = token ?? "";
}

public static class ResultCache
{
    private static string Dir => AppEnvironment.UserDataDir;

    private static string CachePath => Path.Combine(Dir, "cache.json");

    public static void Save(List<RepoInfo> repos)
    {
        try
        {
            var data = new CacheData { SavedAt = DateTime.Now, Repos = repos };
            AtomicFile.Write(CachePath, JsonSerializer.Serialize(data, AppJsonContext.Default.CacheData));
        }
        catch { }
    }

    public static CacheData? Load()
    {
        try
        {
            if (File.Exists(CachePath))
                return JsonSerializer.Deserialize(File.ReadAllText(CachePath), AppJsonContext.Default.CacheData);
        }
        catch { }
        return null;
    }

    public static void Clear()
    {
        try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
    }
}

public static class FavoritesStore
{
    private static string Dir => AppEnvironment.UserDataDir;

    private static string StorePath => Path.Combine(Dir, "favorites.json");

    /// <summary>收藏列表缓存。SpeedTestPanel 里三处都 Load，之前每次读盘。</summary>
    private static List<RepoInfo>? _cache;

    public static List<RepoInfo> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(StorePath))
                _cache = JsonSerializer.Deserialize(File.ReadAllText(StorePath), AppJsonContext.Default.RepoList) ?? new();
        }
        catch { }
        _cache ??= new List<RepoInfo>();
        return _cache;
    }

    public static void Save(List<RepoInfo> repos)
    {
        _cache = repos;   // 同步缓存
        try
        {
            AtomicFile.Write(StorePath, JsonSerializer.Serialize(repos, AppJsonContext.Default.RepoList));
        }
        catch { }
    }
}
