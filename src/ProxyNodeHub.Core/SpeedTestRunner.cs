using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

// ══════════════════ 结果模型（对应 save/results.go 的 JSON） ══════════════════

public class SpeedTestStatus
{
    public bool Checking;
    public int ProxyCount;
    public int Available;
    public int Progress;
    public int Phase;
    public SpeedTestPipeline? Pipeline;
}

public class SpeedTestPipeline
{
    public int Total;
    public int AliveDone;
    public int AlivePass;
    public int MediaDone;
    public int FilterPass;
    public int SpeedDone;
    public int SpeedPass;
}

public class MediaTag
{
    public string Platform { get; set; } = "";
    public string Tag { get; set; } = "";
}

public class NodeDetail
{
    public string K { get; set; } = "";
    public string V { get; set; } = "";
}

public class SpeedTestNode
{
    public string Name { get; set; } = "";
    public string BaseName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Server { get; set; } = "";
    public string Port { get; set; } = "";
    public string? Sni { get; set; }
    public bool Tls { get; set; }
    public bool Reality { get; set; }
    public bool Udp { get; set; }
    public string Network { get; set; } = "";
    /// <summary>KB/s；0 = 未测速或未通过。</summary>
    public int Speed { get; set; }
    public string? Country { get; set; }
    public string? Ip { get; set; }
    public string? IpRisk { get; set; }
    public List<MediaTag> Media { get; set; } = new();
    public string? SubTag { get; set; }
    public List<NodeDetail> Details { get; set; } = new();

    // ── ProxyNodeHub 自己补全的 IP 详细信息（内核不填）──
    public string? IpInfoCity { get; set; }
    public string? IpInfoRegion { get; set; }
    public string? IpInfoOrg { get; set; }
    public string? IpInfoAsn { get; set; }
    /// <summary>经纬度，格式 "lat,lng"，来自 ipinfo.io。用于在世界地图上标注城市气泡。</summary>
    public string? IpInfoLoc { get; set; }

    public bool Passed => Speed > 0;

    public string TlsUdpLabel =>
        (Tls ? "tls" : Network is "ws" or "h2" or "grpc" ? Network : "tcp")
        + (Udp ? "+udp" : "");

    /// <summary>
    /// 启发式判断 IP 是否为家宽（住宅 IP）。
    ///
    /// ipinfo.io 免费版不返回 usage 类型字段，只能按 ISP 组织名关键词猜：
    /// 先排除机房关键词（amazon/google/cloudflare/ovh/hetzner/hosting/cloud/…），
    /// 再匹配家宽关键词（comcast/verizon/telekom/vodafone/china telecom/…）。
    /// 两者都不命中的视为「未知」——返回 false，宁可漏判不错判。
    /// </summary>
    public bool IsResidential
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IpInfoOrg)) return false;
            var org = IpInfoOrg!.ToLowerInvariant();

            var dc = new[] { "amazon", "google", "microsoft", "cloudflare",
                "digitalocean", "vultr", "linode", "ovh", "hetzner",
                "data center", "hosting", "cloud", "server",
                "data communication", "akamai", "fastly", "alibaba",
                "tencent", "huawei", "oracle", "ibm", " Choopa " };
            if (dc.Any(k => org.Contains(k))) return false;

            var res = new[] { "comcast", "verizon", "at&t", "charter", "cox",
                "spectrum", "centurylink", "telekom", "orange", "vodafone",
                "telefonica", "bt ", "sky ", "rogers", "bell ", "telstra",
                "china telecom", "china unicom", "china mobile",
                "kddi", "ntt", "jcom", "sk broadband", " virgin ",
                "liberty global", "tele2", "telia", "mtn", "etisalat",
                "propelling", "hughes", "viasat", "starlink", "spacex" };
            return res.Any(k => org.Contains(k));
        }
    }

    /// <summary>
    /// 统一的展示用节点名：截断内核拼接的 | 后缀 + 插入 ipinfo.io 查到的城市。
    ///
    /// 内核 rename-node 只拼国家码（🇺🇸US_1），不拼城市。城市来自 ipinfo.io
    /// 的 IpInfoCity 字段，插在国家码与序号之间：🇺🇸US_1 → 🇺🇸US-Kansas City_1。
    /// Name/BaseName 保持内核原值不动，DisplayName 只读不写。
    /// </summary>
    public string DisplayName
    {
        get
        {
            var raw = !string.IsNullOrWhiteSpace(Name) ? Name
                    : !string.IsNullOrWhiteSpace(BaseName) ? BaseName
                    : Server;
            var pipe = raw.IndexOf('|');
            var core = pipe > 0 ? raw[..pipe] : raw;
            if (string.IsNullOrWhiteSpace(IpInfoCity)) return core;
            var us = core.IndexOf('_');
            return us <= 0 ? core : core[..us] + "-" + IpInfoCity + core[us..];
        }
    }
}

