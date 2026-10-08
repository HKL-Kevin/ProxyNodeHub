using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ProxyNodeHub;

/// <summary>
/// 桌面版与容器版共用的显示/转换小工具。
///
/// 放在 Core 而非 UI 层：两边都要跑搜索与测速，都依赖这些转换，
/// 各写一份必然漂移（项目早期 StyleTab / OpenDir / ToTaggedUrls
/// 就吃过这个亏）。
/// </summary>
public static class SubSourceHelper
{
    /// <summary>订阅来源 → 带备注的 URL。备注让内核把来源写进节点名与 subTag，
    /// 测速结果才能映射回仓库。备注只保留安全字符，避免 URL 片段解析出问题。</summary>
    public static List<string> ToTaggedUrls(IEnumerable<SubSource> sources)
        => sources.Select(s =>
        {
            if (string.IsNullOrEmpty(s.Repo)) return s.Url;
            var tag = new string(s.Repo.Select(c =>
                char.IsLetterOrDigit(c) || c is '/' or '-' or '_' or '.' ? c : '_').ToArray());
            return $"{s.Url}#{tag}";
        }).ToList();

    /// <summary>日志缓冲超过上限时裁掉最旧的一段，防止内存无限增长。</summary>
    public static void TrimLogTail(StringBuilder sb)
    {
        if (sb.Length > 60000) sb.Remove(0, 20000);
    }
}
