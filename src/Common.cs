using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ProxyNodeHub;

/// <summary>
/// 跨文件的共享小工具。之前 StyleTab / OpenDir / ToTaggedUrls 各复制了
/// 一份，改一处另一处就漂移。收口到这里，全项目统一调用。
/// </summary>
public static class Common
{
    /// <summary>页签按钮的选中/未选中样式。</summary>
    public static void StyleTab(ModernButton btn, bool selected)
    {
        btn.Ghost = !selected;
        if (selected)
        {
            btn.BaseColor = Theme.Stamp;
            btn.ForeColor = Theme.Paper;
        }
        btn.Invalidate();
    }

    /// <summary>在资源管理器里打开目录；path 是文件时打开其所在目录。</summary>
    public static void OpenDir(string path)
    {
        try
        {
            var dir = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>订阅来源 → 带备注的 URL。实现已下沉到 Core，两边共用一份。</summary>
    public static List<string> ToTaggedUrls(IEnumerable<SubSource> sources)
        => SubSourceHelper.ToTaggedUrls(sources);

    /// <summary>日志缓冲超过上限时裁掉最旧的一段，防止内存无限增长。</summary>
    public static void TrimLogTail(StringBuilder sb)
        => SubSourceHelper.TrimLogTail(sb);
}
