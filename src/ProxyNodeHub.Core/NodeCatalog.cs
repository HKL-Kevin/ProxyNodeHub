using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

/// <summary>
/// 订阅节点清单加载：把订阅内容解析成节点条目，不启动测速内核。
///
/// 为什么单独做这一步：内核的全量测速会对每个节点真实下载 20MB，
/// 几千个节点要跑几小时。用户想先看到「有哪些节点」再决定测哪些 ——
/// 那一步只需要 HTTP 拉取 + 文本解析，秒级完成，用不着内核。
///
/// 解析口径与 NodeParser 一致（vmess/vless/trojan/ss/ssr/hy2/tuic），
/// 这样列表里看到的节点与后续测速的是同一批。
/// </summary>
public static class NodeCatalog
{
    public class NodeEntry
    {
        public string Uri = "";
        /// <summary>节点名（URI 的 #fragment，已解码）。</summary>
        public string Name = "";
        public string Protocol = "";
        public string Server = "";
        public string Port = "";
        /// <summary>来源仓库（订阅链接末尾的 #备注）。</summary>
        public string Repo = "";
        /// <summary>来源订阅名（文件名）。</summary>
        public string SourceName = "";
    }

    /// <summary>
    /// 拉取并解析所有订阅，返回去重后的节点清单。
    ///
    /// subConverterUrl 非空时，每个订阅 URL 会先改写成
    ///   {subConverterUrl}?target=uri&url={原URL 编码}
    /// （地址若已含查询串则用 & 接续）
    /// 让转换后端把 Clash YAML / base64 / sing-box 等格式统一转成 URI 列表。
    /// 否则只拉原始内容，靠 NodeParser.SplitNodeLines 自己解析 ——
    /// 后者不认 Clash YAML 的 proxies: 段，纯 YAML 订阅会得 0 节点。
    ///
    /// subConverterUrl 应填到「完整端点路径」为止，例如
    ///   http://host:3001/minisubconvert/sub
    /// 而不是只填 base，因为不同部署的路径前缀（secret）不同，
    /// 拼接逻辑无法猜出 /sub 在哪。
    /// </summary>
    public static async Task<List<NodeEntry>> LoadAsync(
        GitHubService github, IEnumerable<SubSource> sources,
        Action<int, int>? onProgress = null, Action<string>? log = null,
        CancellationToken ct = default, string? subConverterUrl = null)
    {
        var list = sources.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<NodeEntry>();

        var converter = string.IsNullOrWhiteSpace(subConverterUrl) ? null
            : subConverterUrl!.Trim();

        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = list[i];
            onProgress?.Invoke(i + 1, list.Count);

            // 转换后端模式：在端点后追加 target=uri&url=<原URL 编码>。
            // 端点本身可能已带查询串（如 ?secret=xxx），所以用 & 接续而不是 ?。
            // 不转换模式：直接拉原 URL。
            string fetchUrl;
            if (converter != null)
            {
                var sep = converter.Contains('?') ? "&" : "?";
                fetchUrl = $"{converter}{sep}target=uri&url={Uri.EscapeDataString(src.Url)}";
            }
            else fetchUrl = src.Url;

            string? content;
            try
            {
                content = await github.GetUrlAsync(fetchUrl, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"拉取失败 {src.Url}: {ex.Message}");
                continue;
            }

            if (string.IsNullOrEmpty(content)) continue;

            var lines = NodeParser.SplitNodeLines(content);
            int added = 0;
            foreach (var line in lines)
            {
                // 去重键与 NodeParser.MergeNodes 同口径，保证列表不重复
                var key = NodeParser.GetDedupKey(line) ?? "h:" + line;
                if (!seen.Add(key)) continue;

                nodes.Add(Describe(line, src));
                added++;
            }

            if (added > 0)
                log?.Invoke($"  {src.Repo ?? src.Url} → {added} 节点");
        }

        return nodes;
    }

    /// <summary>从节点 URI 提取协议、服务器、端口、名称。</summary>
    public static NodeEntry Describe(string uri, SubSource src)
    {
        var entry = new NodeEntry
        {
            Uri = uri,
            Repo = src.Repo ?? "",
            SourceName = SourceNameOf(src.Url),
            Protocol = ProtocolOf(uri),
            Name = NameOf(uri),
        };

        var key = NodeParser.GetDedupKey(uri);
        if (!string.IsNullOrEmpty(key))
        {
            var idx = key.LastIndexOf(':');
            if (idx > 0)
            {
                entry.Server = key[..idx];
                entry.Port = key[(idx + 1)..];
            }
            else entry.Server = key;
        }

        return entry;
    }

    private static string ProtocolOf(string uri)
    {
        var i = uri.IndexOf("://", StringComparison.Ordinal);
        return i > 0 ? uri[..i] : "?";
    }

    /// <summary>取 URI 的 #fragment 作为节点名并解码。</summary>
    private static string NameOf(string uri)
    {
        var i = uri.IndexOf('#');
        if (i < 0 || i + 1 >= uri.Length) return "";
        var raw = uri[(i + 1)..];
        try { return Uri.UnescapeDataString(raw); }
        catch { return raw; }
    }

    private static string SourceNameOf(string url)
    {
        try
        {
            var noFrag = url.Split('#')[0];
            var seg = noFrag.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            return seg.Length > 0 ? seg : url;
        }
        catch { return url; }
    }

    /// <summary>把选中节点还原成内核可吃的订阅内容（URI 列表，一行一个）。</summary>
    public static string ToSubscription(IEnumerable<NodeEntry> nodes)
        => string.Join("\n", nodes.Select(n => n.Uri));

    /// <summary>导出为 Clash YAML（仅基本信息，不含测速结果）。</summary>
    public static string ToBasicClashYaml(IEnumerable<NodeEntry> nodes)
    {
        var sb = new StringBuilder("proxies:\n");
        foreach (var n in nodes)
        {
            sb.Append("  - {name: \"").Append(Escape(n.Name.Length > 0 ? n.Name : n.Server))
              .Append("\", type: ").Append(n.Protocol)
              .Append(", server: ").Append(n.Server)
              .Append(", port: ").Append(n.Port).Append("}\n");
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