public class SpeedTestSnapshot
{
    public DateTime CheckedAt { get; set; }
    public bool SpeedTest { get; set; }
    public bool MediaCheck { get; set; }
    public List<SpeedTestNode> Nodes { get; set; } = new();
}

// ══════════════════ 微型 HTTP 服务（历史订阅注入用） ══════════════════

/// <summary>
/// 裸 TcpListener 上的单文件 HTTP 服务。
///
/// 不用 HttpListener：它需要对 URL 前缀的 ACL 保留，非管理员用户会直接抛
/// "拒绝访问"。这里只服务 127.0.0.1，一个路径，内容固定，所以手写足够。
/// </summary>
internal sealed class MiniHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _path;
    private byte[] _body = Array.Empty<byte>();
    private bool _running;

    public MiniHttpServer(int port, string path)
    {
        _path = path;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void SetBody(string text)
    {
        _body = Encoding.UTF8.GetBytes(text);
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (_running && !_cts.IsCancellationRequested)
        {
            try
            {
                // 注意：不能写 using。client 的归属权交给 Handle，
                // 否则本轮迭代一结束就把它释放，Handle 拿到的是死连接。
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Handle(client), _cts.Token);
            }
            catch { return; }
        }
    }

    private async Task Handle(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                await using var ns = client.GetStream();
                var buf = new byte[4096];
                int n;
                try { n = await ns.ReadAsync(buf, 0, buf.Length, _cts.Token); }
                catch { return; }
                if (n <= 0) return;

                var request = Encoding.ASCII.GetString(buf, 0, n);
                var line = request.Split('\n')[0];
                var want = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                var body = want.StartsWith(_path, StringComparison.OrdinalIgnoreCase) ? _body : Array.Empty<byte>();

                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/yaml; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n\r\n");
                await ns.WriteAsync(head, 0, head.Length);
                if (body.Length > 0) await ns.WriteAsync(body, 0, body.Length);
                await ns.FlushAsync();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _running = false;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

// ══════════════════ 运行器 ══════════════════

/// <summary>
/// subs-check 内核的宿主：进程生命周期 + HTTP 驱动 + 历史节点注入。
///
/// 设计要点：
///   · listen-port 在启动前定好写进 session.yaml（内核该参数"更新需重启"）
///   · 历史订阅经 127.0.0.1 上的微型服务注入 sub-urls，不依赖内核任何内部行为
///   · 结果一律走 /api/* 读取，不猜内核的静态文件路径映射
/// </summary>
public sealed class SpeedTestRunner : IDisposable
{
    private Process? _proc;
    private MiniHttpServer? _historyServer;
    private readonly StringBuilder _logTail = new();
    private readonly object _logGate = new();

    public bool IsRunning => _proc is { HasExited: false };
    public int Port { get; internal set; }
    public int HistoryPort => _historyServer?.Port ?? 0;
    public string ApiKey { get; internal set; } = "";

    public event Action<string>? OnLog;

    // ══════════════════ 会话生成 ══════════════════

    /// <summary>
    /// 生成 session.yaml = 用户的 config.yaml 原样 + 必须托管的 4 个键。
    /// 其余键完全继承，包括我们不知道的内核新键。
    ///
    /// nodeUris 非空时，只测这些节点：把它们作为本地订阅注入，
    /// 而不是把上游订阅交给内核 —— 后者会让内核测到全部节点，
    /// 几千个节点每个下载 20MB，耗时不可接受。
    /// </summary>
    public string BuildSessionYaml(List<string> subUrls, bool withHistory, int keepRounds,
        List<string>? nodeUris = null)
    {
        var yaml = SpeedTestConfig.ReadConfig();
        if (string.IsNullOrWhiteSpace(yaml)) yaml = "concurrent: 20\n";

        // 设置页暴露的快捷参数注入。未设过的键不动 config.yaml 原值。
        yaml = SpeedTestConfig.ApplySettingsOverrides(yaml, AppSettings.Load());

        // listen-port 必须绑回环，不能用内核默认的 0.0.0.0——那会把带节点
        // 凭据的订阅暴露到局域网。
        yaml = SpeedTestConfig.SetScalar(yaml, SpeedTestConfig.KeyListenPort, $"127.0.0.1:{Port}");
        yaml = SpeedTestConfig.SetScalar(yaml, SpeedTestConfig.KeyApiKey, ApiKey);
        yaml = SpeedTestConfig.SetScalar(yaml, SpeedTestConfig.KeyEnableWebUi, true);

        // mihomo-overwrite-url 内置指向 http://127.0.0.1:8199/...，那个端口是
        // 内核默认的 listen-port。我们改用随机端口后必须同步这个键，否则
        // sub-store 会去连 8199 拿规则文件而 ECONNREFUSED，最终表现为
        // 「获取 mihomo.yaml 失败, 状态码: 500」。
        yaml = SpeedTestConfig.RetargetLocalOverwriteUrl(yaml, Port);

        var urls = new List<string>();

        if (nodeUris is { Count: > 0 } && _historyServer != null)
        {
            // 指定节点模式：注入节点 URI 列表。此时不并入历史 ——
            // 用户明确选了要测什么，混入历史快照里的节点会超出他的选择。
            // （且历史快照是 Clash YAML，与 URI 列表格式不同，不能简单拼接。）
            _historyServer.SetBody(string.Join("\n", nodeUris));
            urls.Add($"http://127.0.0.1:{_historyServer.Port}/history.yaml");
        }
        else
        {
            urls.AddRange(subUrls);
            if (withHistory)
            {
                var history = BuildHistoryMerged(keepRounds);
                if (!string.IsNullOrWhiteSpace(history) && _historyServer != null)
                {
                    _historyServer.SetBody(history);
                    urls.Add($"http://127.0.0.1:{_historyServer.Port}/history.yaml");
                }
            }
        }

        yaml = SpeedTestConfig.SetStringList(yaml, SpeedTestConfig.KeySubUrls, urls);
        return yaml;
    }

    public void WriteSessionYaml(string yaml)
    {
        SpeedTestConfig.EnsureDirs();
        File.WriteAllText(SpeedTestConfig.SessionPath, yaml);
    }

    // ══════════════════ 启动 / 停止 ══════════════════

    /// <summary>
    /// 准备一次会话：分配端口、拉起历史服务、生成并写入 session.yaml。
    ///
    /// 必须在内核进程启动之前完成 —— 内核的 listen-port「更新需重启」，
    /// 且 -f 指定的是启动那一刻读取的文件。先启动后写配置会导致
    /// 内核监听旧端口而探测打新端口，直接超时。
    ///
    /// nodeUris 非空时只测这些节点（见 BuildSessionYaml）。
    /// </summary>
    public async Task PrepareAsync(List<string> subUrls, bool withHistory, int keepRounds,
        List<string>? nodeUris = null)
    {
        AllocateOnly();
        var yaml = BuildSessionYaml(subUrls, withHistory, keepRounds, nodeUris);
        WriteSessionYaml(yaml);
        await Task.CompletedTask;
    }

    /// <summary>只分配端口与历史服务，不启动内核进程。</summary>
    internal void AllocateOnly()
    {
        if (_historyServer != null) return;
        SpeedTestConfig.EnsureDirs();
        Port = FreePort();
        ApiKey = Guid.NewGuid().ToString("n")[..24];
        _historyServer = new MiniHttpServer(FreePort(), "/history.yaml");
        _historyServer.Start();
    }

    /// <summary>启动内核进程并等待 API 就绪。</summary>
    public async Task StartAsync(Action<string> log, CancellationToken ct = default)
    {
        if (IsRunning) return;

        SpeedTestConfig.EnsureDirs();

        // 端口与 session.yaml 由 PrepareAsync 预先准备好。
        // listen-port「更新需重启」，所以必须启动前定好，这里不能再改。
        AllocateOnly();

        var psi = new ProcessStartInfo
        {
            FileName = SpeedTestConfig.KernelPath,
            Arguments = $"-f \"{SpeedTestConfig.SessionPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = SpeedTestConfig.RootDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) EmitLog(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) EmitLog(e.Data); };
        _proc.Exited += (_, _) => EmitLog("[ProxyNodeHub] 内核进程已退出");

        if (!_proc.Start())
            throw new InvalidOperationException("内核进程启动失败");

        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        EmitLog($"[ProxyNodeHub] 内核已启动 · 127.0.0.1:{Port} · 配置 session.yaml");

        // 等 /api/status 就绪，确认端口真的起来了
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_proc.HasExited)
                throw new InvalidOperationException(
                    $"内核启动后立即退出（退出码 {_proc.ExitCode}）。请查看实时日志，常见原因是 config.yaml 有语法错误。");
            var st = await GetStatusAsync(ct);
            if (st != null) { EmitLog("[ProxyNodeHub] 内核 API 就绪"); return; }
            await Task.Delay(400, ct);
        }
        throw new TimeoutException("内核 API 在 25 秒内未就绪，请检查 config.yaml 的 listen-port 是否被占用。");
    }

    public async Task StopAsync()
    {
        var p = _proc;
        _proc = null;
        if (p == null) { _historyServer?.Dispose(); _historyServer = null; return; }

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                using var halt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await p.WaitForExitAsync(halt.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            p.Dispose();
            _historyServer?.Dispose();
            _historyServer = null;
            EmitLog("[ProxyNodeHub] 内核已停止");
        }
    }

    // ══════════════════ HTTP 客户端 ══════════════════

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        BaseAddress = null
    };

    private string Base => $"http://127.0.0.1:{Port}";

    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, Base + path);
        if (!string.IsNullOrEmpty(ApiKey)) r.Headers.Add("X-API-Key", ApiKey);
        return r;
    }

    public async Task<bool> TriggerAsync(CancellationToken ct = default)
    {
        using var resp = await Http.SendAsync(Req(HttpMethod.Post, "/api/trigger-check"), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<SpeedTestStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.SendAsync(Req(HttpMethod.Get, "/api/status"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var st = new SpeedTestStatus
            {
                Checking = r.TryGetProperty("checking", out var c) && c.GetBoolean(),
                ProxyCount = GetInt(r, "proxyCount"),
                Available = GetInt(r, "available"),
                Progress = GetInt(r, "progress"),
                Phase = GetInt(r, "phase")
            };
            if (r.TryGetProperty("pipeline", out var pl) && pl.ValueKind == JsonValueKind.Object)
            {
                st.Pipeline = new SpeedTestPipeline
                {
                    Total = GetInt(pl, "total"),
                    AliveDone = GetInt(pl, "aliveDone"),
                    AlivePass = GetInt(pl, "alivePass"),
                    MediaDone = GetInt(pl, "mediaDone"),
                    FilterPass = GetInt(pl, "filterPass"),
                    SpeedDone = GetInt(pl, "speedDone"),
                    SpeedPass = GetInt(pl, "speedPass")
                };
            }
            return st;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<SpeedTestSnapshot?> GetResultsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.SendAsync(Req(HttpMethod.Get, "/api/results"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var snap = JsonSerializer.Deserialize(json, AppJsonContext.Default.SpeedTestSnapshot);
            if (snap != null) snap.Nodes ??= new List<SpeedTestNode>();
            return snap;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>测速通过的节点导出为 Clash YAML 订阅（只含通过的）。</summary>
    public async Task<string?> GetPassedSubscriptionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"{Base}/sub/all.yaml", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    // ══════════════════ 日志 ══════════════════

    private void EmitLog(string line)
    {
        lock (_logGate)
        {
            _logTail.Append(line).Append('\n');
            SubSourceHelper.TrimLogTail(_logTail);
        }
        try { OnLog?.Invoke(line); } catch { }
    }

    public string LogTail
    {
        get { lock (_logGate) return _logTail.ToString(); }
    }

    // ══════════════════ 历史快照 ══════════════════

    /// <summary>把本轮结果另存为一份快照，超出保留回数的删最旧。</summary>
    public void SaveRound(SpeedTestSnapshot snap)
    {
        try
        {
            Directory.CreateDirectory(SpeedTestConfig.HistoryDir);
            // 文件名必须带毫秒：两轮测速间隔可能不足一秒，只到秒会互相覆盖。
            // 再加一个短随机后缀，彻底消除同毫秒碰撞的可能。
            var name = $"round-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.yaml";
            var path = Path.Combine(SpeedTestConfig.HistoryDir, name);
            File.WriteAllText(path, ToClashYaml(snap));
            PruneSnapshots();
        }
        catch { }
    }

    public List<(string file, int nodes, DateTime when)> ListSnapshots()
    {
        var list = new List<(string, int, DateTime)>();
        try
        {
            if (!Directory.Exists(SpeedTestConfig.HistoryDir)) return list;
            foreach (var f in Directory.EnumerateFiles(SpeedTestConfig.HistoryDir, "round-*.yaml"))
            {
                var when = File.GetCreationTime(f);
                var n = NodeParser.CountNodes(File.ReadAllText(f));
                list.Add((Path.GetFileName(f), n, when));
            }
        }
        catch { }
        // 文件名内嵌 yyyyMMdd-HHmmssfff，字典序即时间序；
        // 不用 File.GetCreationTime —— 精度与稳定性都不够。
        return list.OrderByDescending(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    public void PruneSnapshots(int keepRounds = 10)
    {
        try
        {
            if (!Directory.Exists(SpeedTestConfig.HistoryDir)) return;
            var files = Directory.EnumerateFiles(SpeedTestConfig.HistoryDir, "round-*.yaml")
                .OrderByDescending(f => f, StringComparer.Ordinal).Skip(keepRounds).ToList();
            foreach (var f in files) File.Delete(f);
        }
        catch { }
    }

    /// <summary>最近 N 回快照合并去重，返回 Clash YAML 供注入。</summary>
    public string BuildHistoryMerged(int keepRounds)
    {
        try
        {
            var files = Directory.EnumerateFiles(SpeedTestConfig.HistoryDir, "round-*.yaml")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Take(Math.Max(0, keepRounds))
                .ToList();
            if (files.Count == 0) return "";

            // 快照里是 Clash YAML，直接取节点条目重组比解析 NodeParser 的 URI 列表更稳
            var merged = new StringBuilder();
            merged.Append("proxies:\n");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                foreach (var line in File.ReadAllLines(f))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("- {name:") && !t.StartsWith("- name:")) continue;
                    var key = DedupKeyFromYamlLine(t);
                    if (key == null || !seen.Add(key)) continue;
                    merged.Append(t).Append('\n');
                }
            }
            return merged.Length <= "proxies:\n".Length ? "" : merged.ToString();
        }
        catch { return ""; }
    }

    private static string? DedupKeyFromYamlLine(string line)
    {
        // 从 Clash 节点行里抠 server:port，与 NodeParser 的去重键口径一致
        var s = Extract(line, "server:");
        var p = Extract(line, "port:");
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(p)) return null;
        return $"{s}:{p}";
    }

    private static string? Extract(string line, string key)
    {
        var i = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += key.Length;
        int end = i;
        bool quoted = i < line.Length && (line[i] == '"' || line[i] == '\'');
        if (quoted) { i++; end = i; while (end < line.Length && line[end] != line[i - 1]) end++; }
        else while (end < line.Length && line[end] != ',' && line[end] != '}') end++;
        var v = line[i..end].Trim();
        return v.Length == 0 ? null : v;
    }

    /// <summary>快照内容（本轮通过节点）序列化为 Clash proxies 列表。</summary>
    public static string ToClashYaml(SpeedTestSnapshot snap)
    {
        // 节点名与 SNI 可能含 " 或 \，直接嵌进双引号会生成损坏的 YAML，
        // 而这个文件又要被 BuildHistoryMerged 按行解析回来，必须转义。
        static string YQ(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var sb = new StringBuilder();
        sb.Append("proxies:\n");
        foreach (var n in snap.Nodes.Where(n => n.Passed))
        {
            // 用 DisplayName：截断 | 后缀 + 带城市，Name/BaseName 保持原值不动
            sb.Append($"  - {{name: \"{YQ(n.DisplayName)}\", type: {n.Type}, server: {n.Server}, port: {n.Port}");
            if (n.Tls) sb.Append(", tls: true");
            if (n.Udp) sb.Append(", udp: true");
            if (!string.IsNullOrEmpty(n.Network)) sb.Append($", network: {n.Network}");
            if (!string.IsNullOrEmpty(n.Sni)) sb.Append($", servername: {YQ(n.Sni)}");
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    // ══════════════════ 工具 ══════════════════

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _ = StopAsync();
        _proc?.Dispose();
    }
}

