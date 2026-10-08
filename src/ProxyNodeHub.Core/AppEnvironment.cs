using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ProxyNodeHub;

/// <summary>
/// 跨平台运行环境抽象。原先各文件直接调 Application.UserAppDataPath /
/// Environment.SpecialFolder.ApplicationData / Application.ExecutablePath，
/// 这些 API 只存在于 Windows 桌面（WinForms），Linux 容器里根本没有。
///
/// 收口到这一处后：
///   · 桌面版（Windows）行为与改造前完全一致
///   · 容器版（Linux）走 XDG 目录规范，可用环境变量覆盖
///
/// 环境变量优先级最高，方便 Docker 里挂载卷：
///   PROXYNODEHUB_DATA_DIR  用户数据目录（默认 ~/.local/share/ProxyNodeHub）
///   PROXYNODEHUB_CONFIG_DIR 配置目录（默认与数据目录相同）
/// </summary>
public static class AppEnvironment
{
    /// <summary>是否运行在 Windows 桌面（有 WinForms 运行时）。</summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsWindowsDesktop
    {
        get
        {
            // 只有引用了 WinForms 的宿主（桌面版）才为 true；
            // CLI / 容器版没引用，走下面的跨平台分支。
            try { return Type.GetType("System.Windows.Forms.Application, System.Windows.Forms") != null && IsWindows; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 用户数据目录：缓存、特征库、搜索历史、测速结果都放这。
    /// </summary>
    public static string UserDataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PROXYNODEHUB_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            if (IsWindowsDesktop)
            {
                // 桌面版沿用 Application.UserAppDataPath（%AppData%\ProxyNodeHub\0.0.1）
                try { return GetWinFormsUserAppDataPath(); } catch { }
            }

            if (IsWindows)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "ProxyNodeHub");
            }

            // Linux / macOS：XDG 规范
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var baseDir = !string.IsNullOrWhiteSpace(xdg)
                ? xdg.Trim()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(baseDir, "ProxyNodeHub");
        }
    }

    /// <summary>配置文件目录（settings.json 等）。</summary>
    public static string ConfigDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PROXYNODEHUB_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            if (IsWindowsDesktop)
            {
                // 桌面版：配置跟 exe 走（便携），与改造前一致
                try
                {
                    var dir = Path.GetDirectoryName(GetWinFormsExecutablePath());
                    return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
                }
                catch { return AppContext.BaseDirectory; }
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = !string.IsNullOrWhiteSpace(xdg)
                ? xdg.Trim()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(baseDir, "ProxyNodeHub");
        }
    }

    /// <summary>内核可执行文件名：Windows 是 subs-check.exe，Linux/macOS 是 subs-check。</summary>
    public static string KernelExecutableName => IsWindows ? "subs-check.exe" : "subs-check";

    /// <summary>subs-check 内核的 release 资产名，按当前平台与架构选择。</summary>
    public static string KernelAssetName
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (IsWindows)
                return arch == Architecture.Arm64
                    ? "subs-check_Windows_aarch64.zip"
                    : "subs-check_Windows_x86_64.zip";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return arch == Architecture.Arm64
                    ? "subs-check_Darwin_aarch64.tar.gz"
                    : "subs-check_Darwin_x86_64.tar.gz";
            return arch == Architecture.Arm64
                ? "subs-check_Linux_aarch64.tar.gz"
                : "subs-check_Linux_x86_64.tar.gz";
        }
    }

    /// <summary>内核压缩包是否为 tar.gz（Linux/macOS）而非 zip（Windows）。</summary>
    public static bool KernelAssetIsTarGz =>
        KernelAssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>确保目录存在，返回该目录。</summary>
    public static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    // 反射调用 WinForms API，避免 Core 库直接引用 System.Windows.Forms
    private static string GetWinFormsUserAppDataPath()
    {
        var t = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms")!;
        return (string)t.GetProperty("UserAppDataPath")!.GetValue(null)!;
    }

    private static string GetWinFormsExecutablePath()
    {
        var t = Type.GetType("System.Windows.Forms.Application, System.Windows.Forms")!;
        return (string)t.GetProperty("ExecutablePath")!.GetValue(null)!;
    }
}
