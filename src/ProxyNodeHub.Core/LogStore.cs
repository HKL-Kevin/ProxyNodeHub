using System;
using System.IO;

namespace ProxyNodeHub;

/// <summary>
/// 日志持久化存储
/// </summary>
public static class LogStore
{
    private static readonly string LogPath = Path.Combine(
        AppEnvironment.UserDataDir, "log.txt");

    /// <summary>日志保存天数（超过则自动清理）</summary>
    public static int SaveDays { get; set; } = 1;

    /// <summary>加载日志（自动清理过期日志）</summary>
    public static string Load()
    {
        try
        {
            if (!File.Exists(LogPath)) return "";

            // 自动清理过期日志
            var lastWrite = File.GetLastWriteTime(LogPath);
            if ((DateTime.Now - lastWrite).TotalDays > SaveDays)
            {
                File.Delete(LogPath);
                return "";
            }

            return File.ReadAllText(LogPath);
        }
        catch { return ""; }
    }

    /// <summary>追加日志到文件</summary>
    public static void Append(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, message);
        }
        catch { }
    }

    /// <summary>清空日志</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(LogPath)) File.Delete(LogPath);
        }
        catch { }
    }
}
