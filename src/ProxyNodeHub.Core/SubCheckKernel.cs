using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

/// <summary>
/// subs-check 内核分发与版本管理。
///
/// 内核是 GPL-3.0 的 Go 二进制（Windows x64 约 55MB），不在本仓库内，
/// 首次随安装包附带，之后由用户确认后增量更新。
/// </summary>
public static class SubCheckKernel
{
    public const string Owner = "beck-8";
    public const string Repo = "subs-check";
    public const string SourceUrl = "https://github.com/beck-8/subs-check";

    /// <summary>当前平台的 release 资产名（Windows zip / Linux·macOS tar.gz）。</summary>
    public static string AssetName => AppEnvironment.KernelAssetName;

    /// <summary>内核可执行文件名（Windows 带 .exe，类 Unix 不带）。</summary>
    public static string ExecutableName => AppEnvironment.KernelExecutableName;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public class ReleaseInfo
    {
        public string Version = "";
        public string DownloadUrl = "";
        public string ChecksumsUrl = "";
        public long SizeBytes;
        public DateTime PublishedAt;
    }

    // ══════════════════ 本地版本 ══════════════════

    /// <summary>本地内核版本号；未安装返回 null。</summary>
    public static string? LocalVersion()
    {
        if (!File.Exists(SpeedTestConfig.KernelPath)) return null;
        return LocalVersionCache;
    }

    // 版本号无法从 PE 文件可靠读取，启动时探测一次后缓存到文件
    private static string? _probed;
    public static string? LocalVersionCache
    {
        get
        {
            if (_probed != null) return _probed;
            var p = Path.Combine(SpeedTestConfig.RootDir, "kernel.version");
            try { if (File.Exists(p)) _probed = File.ReadAllText(p).Trim(); } catch { }
            return string.IsNullOrEmpty(_probed) ? null : _probed;
        }
        set
        {
            _probed = value;
            try
            {
                SpeedTestConfig.EnsureDirs();
                var p = Path.Combine(SpeedTestConfig.RootDir, "kernel.version");
                if (string.IsNullOrEmpty(value)) { if (File.Exists(p)) File.Delete(p); }
                else File.WriteAllText(p, value);
            }
            catch { }
        }
    }

    // ══════════════════ 远端查询 ══════════════════

