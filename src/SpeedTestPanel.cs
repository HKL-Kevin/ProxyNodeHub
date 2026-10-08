using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProxyNodeHub;

/// <summary>
/// 节点测速页。
///
/// 自身持有 SpeedTestRunner 与配置读写，不占用 MainForm 的状态。
/// 订阅来源通过 GetSubUrls 委托从主窗体取，避免双向依赖。
/// </summary>
public sealed class SpeedTestPanel : UserControl
{
    private readonly SpeedTestRunner _runner = new();

    internal SpeedTestRunner Runner => _runner;
    private CancellationTokenSource _cts = new();
    private System.Windows.Forms.Timer _pollTimer = null!;
    private bool _polling, _finished;
    private readonly StringBuilder _logTail = new();
    private readonly object _logGate = new();

    // ── 主窗体注入 ──
    /// <summary>当前应测的订阅及其来源仓库。</summary>
    public Func<List<SubSource>>? GetSubSources { get; set; }

    /// <summary>一轮完成后回调（携带更新的仓库数）。</summary>
    public Action<int>? OnRoundApplied { get; set; }

    /// <summary>内核输出的单行日志。颜色已按 WARN/ERROR 分级。</summary>
    public event Action<string, Color>? OnLogLine;

    /// <summary>结果集变化时回调，详情面板据此重绘。</summary>
    public event Action? OnNodesChanged;

    /// <summary>本轮检测到的节点，供详情面板渲染。</summary>
    public List<SpeedTestNode> Nodes => _nodes;

    /// <summary>是否正在运行。</summary>
    public bool Running => _runner.IsRunning;

    // ── 控件 ──
    private ModernButton _btnRun = null!, _btnSource = null!, _btnSettings = null!,
        _btnKernel = null!, _btnWebUi = null!, _btnCopySub = null!;
    private Label _lblState = null!, _lblNums = null!;
    private Panel _bar = null!;

    /// <summary>订阅来源。选择与「并入历史 N 回」都在弹窗里设定。</summary>
    [Flags]
    public enum SpeedSource { None = 0, Favorites = 1, Results = 2, Manual = 4 }

    private SpeedSource _sources = SpeedSource.Favorites;
    private string _manualUrls = "";
    private bool _withHistory = true;
    private int _keepRounds = 3;

    private List<SpeedTestNode> _nodes = new();

    public SpeedTestPanel()
    {
        BackColor = Theme.Paper;
        Font = Fonts.Ui9;
        DoubleBuffered = true;
        Build();
    }

    // ══════════════════ 布局 ══════════════════

    private void Build()
    {
        // 本页只保留 控制条 + 进度 + 统计。
        // 参数设置走弹窗（两个页面：表单 / YAML），
        // 检测结果与实时日志统一放到右侧详情日志面板。
        var top = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper };
        BuildTop(top);
        Controls.Add(top);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _pollTimer.Tick += (s, e) =>
        {
            if (_polling) return;      // 上一轮还没跑完，跳过本次
            _polling = true;
            _pollTimer.Stop();         // 跑完再由 PollAsync 自己决定是否续
            _ = PollAsync();
        };

