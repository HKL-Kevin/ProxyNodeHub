using System;
using System.Collections.Generic;
using System.Linq;

namespace ProxyNodeHub;

/// <summary>
/// 测速结果按来源仓库分组，写回可用分。
///
/// 收口到 Core：桌面版与 Web 端都要做这件事，且分组口径（subTag 优先、
/// 节点名方括号兜底）必须一致，否则两边看到的「可用分」会对不上。
/// </summary>
public static class SpeedResultAggregator
{
    /// <summary>
    /// 把本轮节点按来源仓库分组。来源来自订阅链接末尾的 #备注 ——
    /// 内核会把它写进节点命名与 subTag 字段。手动粘贴的订阅没有备注，
    /// 因此不计入任何仓库。
    /// </summary>
    public static List<SpeedTestStore.RepoRound> GroupByRepo(IEnumerable<SpeedTestNode> nodes)
    {
        var groups = new Dictionary<string, List<SpeedTestNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in nodes)
        {
            var repo = RepoFromTag(n);
            if (string.IsNullOrEmpty(repo)) continue;
            if (!groups.TryGetValue(repo, out var list))
            {
                list = new List<SpeedTestNode>();
                groups[repo] = list;
            }
            list.Add(n);
        }

        var result = new List<SpeedTestStore.RepoRound>();
        foreach (var (repo, group) in groups)
        {
            // 存活 = 有 IP 或测速结果；内核的 Speed>0 已隐含存活
            var alive = group.Count(n => n.Passed || !string.IsNullOrEmpty(n.Ip));
            var speeds = group.Where(n => n.Speed > 0).Select(n => n.Speed).ToList();
            result.Add(new SpeedTestStore.RepoRound
            {
                FullName = repo,
                Total = group.Count,
                Alive = Math.Min(alive, group.Count),
                Pass = speeds.Count,
                AvgKbps = speeds.Count > 0 ? (int)speeds.Average() : 0,
                BestKbps = speeds.Count > 0 ? speeds.Max() : 0,
            });
        }
        return result;
    }

    /// <summary>从 subTag / 节点名中还原仓库名。</summary>
    public static string? RepoFromTag(SpeedTestNode n)
    {
        // 优先 subTag；它正是我们在 URL 末尾加的 #备注
        if (!string.IsNullOrEmpty(n.SubTag)) return NormalizeRepo(n.SubTag);

        // 兜底：内核把备注追加到节点命名结尾，形如 "HK 01 [owner_repo]"
        if (string.IsNullOrEmpty(n.Name)) return null;
        var i = n.Name.LastIndexOf('[');
        var j = n.Name.LastIndexOf(']');
        if (i >= 0 && j > i)
        {
            var inner = n.Name[(i + 1)..j];
            // 只接受像 owner/repo 的形状，避免把地区标签误当仓库
            if (inner.Count(c => c == '/') == 1) return NormalizeRepo(inner);
        }
        return null;
    }

    private static string NormalizeRepo(string tag)
    {
        var t = tag.Trim();
        // 我们加备注时把非安全字符换成了 _，这里的 / 保留
        return t.Length > 0 ? t : "";
    }

    /// <summary>平台名 → 两字缩写（测速结果表用）。</summary>
    public static string ShortPlatform(string p) => p.ToLowerInvariant() switch
    {
        "netflix" => "NF", "disney" => "D+", "openai" => "AI", "gemini" => "GP",
        "claude" => "CL", "spotify" => "SP", "youtube" => "YT", "tiktok" => "TT",
        "iprisk" => "RISK", _ => p.Length > 3 ? p[..3].ToUpperInvariant() : p.ToUpperInvariant()
    };

    /// <summary>
    /// 一轮测速结束后的完整收尾：分组 → 写回仓库统计。
    /// 返回受影响的仓库数。
    /// </summary>
    public static int ApplyRound(SpeedTestSnapshot snap)
        => SpeedTestStore.ApplyRounds(GroupByRepo(snap.Nodes));
}