    /// <summary>
    /// 查询最新 release。走 github-api 而非 html —— html 的 download 链接带
    /// 重定向，且 GH 的 release 页在部分网络下会被限流。
    /// </summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(
        Func<string, string> proxyPrefix, CancellationToken ct = default)
    {
        var direct = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        // 代理的约定是「前缀 + 完整 URL」（见 ProxyMirror.Prefix 的说明，
        // InstallAsync 也是这么用的）。这里原先写成
        //   proxyPrefix("https://api.github.com") + "repos/..."
        // 拼出来是 ...api.github.comrepos/... ，少一个斜杠，直连不通的机器
        // 走代理这条通道必然失败，只能卡到超时。
        var targets = new[] { direct, proxyPrefix(direct) };

        foreach (var url in targets)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Accept", "application/vnd.github+json");
                req.Headers.Add("User-Agent", "ProxyNodeHub");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                var tag = root.GetProperty("tag_name").GetString() ?? "";

                string? zip = null, sums = null;
                long size = 0;
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name == AssetName) { zip = a.GetProperty("browser_download_url").GetString(); size = a.GetProperty("size").GetInt64(); }
                    else if (name.EndsWith("_checksums.txt")) sums = a.GetProperty("browser_download_url").GetString();
                }
                if (zip == null) continue;

                return new ReleaseInfo
                {
                    Version = tag,
                    DownloadUrl = zip,
                    ChecksumsUrl = sums ?? "",
                    SizeBytes = size,
                    PublishedAt = root.TryGetProperty("published_at", out var pa) &&
                                  DateTime.TryParse(pa.GetString(), out var dt) ? dt : default
                };
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 换下一个通道 */ }
        }
        return null;
    }

    // ══════════════════ 下载与安装 ══════════════════

    public class InstallProgress
    {
        public string Phase = "";      // 下载 / 校验 / 解压
        public double Percent;         // 0-100
        public long BytesDone;
        public long BytesTotal;
    }

    /// <summary>
    /// 下载并安装内核。全程先落临时文件，校验通过后才原子替换，
    /// 任一环节失败都保留旧内核可用。
    ///
    /// 下载走 Range 续传：国内经代理镜像拉 55MB 约需十几分钟，
    /// 断一次就从头再来是不可接受的。镜像不支持 Range 时自动退化为整段下载。
    /// </summary>
    public static async Task<bool> InstallAsync(
        ReleaseInfo rel, Action<InstallProgress> onProgress, CancellationToken ct = default,
        Func<string, string>? proxyPrefix = null)
    {
        SpeedTestConfig.EnsureDirs();
        var tmpZip = SpeedTestConfig.KernelPath + ".download";
        var bakExe = SpeedTestConfig.KernelPath + ".bak";
        var partFile = tmpZip + ".part";
        var newExe = SpeedTestConfig.KernelPath + ".new";

        try
        {
            // ① 下载（支持续传）
            long already = 0;
            try { if (File.Exists(partFile)) already = new FileInfo(partFile).Length; } catch { }

            var channels = new List<string> { rel.DownloadUrl };
            if (proxyPrefix != null)
            {
                var p = proxyPrefix(rel.DownloadUrl);
                if (p != rel.DownloadUrl) channels.Add(p);
            }

            foreach (var url in channels)
            {
                try
                {
                    await DownloadWithResume(url, partFile, already, rel.SizeBytes, onProgress, ct);
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch when (already == 0)
                {
                    // 首个通道失败且无已传字节：换下一个通道重来
                    already = 0;
                    continue;
                }
            }

            File.Move(partFile, tmpZip, true);

            // ② SHA256 校验 —— 这是个会处理节点凭据的代理二进制，校验是硬要求
            if (!string.IsNullOrEmpty(rel.ChecksumsUrl))
            {
                onProgress(new InstallProgress { Phase = "校验", Percent = 50 });
                var expected = await FetchExpectedHashAsync(rel.ChecksumsUrl, ct);
                if (expected != null)
                {
                    await using var fs = File.OpenRead(tmpZip);
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"SHA256 校验失败\n期望 {expected}\n实际 {actual}");
                }
            }
            onProgress(new InstallProgress { Phase = "校验", Percent = 100 });

            // ③ 备份旧版后替换
            onProgress(new InstallProgress { Phase = "解压", Percent = 30 });
            SafeDelete(newExe);   // 清掉上次可能的残留

            if (File.Exists(SpeedTestConfig.KernelPath))
            {
                if (File.Exists(bakExe)) File.Delete(bakExe);
                File.Move(SpeedTestConfig.KernelPath, bakExe);
            }

            await ExtractKernelAsync(tmpZip, newExe, ct);

            // 解压完整成功，原子替换
            File.Move(newExe, SpeedTestConfig.KernelPath, true);
            EnsureExecutable(SpeedTestConfig.KernelPath);

            LocalVersionCache = rel.Version;
            onProgress(new InstallProgress { Phase = "解压", Percent = 100 });
            return true;
        }
        catch (OperationCanceledException)
        {
            RestoreKernelBackup(newExe, bakExe);
            throw;      // 保留 .part 供下次续传
        }
        catch
        {
            RestoreKernelBackup(newExe, bakExe);
            SafeDelete(tmpZip);
            SafeDelete(partFile);
            throw;
        }
    }

    /// <summary>
    /// 安装失败时恢复现场：删掉残缺的 .new，若旧内核被移到了 .bak 就移回来。
    /// 保证任何失败路径下 KernelPath 都不丢 —— 这是硬要求，旧内核一旦被
    /// 残缺文件覆盖就找不回来了。
    /// </summary>
    private static void RestoreKernelBackup(string newExe, string bakExe)
    {
        SafeDelete(newExe);
        if (!File.Exists(SpeedTestConfig.KernelPath) && File.Exists(bakExe))
        {
            try { File.Move(bakExe, SpeedTestConfig.KernelPath); } catch { }
        }
    }

    /// <summary>
    /// 从用户本地 zip 安装。国内拉 55MB 常失败，这是必要的兜底路径。
    /// 不做 SHA256 校验 —— 拿不到该版本的 checksums，改为在 UI 明确告知未校验。
    /// </summary>
    public static async Task<bool> InstallFromLocalAsync(
        string zipPath, ReleaseInfo rel, Action<InstallProgress> onProgress, CancellationToken ct = default)
    {
        SpeedTestConfig.EnsureDirs();
        var tmpZip = SpeedTestConfig.KernelPath + ".download";
        var bakExe = SpeedTestConfig.KernelPath + ".bak";
        var newExe = SpeedTestConfig.KernelPath + ".new";

        try
        {
            onProgress(new InstallProgress { Phase = "解压", Percent = 10 });
            File.Copy(zipPath, tmpZip, true);

            onProgress(new InstallProgress { Phase = "解压", Percent = 40 });
            SafeDelete(newExe);
            if (File.Exists(SpeedTestConfig.KernelPath))
            {
                if (File.Exists(bakExe)) File.Delete(bakExe);
                File.Move(SpeedTestConfig.KernelPath, bakExe);
            }

            using (var zip = ZipFile.OpenRead(tmpZip))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidDataException($"压缩包内未找到 {ExecutableName}");

                await using var src = entry.Open();
                await using var dst = File.Create(newExe);
                await src.CopyToAsync(dst, ct);
            }

            File.Move(newExe, SpeedTestConfig.KernelPath, true);
            EnsureExecutable(SpeedTestConfig.KernelPath);

            LocalVersionCache = rel.Version;
            onProgress(new InstallProgress { Phase = "解压", Percent = 100 });
            return true;
        }
        catch
        {
            RestoreKernelBackup(newExe, bakExe);
            SafeDelete(tmpZip);
            throw;
        }
    }

    /// <summary>带 Range 续传的下载。服务端不支持 Range 时退化为整段重下。</summary>
    private static async Task DownloadWithResume(
        string url, string partFile, long already, long declaredTotal,
        Action<InstallProgress> onProgress, CancellationToken ct)
    {
        // 先探一下是否支持 Range：要第一个字节，看回 200 还是 206
        long startFrom = 0;
        if (already > 0)
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, url);
            probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var pr = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
            if (pr.StatusCode == System.Net.HttpStatusCode.PartialContent)
                startFrom = already;
            else
            {
                SafeDelete(partFile);   // 不支持 Range，已传部分作废
                already = 0;
            }
        }

        onProgress(new InstallProgress
        {
            Phase = "下载", BytesDone = startFrom,
            BytesTotal = declaredTotal > 0 ? declaredTotal : startFrom,
            Percent = declaredTotal > 0 ? startFrom * 100.0 / declaredTotal : 0
        });

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startFrom > 0)
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startFrom, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (startFrom > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // 走到这：探测说支持、实际没回 206 —— 保守起见重下
            startFrom = 0; already = 0;
            SafeDelete(partFile);
        }
        resp.EnsureSuccessStatusCode();

        var total = declaredTotal > 0
            ? declaredTotal
            : (resp.Content.Headers.ContentLength ?? 0) + startFrom;

        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(partFile,
            startFrom > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        var buf = new byte[81920];
        long done = startFrom;
        int n;
        while ((n = await net.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            onProgress(new InstallProgress
            {
                Phase = "下载", BytesDone = done, BytesTotal = total,
                Percent = total > 0 ? done * 100.0 / total : 0
            });
        }
    }

    /// <summary>
    /// 从压缩包中取出内核可执行文件写入 dest。
    /// Windows 资产是 zip，Linux/macOS 资产是 tar.gz —— 两种都要支持，
    /// 否则容器里无法安装或更新内核。
    ///
    /// 格式判断按内容魔数而非文件名：下载落地的临时文件是
    /// "subs-check.download"，没有 .tar.gz 后缀，靠后缀判断会误走 zip 分支。
    /// </summary>
    private static async Task ExtractKernelAsync(string archivePath, string dest, CancellationToken ct)
    {
        if (LooksLikeTarGz(archivePath))
        {
            await ExtractFromTarGzAsync(archivePath, dest, ct);
            return;
        }

        // zip（Windows 资产）
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.Name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            throw new InvalidDataException($"压缩包内未找到 {ExecutableName}");

        // 解压先写临时文件，绝不直接写目标：
        // 中途取消/磁盘满只会留下残缺文件，旧内核不受影响。
        await using var src = entry.Open();
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
    }

    /// <summary>gzip 魔数 (1F 8B) 探测，避免拿错格式硬解。</summary>
    private static bool LooksLikeTarGz(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[2];
            if (fs.Read(b, 0, 2) != 2) return false;
            return b[0] == 0x1F && b[1] == 0x8B;
        }
        catch { return false; }
    }

    private static async Task ExtractFromTarGzAsync(string archivePath, string dest, CancellationToken ct)
    {
        await using var fs = File.OpenRead(archivePath);
        await using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);

        // .NET 8 的 TarReader 足够读这种单文件归档，无需额外依赖
        using var reader = new System.Formats.Tar.TarReader(gz);
        System.Formats.Tar.TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(cancellationToken: ct)) != null)
        {
            if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile
                or System.Formats.Tar.TarEntryType.V7RegularFile)) continue;

            var name = Path.GetFileName(entry.Name);
            if (!name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)) continue;

            await using var dst = File.Create(dest);
            await entry.DataStream!.CopyToAsync(dst, ct);
            return;
        }
        throw new InvalidDataException($"tar.gz 内未找到 {ExecutableName}");
    }

    /// <summary>类 Unix 平台给内核补上可执行权限（zip 解压不会带权限位）。</summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
        }
        catch { }
    }

    private static async Task<string?> FetchExpectedHashAsync(string checksumsUrl, CancellationToken ct)    {
        try
        {
            var txt = await Http.GetStringAsync(checksumsUrl, ct);
            var m = Regex.Match(txt, @"(?im)^([0-9a-f]{64})\s+\*?" + Regex.Escape(AssetName) + @"\s*$");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(txt, @"(?im)^([0-9a-f]{64})\s+" + Regex.Escape(AssetName));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    public static void RemoveBackup()
    {
        try
        {
            var bak = SpeedTestConfig.KernelPath + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
        catch { }
    }

    private static void SafeDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>还原上一次成功安装前的内核。</summary>
    public static bool Rollback()
    {
        var bak = SpeedTestConfig.KernelPath + ".bak";
        try
        {
            if (!File.Exists(bak)) return false;
            if (File.Exists(SpeedTestConfig.KernelPath)) File.Delete(SpeedTestConfig.KernelPath);
            File.Move(bak, SpeedTestConfig.KernelPath);
            return true;
        }
        catch { return false; }
    }

    // ══════════════════ 版本号比对 ══════════════════

    /// <summary>v1.6.5 → (1,6,5)。无法解析返回 null。</summary>
    /// <summary>
    /// 版本号的唯一显示规范：一律 vX.Y.Z。
    ///
    /// 必须收口到这一处：release 的 tag_name 自带 v（"v1.6.5"），
    /// InstallAsync 写进 kernel.version 的就是这个值；展示处若再拼一个 v
    /// 就成了 "vv1.6.5"。内核弹窗与测速页按钮都踩过这个坑。
    /// </summary>
    public static string Display(string? v)
        => string.IsNullOrWhiteSpace(v) ? "未知" : (v.Trim().StartsWith("v") ? v.Trim() : "v" + v.Trim());

    public static int[]? ParseVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var m = Regex.Match(v.Trim(), @"v?(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;
        return new[]
        {
            int.Parse(m.Groups[1].Value),
            m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0
        };
    }

    public static bool IsNewer(string? remote, string? local)
    {
        var r = ParseVersion(remote);
        var l = ParseVersion(local);
        if (r == null) return false;
        if (l == null) return true;
        for (int i = 0; i < 3; i++)
            if (r[i] != l[i]) return r[i] > l[i];
        return false;
    }
}