        _runner.OnLog += line =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    var color = line.Contains("WARN") ? Theme.Ochre
                             : line.Contains("ERROR") || line.Contains("error") ? Theme.Stamp
                             : Theme.InkMid;
                    OnLogLine?.Invoke(line, color);
                });
            }
            catch { }
        };
    }

    private void BuildTop(Panel host)
    {
        host.Padding = new Padding(14, 10, 14, 8);

        // 控制条：按钮组，从左到右
        //   开始测速 · 订阅来源 · 测速参数 · 内核状态 · 访问WebUI · 打开配置目录 · 复制订阅
        // 运行中「开始测速」自身变成「■ 停止」，不再额外占一个位置。
        //
        // 不用 AutoScroll：FlowLayoutPanel 的 AutoScroll 会同时拉出横竖两个
        // 滚动条，窄窗口下底部出现一条难看的横条。改为 WrapContents=true，
        // 按钮在宽度不足时自动折到下一行，容器高度随之长高。
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.PaperHi,
            WrapContents = true, Padding = new Padding(8, 4, 8, 4),
            AutoScroll = false,
        };

        ModernButton Btn(string text, EventHandler onClick, int pad = 0)
        {
            var b = new ModernButton
            {
                Text = text, Ghost = true, Height = UiMetrics.ButtonHeight,
                Margin = new Padding(0, 1, UiMetrics.SpaceSm, 0),
            };
            b.Click += onClick;
            UiMetrics.FitWidth(b, text, pad);
            return b;
        }

        _btnRun = new ModernButton
        {
            Text = "▶ 开始测速", Height = UiMetrics.ButtonHeight,
            Margin = new Padding(0, 1, UiMetrics.SpaceSm, 0),
        };
        UiMetrics.FitWidth(_btnRun);
        _btnRun.Click += async (s, e) => await RunOrStopAsync();
        row.Controls.Add(_btnRun);

        // 按钮文字固定。上面这一排是功能入口，不该跟着当前选择变形
        _btnSource = Btn("订阅来源", (s, e) => OpenSourceDialog());
        row.Controls.Add(_btnSource);

        _btnSettings = Btn("⚙ 测速参数", (s, e) =>
        {
            using var dlg = new SpeedTestSettingsDialog(_runner);
            dlg.ShowDialog(FindForm());
        });
        row.Controls.Add(_btnSettings);

        _btnKernel = Btn("⚙ 内核状态", (s, e) => _ = ManageKernelAsync());
        row.Controls.Add(_btnKernel);
        RefreshKernelButton();

        _btnWebUi = Btn("◈ 访问 WebUI", (s, e) => OpenWebUi());
        _btnWebUi.Enabled = false;
        row.Controls.Add(_btnWebUi);

        // 原来这里还有个「打开配置目录」。测速参数弹窗底部已经有同一个按钮，
        // 一排入口放两个重复的是噪音，去掉。

        _btnCopySub = Btn("⧉ 复制订阅", (s, e) => CopySubscription());
        row.Controls.Add(_btnCopySub);

        host.Controls.Add(row);

        // 状态行
        // 状态行 + 进度条：放在控制条下方的容器里，跟随控制条高度，
        // 不用绝对 Y 坐标 —— 控制条折行时绝对坐标会和按钮重叠。
        var statusHost = new Panel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent,
            Padding = new Padding(14, 10, 14, 4),
        };

        _lblState = new Label { Text = "● 空闲", Font = Fonts.Mono9, ForeColor = Theme.InkMid,
            AutoSize = true, Location = new Point(0, 0), Margin = new Padding(0) };
        _lblNums = new Label { Text = "", Font = Fonts.Mono9, ForeColor = Theme.InkMid,
            AutoSize = true, Location = new Point(300, 0), Margin = new Padding(0) };
        _bar = new Panel { Location = new Point(0, 22), Height = 3, BackColor = Theme.PaperDeep };

        statusHost.Controls.Add(_bar);
        statusHost.Controls.Add(_lblNums);
        statusHost.Controls.Add(_lblState);
        host.Controls.Add(statusHost);

        // 统计数字移到右侧，避免和状态文字抢同一行
        _lblNums.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        statusHost.Resize += (s, e) =>
        {
            _lblNums.Left = Math.Max(220, statusHost.ClientSize.Width - _lblNums.Width);
        };

        ResizeBar();
        host.Resize += (s, e) => ResizeBar();
    }

    private void ResizeBar()
    {
        if (_bar.Parent is Control p)
            _bar.Width = Math.Max(100, p.ClientSize.Width - 8);
    }

    private static int countFavorites()
        => FavoritesStore.Load().SelectMany(r => r.Links).Count(l => l.IsValid && l.NodeCount > 0);

    private int countResults() => GetSubSources?.Invoke().Count ?? 0;

    /// <summary>本轮选了哪些来源。只用于状态栏提示，不进按钮文字。</summary>
    private static readonly char[] ManualSplit = { (char)13, (char)10, (char)9, ' ' };
    private string Summary()
    {
        var parts = new List<string>();
        if (_sources.HasFlag(SpeedSource.Favorites)) parts.Add($"收藏仓库 {countFavorites()}");
        if (_sources.HasFlag(SpeedSource.Results)) parts.Add($"搜索结果 {countResults()}");
        if (_sources.HasFlag(SpeedSource.Manual))
        {
            var c = (_manualUrls ?? "").Split(ManualSplit, StringSplitOptions.RemoveEmptyEntries).Length;
            parts.Add($"手动粘贴 {c}");
        }
        return parts.Count == 0 ? "未选择来源" : string.Join(" + ", parts);
    }

    private static LinkLabel MakeLink(string text, Action onClick)
    {
        var l = new LinkLabel
        {
            Text = text, Font = Fonts.Mono8, ForeColor = Theme.InkMid,
            AutoSize = true, Margin = new Padding(0, 7, 10, 0),
            LinkBehavior = LinkBehavior.NeverUnderline, BackColor = Color.Transparent,
            TabStop = false
        };
        l.LinkClicked += (s, e) => { try { onClick(); } catch { } };
        return l;
    }

    // ══════════════════ 运行流程 ══════════════════

    /// <summary>收集本轮的订阅来源。手动粘贴没有来源仓库，结果不计入仓库统计。</summary>
    private List<SubSource> GatherSubSources()
    {
        var list = new List<SubSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_sources.HasFlag(SpeedSource.Favorites))
        {
            foreach (var r in FavoritesStore.Load())
                foreach (var l in r.Links.Where(l => l.IsValid && l.NodeCount > 0))
                    if (seen.Add(l.Url)) list.Add(new SubSource { Url = l.Url, Repo = r.FullName });
        }

        if (_sources.HasFlag(SpeedSource.Results))
        {
            foreach (var s in GetSubSources?.Invoke() ?? new List<SubSource>())
                if (seen.Add(s.Url)) list.Add(s);
        }

        if (_sources.HasFlag(SpeedSource.Manual))
        {
            foreach (var u in (_manualUrls ?? "")
                     .Split(ManualSplit, StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => x.Trim()).Where(x => x.Length > 0))
                if (seen.Add(u)) list.Add(new SubSource { Url = u });
        }
        return list;
    }


    /// <summary>来源 → 带备注的 URL。备注让内核把来源写进节点名与 subTag，
    /// 结果才能映射回仓库。手动粘贴无来源，不加备注。</summary>
    /// <summary>开始测速；运行中再点一次即停止 —— 不为停止额外占一个按钮位。</summary>
    private async Task RunOrStopAsync()
    {
        if (_runner.IsRunning)
        {
            Cancel();
            return;
        }
        await RunAsync();
    }

    private void OpenSourceDialog()
    {
        using var dlg = new SpeedSourceDialog(
            _sources, _manualUrls, _withHistory, _keepRounds,
            () => FavoritesStore.Load().SelectMany(r => r.Links)
                    .Count(l => l.IsValid && l.NodeCount > 0),
            () => GetSubSources?.Invoke().Count ?? 0);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

        _sources = dlg.Selected.Count == 0 ? SpeedSource.None : dlg.Selected.Aggregate((a, b) => a | b);
        _manualUrls = dlg.ManualUrls;
        _withHistory = dlg.WithHistory;
        _keepRounds = dlg.KeepRounds;
        // 按钮文字固定为「订阅来源」，不随选择变化 —— 上面一排按钮是入口，
        // 跟着选择变形会让人以为每个选项是一个独立功能。
        // 选了什么是本轮的事，跑起来后状态栏会说明。
        ShowToastLabel($"订阅来源已更新 · {Summary()}");
    }

    private void ShowToastLabel(string text) => SetState("● " + text, Theme.InkMid);

    private async Task RunAsync()
    {
        var sources = GatherSubSources();
        if (sources.Count == 0)
        {
            var msg = _sources.HasFlag(SpeedSource.Manual)
                ? "没有可测的订阅链接。\n\n请点「订阅来源」，在弹窗里粘贴订阅地址（每行一条）。"
                : _sources.HasFlag(SpeedSource.Results)
                    ? "当前表格里没有已验证的订阅链接。\n\n请先「搜索并分析」，或改选其他订阅来源。"
                    : "收藏夹是空的。\n\n请在搜索结果里右键仓库 → ★ 加入收藏，或改选其他来源。";
            MessageBox.Show(FindForm(), msg, "节点测速", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var urls = Common.ToTaggedUrls(sources);

        var ver = SubCheckKernel.LocalVersion();
        if (ver == null)
        {
            var r = MessageBox.Show(FindForm(),
                "尚未安装 subs-check 内核（约 55MB）。\n\n点击「确定」下载并安装后开始测速。",
                "节点测速", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
            var ok = await ManageKernelAsync(forceInstall: true);
            if (!ok) return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _polling = false;
        _finished = false;

        _btnRun.Text = "■ 停止";
        _btnRun.Enabled = true;
        _btnSource.Enabled = false;
        lock (_logGate) _logTail.Clear();
        OnLogLine?.Invoke("CLEAR", Theme.InkLow);   // 哨兵：通知详情面板清空
        SetState("● 启动内核", Theme.Stamp);

        try
        {
            // 顺序很重要：先准备好 session.yaml（含端口、订阅、历史），
            // 再启动内核 —— 内核启动那一刻就读这个文件。
            await _runner.PrepareAsync(urls, _withHistory, _keepRounds);
            await _runner.StartAsync(l => LogLine(l, Theme.InkMid), ct);

            SetState("● 触发检测", Theme.Stamp);
            if (!await _runner.TriggerAsync(ct))
            {
                LogLine("触发检测失败：内核拒绝了 /api/trigger-check", Theme.Stamp);
                return;
            }

            _pollTimer.Start();
            _btnWebUi.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            SetState("● 已取消", Theme.InkMid);
        }
        catch (Exception ex)
        {
            LogLine("启动失败: " + ex.Message, Theme.Stamp);
            SetState("● 失败", Theme.Stamp);
        }
        finally
        {
            // 只有还在跑才保持「停止」态；异常/取消都要回到「开始」
            if (_runner.IsRunning)
            {
                _btnRun.Text = "■ 停止";
            }
            else
            {
                _btnRun.Text = "▶ 开始测速";
                _btnSource.Enabled = true;
            }
        }
    }

    private void Cancel()
    {
        _pollTimer.Stop();
        _finished = true;      // 阻止 finally 里的重启
        _cts.Cancel();
        _ = _runner.StopAsync();
        SetState("● 已停止", Theme.InkMid);
        _btnRun.Text = "▶ 开始测速";
        _btnSource.Enabled = true;
        _btnWebUi.Enabled = false;
    }

    private async Task PollAsync()
    {
        try
        {
            var ct = _cts.Token;
            var st = await _runner.GetStatusAsync(ct);
            if (st == null) { _pollTimer.Stop(); return; }

            var p = st.Pipeline;
            int total = p?.Total ?? st.ProxyCount;
            int alivePass = p?.AlivePass ?? st.Available;
            int speedDone = p?.SpeedDone ?? 0;
            int speedPass = p?.SpeedPass ?? 0;

            _lblNums.Text = $"{total} 节点 · 存活 {alivePass} · 测速通过 {speedPass}";
            if (_bar.Parent is Control bp)
                _bar.Width = Math.Max(100, bp.ClientSize.Width - 8);
            var inner = _bar.Controls.Count > 0 ? _bar.Controls[0] : null;
            if (inner == null)
            {
                inner = new Panel { Width = 0, BackColor = Theme.Ink };
                _bar.Controls.Add(inner);
            }
            inner.Width = total > 0 ? (int)(_bar.Width * Math.Min(1.0, (double)speedDone / total)) : 0;

            if (!_finished && !st.Checking && total > 0 && speedDone >= total)
            {
                _finished = true;      // 单次开关：状态可能仍在完成态上多闪一拍
                _pollTimer.Stop();
                await FinishAsync(ct);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 取消是正常退出路径：GetStatusAsync 在取消/停止时抛 OCE，
            // 不吞的话会成为未观察任务异常。这里静默，由 finally 决定是否重启。
        }
        finally
        {
            _polling = false;
            if (!_finished && !_cts.IsCancellationRequested && _runner.IsRunning)
                _pollTimer.Start();
        }
    }

    private async Task FinishAsync(CancellationToken ct)
    {
        SetState("● 读取结果", Theme.Stamp);
        var snap = await _runner.GetResultsAsync(ct);
        if (snap == null)
        {
            LogLine("读取结果失败", Theme.Stamp);
            SetState("● 完成", Theme.InkMid);
            return;
        }

        // 趁内核还在运行，从 /sub/all.yaml 拿完整的节点 YAML（含 uuid/password 等凭据）。
        var kernelYaml = await _runner.GetPassedSubscriptionAsync(CancellationToken.None);

        _nodes = snap.Nodes ?? new List<SpeedTestNode>();
        OnNodesChanged?.Invoke();
        _runner.SaveRound(snap, kernelYaml);

        // 写回仓库统计：按 subTag（订阅链接末尾的 #备注）分组
        var rounds = SpeedResultAggregator.GroupByRepo(_nodes);
        var changed = SpeedTestStore.ApplyRounds(rounds);
        OnRoundApplied?.Invoke(changed);

        SetState($"● 完成 · {DateTime.Now:HH:mm} · 内核 subs-check {SubCheckKernel.LocalVersion()}", Theme.Live);
        LogLine($"[ProxyNodeHub] 本轮 {_nodes.Count} 节点，通过 {_nodes.Count(n => n.Passed)}，已存快照", Theme.Live);
        if (changed > 0)
            LogLine($"[ProxyNodeHub] 已更新 {changed} 个仓库的可用分，可用仓库排序查看", Theme.Live);
    }

    /// <summary>平台名 → 两字缩写。实现已下沉到 Core，与 Web 端共用一份。</summary>
    public static string ShortPlatform(string p) => SpeedResultAggregator.ShortPlatform(p);

    /// <summary>
    /// 记一条日志：本地缓存一份（详情面板重新挂载时回填），并抛给主窗体。
    /// 不直接写控件 —— 控件归详情面板所有。
    /// </summary>
    private void LogLine(string line, Color color)
    {
        lock (_logGate)
        {
            _logTail.Append(line).Append('\n');
            Common.TrimLogTail(_logTail);
        }
        try { OnLogLine?.Invoke(line, color); } catch { }
    }

    /// <summary>累计日志，供详情面板初次挂载时回填。</summary>
    public string LogTailText
    {
        get { lock (_logGate) return _logTail.ToString(); }
    }

    private void SetState(string text, Color c)
    {
        try
        {
            _lblState.BeginInvoke(() => { _lblState.Text = text; _lblState.ForeColor = c; });
        }
        catch { }
    }

    // ══════════════════ 操作 ══════════════════

    private void CopySubscription()
    {
        var url = _runner.IsRunning ? $"http://127.0.0.1:{_runner.Port}/sub/all.yaml" : null;
        if (url == null)
        {
            MessageBox.Show(FindForm(), "内核未在运行。请先开始一次测速。", "节点测速",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Clipboard.SetText(url); SetState("● 订阅链接已复制", Theme.Live); }
        catch { }
    }

    private void OpenWebUi()
    {
        if (!_runner.IsRunning) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://127.0.0.1:{_runner.Port}/admin",
                UseShellExecute = true
            });
        }
        catch { }
    }


    // ══════════════════ 内核管理 ══════════════════

    private async Task<bool> ManageKernelAsync(bool forceInstall = false)
    {
        if (!forceInstall)
        {
            using var dlg = new KernelDialog(WithProxy);
            dlg.ShowDialog(FindForm());
            if (dlg.RequestedManual) return await ManualInstallKernelAsync();
            // 改了安装位置就同步按钮文字，让用户立刻看到生效
            if (dlg.Changed) RefreshKernelButton();
            return false;
        }
        return await InstallKernelAsync();
    }

    /// <summary>内核按钮文字反映当前状态：未安装 / 已安装版本。</summary>
    private void RefreshKernelButton()
    {
        var v = SubCheckKernel.LocalVersion();
        _btnKernel.Text = v == null ? "⚙ 内核状态 · 未安装" : $"⚙ 内核状态 · {SubCheckKernel.Display(v)}";
        UiMetrics.FitWidth(_btnKernel);
    }

    private async Task<bool> InstallKernelAsync()
    {
        _btnKernel.Enabled = false;
        try
        {
            LogLine("[ProxyNodeHub] 正在查询 subs-check 最新版本…", Theme.InkMid);
            SetState("● 查询内核版本", Theme.Stamp);

            var rel = await SubCheckKernel.FetchLatestAsync(WithProxy, _cts.Token);
            if (rel == null)
            {
                MessageBox.Show(FindForm(),
                    $"查询内核版本失败。\n\n网络不通时可改用「从本地 zip 安装」，\n或手动把 subs-check.exe 放到：\n{SpeedTestConfig.KernelPath}",
                    "节点测速", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            LogLine($"[ProxyNodeHub] 最新版本 {rel.Version} · {rel.SizeBytes / 1048576.0:F1} MB", Theme.InkMid);

            var ok = await SubCheckKernel.InstallAsync(rel,
                p => { if (p.Phase is "下载" or "校验" or "解压") LogLine($"  {p.Phase} {p.Percent:F0}%", Theme.InkLow); },
                _cts.Token, WithProxy);

            if (!ok) return false;

            LogLine($"[ProxyNodeHub] 内核 {rel.Version} 安装完成（SHA256 已校验）", Theme.Live);
            SetState("● 内核就绪", Theme.Live);
            SubCheckKernel.RemoveBackup();
            return true;
        }
        catch (Exception ex)
        {
            LogLine("[ProxyNodeHub] 内核安装失败: " + ex.Message, Theme.Stamp);
            MessageBox.Show(FindForm(),
                "内核安装失败：\n" + ex.Message +
                "\n\n可改用「从本地 zip 安装」（下载已中断的进度会保留，下次续传）。",
                "节点测速", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _btnKernel.Enabled = true;
        }
    }

    /// <summary>从用户本地 zip 安装 —— 国内拉 55MB 常失败，这是必要的兜底。</summary>
    private async Task<bool> ManualInstallKernelAsync()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择 subs-check_Windows_x86_64.zip",
            Filter = "内核压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return false;

        _btnKernel.Enabled = false;
        try
        {
            SetState("● 安装内核", Theme.Stamp);
            var rel = new SubCheckKernel.ReleaseInfo
            {
                Version = "manual",
                DownloadUrl = "",
                SizeBytes = new FileInfo(ofd.FileName).Length,
            };
            // 全程 await，不能在 UI 线程上 .GetResult() —— InstallFromLocalAsync
            // 内部 await CopyToAsync，续文要回投 UI 同步上下文，会死锁。
            var ok = await SubCheckKernel.InstallFromLocalAsync(ofd.FileName, rel,
                p => LogLine($"  {p.Phase} {p.Percent:F0}%", Theme.InkLow),
                _cts.Token);
            if (!ok) return false;

            LogLine("[ProxyNodeHub] 内核已从本地 zip 安装（未做 SHA256 校验，请确认来源可信）", Theme.Ochre);
            SetState("● 内核就绪", Theme.Ochre);
            SubCheckKernel.RemoveBackup();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), "本地安装失败：\n" + ex.Message, "节点测速",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _btnKernel.Enabled = true;
        }
    }

    /// <summary>内核下载/查询走的最快 GitHub 镜像。由主窗体提供；未提供则直连。</summary>
    public Func<string, string>? KernelProxy { get; set; }

    private string WithProxy(string url)
        => KernelProxy?.Invoke(url) ?? url;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _pollTimer?.Stop();
        _cts?.Cancel();
        _runner?.Dispose();
        base.OnHandleDestroyed(e);
    }
}
