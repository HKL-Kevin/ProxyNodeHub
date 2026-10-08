using System;
using System.IO;

namespace ProxyNodeHub;

/// <summary>
/// 原子写文件：先写 .tmp，再 File.Replace 或 Move 落盘。
///
/// 直接 File.WriteAllText 覆盖的风险是：写一半崩溃，原文件损坏且无备份。
/// 设置、收藏、缓存这类用户数据一旦损坏就不可恢复，必须原子写。
/// 失败时清理 .tmp 后重新抛出，由调用方决定静默还是提示。
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(tmp, content);

            // File.Replace 要求目标已存在（首次写会抛 FileNotFoundException），
            // 所以显式分两支；第三个参数是上一版的备份。
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }
        catch
        {
            // 失败时把 .tmp 清掉，否则下次写会被残留污染
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
