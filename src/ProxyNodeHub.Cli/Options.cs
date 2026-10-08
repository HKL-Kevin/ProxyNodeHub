using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProxyNodeHub.Cli;

/// <summary>
/// 极简命令行解析。刻意不引第三方库 —— 容器镜像要尽量小，
/// 且参数面很窄，手写足够且无依赖。
/// </summary>
public sealed class Options
{
    public string Command = "search";
    public List<string> Inputs = new();

    public string? Token;
    public string? Proxy;
    public bool AutoProxy;
    /// <summary>
    /// 订阅转换后端端点（minisubconvert / subconverter）。CLI 的 merge/search
    /// 抓到的订阅若非纯 URI 列表（Clash YAML、sing-box JSON 等），NodeParser
    /// 本地解不了，必须过这个后端归一成 target=uri。
    /// 也支持 PROXYNODEHUB_SUBCONVERTER_URL 环境变量。
    /// </summary>
    public string? SubConverterUrl;
    public int Count = 30;
    public int SinceDays = 7;
    public int Concurrency = 6;
    public int TimeoutMinutes = 60;
    public int HistoryDays = 1;
    public bool SkipSearched;
    public bool NoMerge;
    public bool Stdout;
    public bool Json;
    public bool Verbose;
    public bool ShowHelp;
    public bool ShowVersion;

    /// <summary>Web 服务监听地址。容器里默认 0.0.0.0 才能从宿主访问。</summary>
    public string Bind = "0.0.0.0";
    public int Port = 8080;

    /// <summary>输出目录。容器里默认 /data/output，可用 --output 覆盖。</summary>
    public string OutputDir = DefaultOutputDir();

    /// <summary>
    /// 输出目录默认值。容器里 /data 是可写的挂载卷，优先用它；
    /// 在宿主机（macOS/Windows）上 /data 不存在或只读，回退到数据目录，
    /// 否则本地直接跑 CLI 会在写结果时炸掉。
    /// </summary>
    private static string DefaultOutputDir()
    {
        var env = Environment.GetEnvironmentVariable("PROXYNODEHUB_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var dataDir = Environment.GetEnvironmentVariable("PROXYNODEHUB_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
            return Path.Combine(dataDir.Trim(), "output");

        // 容器约定：/data 由 Dockerfile 创建且可写
        if (Directory.Exists("/data") && IsWritable("/data"))
            return "/data/output";

        return Path.Combine(AppEnvironment.UserDataDir, "output");
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static readonly string[] Commands =
        { "web", "search", "merge", "speedtest", "kernel", "doctor" };

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;

        // 第一个非选项参数若是已知命令，则作为命令
        if (args.Length > 0 && !args[0].StartsWith('-') && Commands.Contains(args[0]))
        {
            o.Command = args[0];
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            var a = args[i];

            string Next(string name)
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{name} 需要一个值");
                return args[++i];
            }

            switch (a)
            {
                case "-h" or "--help": o.ShowHelp = true; break;
                case "-V" or "--version": o.ShowVersion = true; break;
                case "-v" or "--verbose": o.Verbose = true; break;
                case "--stdout": o.Stdout = true; break;
                case "--json": o.Json = true; break;
                case "--no-merge": o.NoMerge = true; break;
                case "--skip-searched": o.SkipSearched = true; break;
                case "--auto-proxy": o.AutoProxy = true; break;

                case "-t" or "--token":
                    o.Token = Next(a);
                    break;
                case "-p" or "--proxy":
                    o.Proxy = Next(a);
                    break;
                case "--sub-converter" or "--subconverter":
                    o.SubConverterUrl = Next(a);
                    break;
                case "-n" or "--count":
                    o.Count = ParseInt(a, Next(a), 1, 500);
                    break;
                case "-d" or "--days":
                    o.SinceDays = ParseInt(a, Next(a), 1, 365);
                    break;
                case "-c" or "--concurrency":
                    o.Concurrency = ParseInt(a, Next(a), 1, 64);
                    break;
                case "--timeout":
                    o.TimeoutMinutes = ParseInt(a, Next(a), 1, 1440);
                    break;
                case "--history-days":
                    o.HistoryDays = ParseInt(a, Next(a), 1, 30);
                    break;
                case "-o" or "--output":
                    o.OutputDir = Next(a);
                    break;
                case "--bind":
                    o.Bind = Next(a);
                    break;
                case "--port":
                    o.Port = ParseInt(a, Next(a), 1, 65535);
                    break;

                default:
                    if (a.StartsWith('-')) throw new ArgumentException($"未知选项: {a}");
                    o.Inputs.Add(a);
                    break;
            }
        }

        // 环境变量兜底（容器里最方便的传参方式）
        o.Token ??= Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        o.SubConverterUrl ??= Environment.GetEnvironmentVariable("PROXYNODEHUB_SUBCONVERTER_URL");

        return o;
    }

    private static int ParseInt(string name, string raw, int min, int max)
    {
        if (!int.TryParse(raw, out var v))
            throw new ArgumentException($"{name} 需要一个整数，得到: {raw}");
        if (v < min || v > max)
            throw new ArgumentException($"{name} 需在 {min}..{max} 之间，得到: {v}");
        return v;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
ProxyNodeHub CLI — GitHub 免费节点仓库搜索、聚合与测速

用法:
  pnodes [命令] [选项] [参数]

命令:
  web                        启动 Web 管理界面（浏览器操作，常驻运行）
  search                     搜索仓库、分析活跃度、探测订阅链接并合并去重 (默认)
  merge <文件|URL…>          只合并去重已有订阅，不做搜索
  speedtest <URL|文件…>      用 subs-check 内核测速（需已安装内核）
  kernel [status|install]    查看或安装测速内核
  doctor                     环境自检（路径、权限、Token）

通用选项:
  -t, --token <TOKEN>        GitHub Token（或设 GITHUB_TOKEN 环境变量）
  -p, --proxy <名称>         加速镜像，如 ghfast.top、gh-proxy.com
      --sub-converter <URL>  订阅转换后端，如 http://host:3001/minisubconvert/sub
                             （或设 PROXYNODEHUB_SUBCONVERTER_URL；非纯 URI 订阅必填）
      --auto-proxy           自动测速并选择最快镜像
  -o, --output <目录>        输出目录（默认 /data/output）
      --stdout               结果同时打印到标准输出
      --json                 额外输出 repos.json
  -v, --verbose              详细日志（含探测与内核输出）
  -h, --help                 显示帮助
  -V, --version              显示版本

search 专用:
  -n, --count <N>            目标仓库数（默认 30）
  -d, --days <N>             只看最近 N 天更新（默认 7）
  -c, --concurrency <N>      分析并发数（默认 6）
      --skip-searched        跳过搜索历史里未过期的仓库
      --history-days <N>     搜索历史保留天数（默认 1）
      --no-merge             只列链接，不合并去重

speedtest 专用:
      --timeout <分钟>       测速超时（默认 60）

web 专用:
      --bind <地址>          监听地址（默认 0.0.0.0，容器内必须如此）
      --port <端口>          监听端口（默认 8080）

示例:
  pnodes web --port 8080
  pnodes search --count 20 --auto-proxy --json
  pnodes search -t ghp_xxx -n 50 -d 14 -v
  pnodes merge /data/output/links.txt https://example.com/sub
  pnodes speedtest https://example.com/sub --timeout 30
  pnodes kernel install
""");
    }
}
