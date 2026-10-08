using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxyNodeHub;

/// <summary>
/// SubsCheck 测速配置层。
///
/// 三个文件，职责严格分离：
///   config.yaml          用户拥有，权威，本类只按用户显式请求写回
///   config.example.yaml  上游参考副本，只读，随内核版本同步
///   session.yaml         每次测速前生成 = 用户配置 + 少量必须托管的键
///
/// 关键约束：本类不解析 YAML，只做行级键块定位与替换。
/// 因此用户文件里的注释一个字符都不会丢，内核未来新增的键也自动继承 ——
/// 我们不需要知道内核有哪些配置项。
/// </summary>
public static class SpeedTestConfig
{
    // ── 每次测速必须由程序接管的键 ──
    // 其余全部来自用户的 config.yaml，我们不做任何假设。
    public const string KeySubUrls = "sub-urls";
    public const string KeyListenPort = "listen-port";
    public const string KeyApiKey = "api-key";
    public const string KeyEnableWebUi = "enable-web-ui";

    // ── 测速快捷参数（设置页暴露给用户；不接管则沿用用户 config.yaml）──
    public const string KeyTimeout = "timeout";
    public const string KeyDownloadMb = "download-mb";
    public const string KeyMinSpeed = "min-speed";
    public const string KeySpeedConcurrent = "speed-concurrent";
    public const string KeyConcurrent = "concurrent";
    public const string KeySpeedTestUrl = "speed-test-url";

    // ── 流媒体检测 ──
    public const string KeyMediaCheck = "media-check";
    public const string KeyMediaCheckTimeout = "media-check-timeout";
    public const string KeyPlatforms = "platforms";

    /// <summary>
    /// 覆写订阅的规则文件 URL。内核默认写死 http://127.0.0.1:8199/...，
    /// 依赖 listen-port —— 我们每次测速都把 listen-port 改成随机端口，
    /// 这个键必须跟着改，否则 sub-store 会去连 8199 拿规则文件而
    /// ECONNREFUSED（表现为「获取 mihomo.yaml 失败，状态码 500」）。
    /// </summary>
    public const string KeyMihomoOverwriteUrl = "mihomo-overwrite-url";

    /// <summary>
    /// 把 mihomo-overwrite-url 指向本轮的监听端口。
    ///
    /// 只改指向本机（127.0.0.1 / localhost）的 URL：那是内核的内置自引用，
    /// 端口随 listen-port 浮动，必须同步。用户若把它改成了自己的外部规则
    /// 地址，那是他的自定义配置，原样保留。
    /// </summary>
    public static string RetargetLocalOverwriteUrl(string yaml, int port)
    {
        var current = GetScalar(yaml, KeyMihomoOverwriteUrl);
        if (string.IsNullOrEmpty(current)) return yaml;

        if (!Uri.TryCreate(current, UriKind.Absolute, out var uri)) return yaml;
        if (!uri.IsLoopback) return yaml;   // 用户自定义的外部地址，不动

        var retargeted = new UriBuilder(uri) { Port = port }.Uri.ToString();
        return SetScalar(yaml, KeyMihomoOverwriteUrl, retargeted);
    }

