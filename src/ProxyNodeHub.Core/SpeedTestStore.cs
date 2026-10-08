using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyNodeHub;

/// <summary>
/// 单个仓库的测速结果统计。
/// </summary>
public class SpeedStat
{
    public string FullName { get; set; } = "";
    public DateTime TestedAt { get; set; }

    /// <summary>本轮该仓库产出的节点总数。</summary>
    public int TotalNodes { get; set; }

    /// <summary>测活通过数。</summary>
    public int AliveNodes { get; set; }

    /// <summary>测速通过数（同时满足存活与速度门槛）。</summary>
    public int PassNodes { get; set; }

    /// <summary>平均速度，KB/s。</summary>
    public int AvgSpeedKbps { get; set; }

    /// <summary>最快节点速度，KB/s。</summary>
    public int BestSpeedKbps { get; set; }

    /// <summary>综合可用分 0–100。</summary>
    public int Score { get; set; }

    /// <summary>连续「零通过」轮数，用于自动降权。</summary>
    public int ZeroRounds { get; set; }

    public bool Demoted { get; set; }

    public double PassRate => TotalNodes > 0 ? (double)PassNodes / TotalNodes : 0;
    public double AliveRate => TotalNodes > 0 ? (double)AliveNodes / TotalNodes : 0;

    public string SpeedText => AvgSpeedKbps <= 0 ? "—" :
        AvgSpeedKbps >= 1024 ? $"{AvgSpeedKbps / 1024.0:F1} MB/s" : $"{AvgSpeedKbps} KB/s";

    public string TestedText =>
        TestedAt == default ? "未测速" :
        (DateTime.Now - TestedAt).TotalHours < 1 ? "刚刚" :
        (DateTime.Now - TestedAt).TotalHours < 24 ? $"{(DateTime.Now - TestedAt).TotalHours:F0} 小时前" :
        $"{(DateTime.Now - TestedAt).TotalDays:F0} 天前";
}

/// <summary>
/// 测速结果的持久化与仓库映射。
///
/// 单独存而不放进 RepoInfo：仓库缓存已经很大，且自动降权需要的连续轮数
/// 不该被用户清缓存重置。
///
/// 节点到仓库的映射依赖内核的订阅备注能力 —— sub-urls 末尾加 #备注，
/// 备注会进节点命名与结果的 subTag 字段。这样不需要自己解析节点内容。
/// </summary>
public static class SpeedTestStore
{
    /// <summary>综合可用分中「通过率」与「速度」的权重。</summary>
    private const double PassWeight = 0.6;
    private const double SpeedWeight = 0.4;

    /// <summary>速度分的归一化上限：4 MB/s 视为满速。</summary>
    private const double SpeedFullScale = 4096.0;

    /// <summary>连续多少轮零通过后降权。</summary>
    public const int DemoteAfterRounds = 3;

    private const string FileName = "speed_stats.json";

    private static string Path_
    {
        get { return System.IO.Path.Combine(AppEnvironment.UserDataDir, FileName); }
    }

    private static Dictionary<string, SpeedStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static IEnumerable<SpeedStat> All => _stats.Values;

    public static SpeedStat? Get(string fullName)
        => _stats.TryGetValue(fullName, out var s) ? s : null;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(Path_)) return;
            var list = JsonSerializer.Deserialize(
                File.ReadAllText(Path_), AppJsonContext.Default.SpeedStatList) ?? new List<SpeedStat>();
            _stats = new Dictionary<string, SpeedStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in list)
                if (!string.IsNullOrEmpty(s.FullName)) _stats[s.FullName] = s;
        }
        catch { _stats = new(StringComparer.OrdinalIgnoreCase); }
    }

    public static void Save()
    {
        try
        {
            AtomicFile.Write(Path_,
                JsonSerializer.Serialize(_stats.Values.ToList(), AppJsonContext.Default.SpeedStatList));
        }
        catch
        {
            // 统计写入失败不中断测速流程（降权统计非关键数据）。
            // .tmp 残留已由 AtomicFile 清理。
        }
    }

    // ══════════════════ 计算 ══════════════════

    /// <summary>综合可用分：60% 通过率 + 40% 速度。</summary>
    public static int ComputeScore(int total, int pass, int avgKbps)
    {
        if (total <= 0) return 0;
        var passRate = Math.Clamp((double)pass / total, 0, 1);
        var speedRate = Math.Clamp(avgKbps / SpeedFullScale, 0, 1);
        return (int)Math.Round(100 * (PassWeight * passRate + SpeedWeight * speedRate));
    }

    // ══════════════════ 一轮结果写回 ══════════════════

    public class RepoRound
    {
        public string FullName = "";
        public int Total, Alive, Pass, AvgKbps, BestKbps;
    }

    /// <summary>
    /// 把一轮测速结果写回各仓库。
    /// 本轮没有出现的仓库不动它的历史分，只让「连续零通过」计数继续累加
    /// —— 一次搜索没收到的仓库不该被误判为失效。
    /// </summary>
    public static int ApplyRounds(IEnumerable<RepoRound> rounds)
    {
        Load();
        int changed = 0;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rounds)
        {
            if (string.IsNullOrEmpty(r.FullName)) continue;
            touched.Add(r.FullName);

            if (!_stats.TryGetValue(r.FullName, out var s))
            {
                s = new SpeedStat { FullName = r.FullName };
                _stats[r.FullName] = s;
            }

            s.TestedAt = DateTime.Now;
            s.TotalNodes = r.Total;
            s.AliveNodes = r.Alive;
            s.PassNodes = r.Pass;
            s.AvgSpeedKbps = r.AvgKbps;
            s.BestSpeedKbps = r.BestKbps;
            s.Score = ComputeScore(r.Total, r.Pass, r.AvgKbps);

            if (r.Pass == 0)
            {
                s.ZeroRounds++;
                if (s.ZeroRounds >= DemoteAfterRounds) s.Demoted = true;
            }
            else
            {
                s.ZeroRounds = 0;
                s.Demoted = false;      // 恢复可用就解除降权
            }
            changed++;
        }

        // 未出现的仓库：不涨分数，但若已降权则保持；未降权的不累加
        // （累加会让「只是这次没搜到」的仓库被误杀）

        Save();
        return changed;
    }

    /// <summary>解除某个仓库的降权（用户手动操作）。</summary>
    public static void Undemote(string fullName)
    {
        Load();
        if (_stats.TryGetValue(fullName, out var s))
        {
            s.ZeroRounds = 0;
            s.Demoted = false;
            Save();
        }
    }

    public static void UndemoteAll()
    {
        Load();
        foreach (var s in _stats.Values) { s.ZeroRounds = 0; s.Demoted = false; }
        Save();
    }

    public static void Clear()
    {
        _stats = new(StringComparer.OrdinalIgnoreCase);
        _loaded = true;
        try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
    }

    // ══════════════════ 排序辅助 ══════════════════

    /// <summary>参与排序的分值：未测速的仓库给 -1，排在已测速之后。</summary>
    public static int SortScore(string fullName)
    {
        var s = Get(fullName);
        if (s == null) return -1;
        if (s.Demoted) return -1;      // 降权等同于未测速，沉底
        return s.Score;
    }

    public static int SortAlive(string fullName)
    {
        var s = Get(fullName);
        if (s == null) return -1;
        if (s.Demoted) return -1;
        return (int)Math.Round(s.AliveRate * 100);
    }

    public static int SortAvgSpeed(string fullName)
    {
        var s = Get(fullName);
        if (s == null || s.Demoted) return -1;
        return s.AvgSpeedKbps;
    }
}