    public static string RootDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PROXYNODEHUB_SUBCHECK_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return Path.Combine(AppEnvironment.UserDataDir, "subcheck");
        }
    }

    public static string ConfigDir => Path.Combine(RootDir, "config");
    public static string ConfigPath => Path.Combine(ConfigDir, "config.yaml");
    public static string ExamplePath => Path.Combine(ConfigDir, "config.example.yaml");
    public static string SessionPath => Path.Combine(ConfigDir, "session.yaml");
    public static string HistoryDir => Path.Combine(RootDir, "history");
    public static string OutputDir => Path.Combine(RootDir, "output");

    /// <summary>
    /// 内核可执行文件路径。用户在「内核状态」里选了自定义位置就用那个，
    /// 否则用默认位置。空串与不存在的路径都回落到默认。
    /// 容器里可用 PROXYNODEHUB_KERNEL_PATH 指定（Dockerfile 会预装内核）。
    /// </summary>
    public static string KernelPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PROXYNODEHUB_KERNEL_PATH");
            if (!string.IsNullOrWhiteSpace(env))
            {
                var p = env.Trim();
                // 允许直接给可执行文件路径，或给所在目录
                if (File.Exists(p)) return p;
                if (Directory.Exists(p))
                {
                    var inDir = Path.Combine(p, AppEnvironment.KernelExecutableName);
                    if (File.Exists(inDir)) return inDir;
                }
            }

            try
            {
                var custom = AppSettings.Load().KernelPath;
                if (!string.IsNullOrWhiteSpace(custom)
                    && Path.IsPathRooted(custom)
                    && File.Exists(Path.Combine(custom, AppEnvironment.KernelExecutableName)))
                    return Path.Combine(custom, AppEnvironment.KernelExecutableName);
            }
            catch { }
            return Path.Combine(RootDir, AppEnvironment.KernelExecutableName);
        }
    }

    /// <summary>内核所在目录（配置/输出/历史都相对它，不随自定义位置漂移）。</summary>
    public static string KernelDirDefault => RootDir;

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(HistoryDir);
        Directory.CreateDirectory(OutputDir);
    }

    // ══════════════════ 键发现 ══════════════════

    /// <summary>顶层键名 — 顶格书写、非注释、以 : 结尾的行。</summary>
    private static readonly Regex KeyLine = new(@"^([A-Za-z][A-Za-z0-9_-]*):", RegexOptions.Compiled);

    /// <summary>块的结束：下一个顶格且非注释、非空的行。</summary>
    private static readonly Regex NextTopLevel = new(@"^(\S|$)", RegexOptions.Compiled);

    public static List<string> DiscoverKeys(string yaml)
    {
        var keys = new List<string>();
        foreach (var line in yaml.Split('\n'))
        {
            var m = KeyLine.Match(line);
            if (m.Success && !keys.Contains(m.Groups[1].Value))
                keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    /// <summary>取顶层键对应的原始文本块（不含键名行之后的下一顶层键）。</summary>
    public static string? GetBlock(string yaml, string key)
    {
        var lines = yaml.Split('\n');
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var m = KeyLine.Match(lines[i]);
            if (m.Success && m.Groups[1].Value == key) { start = i; break; }
        }
        if (start < 0) return null;

        var sb = new StringBuilder();
        int end = start + 1;
        for (; end < lines.Length; end++)
        {
            if (Regex.IsMatch(lines[end], @"^\S")) break;   // 顶格 → 新键
            sb.Append(lines[end]).Append('\n');
        }
        return string.Concat(lines[start], "\n", sb.ToString());
    }

    /// <summary>只取值部分（去掉键名行），已去首尾空白与行尾注释。</summary>
    public static string? GetScalar(string yaml, string key)
    {
        var block = GetBlock(yaml, key);
        if (block == null) return null;

        var lines = block.Split('\n');
        var first = lines[0];
        var idx = first.IndexOf(':');
        var val = idx < 0 ? "" : first[(idx + 1)..];
        val = StripTrailingComment(val).Trim();

        // 键行无值且带有缩进子行 → 这是列表/映射，不是标量
        if (val.Length == 0 && lines.Skip(1).Any(l => l.Trim().Length > 0))
            return null;

        if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
            val = val[1..^1];
        return val;
    }

    /// <summary>
    /// 去掉 YAML 行尾注释。YAML 规则：# 只有在行首或前面有空白时才是注释起点，
    /// 且不作用于引号内 —— subs-check 的订阅链接支持 `URL#备注`，所以不能见 # 就切。
    /// </summary>
    private static string StripTrailingComment(string s)
    {
        bool inS = false, inD = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'' && !inD) inS = !inS;
            else if (c == '"' && !inS) inD = !inD;
            else if (c == '#' && !inS && !inD && (i == 0 || char.IsWhiteSpace(s[i - 1])))
                return s[..i];
        }
        return s;
    }

    // ══════════════════ 写回 ══════════════════════

    /// <summary>
    /// 行级替换顶层键块；不存在则追加到文件末尾。
    /// 语义：永不删除用户的其他键，注释与格式原样保留。
    /// </summary>
    public static string SetBlock(string yaml, string key, string block)
    {
        var lines = yaml.Split('\n').ToList();
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var m = KeyLine.Match(lines[i]);
            if (m.Success && m.Groups[1].Value == key) { start = i; break; }
        }

        var newBlock = block.TrimEnd('\n', '\r');
        if (start < 0)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            lines.Add(newBlock);
            return string.Join("\n", lines) + "\n";
        }

        int end = start + 1;
        for (; end < lines.Count; end++)
            if (Regex.IsMatch(lines[end], @"^\S")) break;

        lines.RemoveRange(start, end - start);
        // 原块前的缩进/空行保持不动，只替换键行本身及其从属行
        lines.Insert(start, newBlock);
        return string.Join("\n", lines);
    }

    public static string SetScalar(string yaml, string key, object? value)
    {
        // 替换标量时保留用户写在键行上的行尾注释
        var old = GetBlock(yaml, key);
        var comment = "";
        if (old != null)
        {
            var firstLine = old.Split('\n')[0];
            var after = firstLine[(firstLine.IndexOf(':') + 1)..];
            var cut = StripTrailingComment(after);
            var idx = after.IndexOf('#', StringComparison.Ordinal);
            if (idx >= 0) comment = "  " + after[idx..].TrimEnd();
        }
        return SetBlock(yaml, key, $"{key}: {Quote(value)}{comment}");
    }

    public static string SetStringList(string yaml, string key, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return SetScalar(yaml, key, "");
        var sb = new StringBuilder($"{key}:\n");
        foreach (var it in list) sb.Append($"  - \"{Escape(it)}\"\n");
        return SetBlock(yaml, key, sb.ToString());
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Quote(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        int or long or double or float => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!,
        string s => NeedsQuote(s) ? $"\"{Escape(s)}\"" : s,
        _ => v.ToString() ?? ""
    };

    private static bool NeedsQuote(string s)
    {
        if (s.Length == 0) return false;
        if (s.StartsWith("#") || s.StartsWith("&") || s.StartsWith("*") ||
            s.StartsWith("!") || s.StartsWith("|") || s.StartsWith(">") ||
            s.StartsWith("%") || s.StartsWith("@") || s.StartsWith("`")) return true;
        return s.Any(c => ":#{}[],&*?|-<>=!%@`\"'".IndexOf(c) >= 0) || s != s.Trim();
    }

    // ══════════════════ 读写文件 ══════════════════

    public static string ReadConfig()
    {
        try { return File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// 把「设置页暴露的快捷参数」注入到 session.yaml。
    /// 只在用户显式设过（非 null/非空）时覆盖 config.yaml 的原值 ——
    /// 这样用户在 config.yaml 里手写的高级值不会被设置页的「空」覆盖。
    /// </summary>
    public static string ApplySettingsOverrides(string yaml, AppSettings s)
    {
        if (s.SpeedTimeout.HasValue)
            yaml = SetScalar(yaml, KeyTimeout, s.SpeedTimeout.Value);
        if (s.DownloadMb.HasValue)
            yaml = SetScalar(yaml, KeyDownloadMb, s.DownloadMb.Value);
        if (s.MinSpeed.HasValue)
            yaml = SetScalar(yaml, KeyMinSpeed, s.MinSpeed.Value);
        if (s.SpeedConcurrent.HasValue)
            yaml = SetScalar(yaml, KeySpeedConcurrent, s.SpeedConcurrent.Value);
        if (s.Concurrent.HasValue)
            yaml = SetScalar(yaml, KeyConcurrent, s.Concurrent.Value);
        if (!string.IsNullOrWhiteSpace(s.SpeedTestUrl))
            yaml = SetScalar(yaml, KeySpeedTestUrl, s.SpeedTestUrl.Trim());

        // 流媒体检测
        if (s.MediaCheck.HasValue)
            yaml = SetScalar(yaml, KeyMediaCheck, s.MediaCheck.Value);
        if (s.MediaCheckTimeout.HasValue)
            yaml = SetScalar(yaml, KeyMediaCheckTimeout, s.MediaCheckTimeout.Value);
        if (s.Platforms != null)
            yaml = SetStringList(yaml, KeyPlatforms, s.Platforms);

        return yaml;
    }

    public static void WriteConfig(string yaml)
    {
        EnsureDirs();
        // 失败会由 AtomicFile 清 .tmp 并上抛，调用方（参数弹窗）弹窗提示用户
        AtomicFile.Write(ConfigPath, yaml);
    }

    public static string ReadExample()
    {
        try { return File.Exists(ExamplePath) ? File.ReadAllText(ExamplePath) : ""; }
        catch { return ""; }
    }

    // ══════════════════ 配置合并助手 ══════════════════

    public class KeyDiff
    {
        public List<string> Added = new();     // 上游有、用户无
        public List<string> Removed = new();   // 用户有、上游无
        public List<string> Kept = new();
    }

    /// <summary>
    /// 内核升级后比对上游示例与用户配置的顶层键。
    /// 上游新增的键给出官方注释+默认值，由用户决定是否合入。
    /// </summary>
    public static KeyDiff DiffAgainstExample()
    {
        var upstream = DiscoverKeys(ReadExample());
        var mine = DiscoverKeys(ReadConfig());
        var d = new KeyDiff();
        foreach (var k in upstream)
        {
            if (mine.Contains(k)) d.Kept.Add(k);
            else d.Added.Add(k);
        }
        foreach (var k in mine)
            if (!upstream.Contains(k)) d.Removed.Add(k);
        return d;
    }

    /// <summary>从上游示例取出某个键的完整块（含注释），供合并用。</summary>
    public static string? ExampleBlock(string key) => GetBlock(ReadExample(), key);

    /// <summary>把上游键块原样追加进用户配置。</summary>
    public static string MergeFromExample(string key)
    {
        var block = ExampleBlock(key);
        if (string.IsNullOrWhiteSpace(block)) return ReadConfig();
        return SetBlock(ReadConfig(), key, block);
    }
}
