using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ProxyNodeHub;

public class ExportDialog : Form
{
    private readonly TextBox _txt;
    private readonly string _originalText;
    private readonly GitHubService _github;
    private readonly List<RepoInfo> _repos;
    /// <summary>订阅转换后端端点；非空时合并走它归一成 URI 列表。</summary>
    private readonly string? _subConverterUrl;
    private ModernButton _btnMerge = null!, _btnBack = null!, _btnCopy = null!, _btnExport = null!;

    public ExportDialog(List<RepoInfo> repos, GitHubService github, string? subConverterUrl = null)
    {
        _repos = repos;
        _github = github;
        _subConverterUrl = string.IsNullOrWhiteSpace(subConverterUrl) ? null : subConverterUrl!.Trim();

        Text = "订阅链接";
        Size = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Fonts.Ui9;

        var linkCount = repos.Sum(r => r.Links.Count);
        var nodeCount = repos.Sum(r => r.TotalNodes);
        var lblTitle = new Label
        {
            Text = $"{repos.Count} 个仓库  ·  {linkCount} 条链接  ·  约 {nodeCount} 个节点",
            Dock = DockStyle.Top, Height = 40,
            ForeColor = Theme.InkMid,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 12, 0, 0),
            BackColor = Theme.Paper
        };

        _txt = new TextBox
        {
            Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Theme.PaperDeep,
            ForeColor = Theme.LogGreen,
            Font = Fonts.Mono85,
            BorderStyle = BorderStyle.None
        };

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# GitHub 免费节点订阅链接");
        sb.AppendLine($"# 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# 仓库: {repos.Count} 个 (按活跃度排序)");
        sb.AppendLine("# 标注 [未验证] 的链接因网络原因未能确认, 请自行测试");
        sb.AppendLine();
        foreach (var r in repos)
        {
            sb.AppendLine($"# ── {r.FullName} ── 活跃度:{r.Score} | {r.StatusText} | {r.ProcessingType}");
            if (r.Links.Count == 0)
                sb.AppendLine("#   (未检测到订阅链接)");
            foreach (var link in r.Links)
            {
                var mark = link.NodeCount > 0 ? $" {link.NodeCount}节点" : link.NodeCount == -1 ? " [未验证]" : " [无效]";
                sb.AppendLine($"{link.Url}    # {link.Name} ({link.Type}{mark})");
            }
            sb.AppendLine();
        }
        _txt.Text = sb.ToString();
        _originalText = _txt.Text;
        _txt.SelectionStart = 0;
        _txt.SelectionLength = 0;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Paper };

        var btnClose = new ModernButton { Text = "关 闭", Ghost = true, BackColor = Theme.Paper, Size = new Size(88, 34) };
        var btnExport = new ModernButton { Text = "导出文件", Ghost = true, BackColor = Theme.Paper, Size = new Size(100, 34) };
        _btnExport = btnExport;
        _btnCopy = new ModernButton { Text = "复制全部", Ghost = true, BackColor = Theme.Paper, Size = new Size(100, 34) };
        _btnMerge = new ModernButton { Text = "⚡ 合并订阅", BaseColor = Theme.Stamp, ForeColor = Theme.Paper, Size = new Size(110, 34) };
        _btnBack = new ModernButton { Text = "↩ 链接列表", Ghost = true, BackColor = Theme.Paper, Size = new Size(100, 34), Enabled = false };

        LayoutBottomButtons(btnClose, btnExport);

        btnClose.Click += (s, e) => Close();

        btnExport.Click += (s, e) =>
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|Base64 订阅 (*.b64)|*.b64|所有文件 (*.*)|*.*",
                FileName = $"proxy_links_{DateTime.Now:yyyyMMdd_HHmm}.txt"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(dlg.FileName, _txt.Text);
                FeedbackButton(btnExport, "已导出!", "导出文件");
            }
        };

        _btnCopy.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(_txt.Text))
            {
                Clipboard.SetText(_txt.Text);
                FeedbackButton(_btnCopy, "已复制!", "复制全部");
            }
        };

        _btnMerge.Click += async (s, e) => await MergeSubscriptions();
        _btnBack.Click += (s, e) =>
        {
            _txt.Text = _originalText;
            _btnMerge.Enabled = true;
            _btnMerge.Text = "⚡ 合并订阅";
            _btnBack.Enabled = false;
        };

        bottom.Controls.Add(_btnBack);
        bottom.Controls.Add(_btnMerge);
        bottom.Controls.Add(_btnCopy);
        bottom.Controls.Add(btnExport);
        bottom.Controls.Add(btnClose);

        Controls.Add(_txt);
        Controls.Add(lblTitle);
        Controls.Add(bottom);

        Load += (s, e) => { Win32Interop.EnableDarkTitle(this, false); Win32Interop.EnableRoundedCorners(this); };
        KeyPreview = true;
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    private void LayoutBottomButtons(ModernButton btnClose, ModernButton btnExport)
    {
        // 从右到左排列
        int right = 12;
        btnClose.Location = new Point(ClientSize.Width - right - btnClose.Width, 13);
        right += btnClose.Width + 8;
        btnExport.Location = new Point(ClientSize.Width - right - btnExport.Width, 13);
        right += btnExport.Width + 8;
        _btnCopy.Location = new Point(ClientSize.Width - right - _btnCopy.Width, 13);
        right += _btnCopy.Width + 8;
        _btnMerge.Location = new Point(ClientSize.Width - right - _btnMerge.Width, 13);
        right += _btnMerge.Width + 8;
        _btnBack.Location = new Point(ClientSize.Width - right - _btnBack.Width, 13);
    }

    private async Task MergeSubscriptions()
    {
        var validLinks = _repos.SelectMany(r => r.Links.Where(l => l.IsValid)).ToList();
        if (validLinks.Count == 0)
        {
            _btnMerge.Text = "无有效链接";
            return;
        }

        _btnMerge.Enabled = false;
        var contents = new List<string>();
        try
        {
            for (int i = 0; i < validLinks.Count; i++)
            {
                _btnMerge.Text = $"合并中 {i + 1}/{validLinks.Count}…";
                // 非纯 URI 订阅过 subconverter 归一；端点已带查询串用 & 接续。
                var link = validLinks[i];
                var fetchUrl = _subConverterUrl != null && link.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? $"{_subConverterUrl}{(_subConverterUrl.Contains('?') ? "&" : "?")}target=uri&url={Uri.EscapeDataString(link.Url)}"
                    : link.Url;
                var content = await _github.GetUrlAsync(fetchUrl);
                if (!string.IsNullOrEmpty(content)) contents.Add(content);
            }

            var (plain, b64, total, unique) = NodeParser.MergeNodes(contents);
            _txt.Text = $"# 合并订阅 · {validLinks.Count} 个来源 · 原始 {total} 节点 → 去重后 {unique} 个\n" +
                        $"# 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"# 直接复制下方 Base64 到客户端 (V2RayN / v2rayNG / Shadowrocket):\n\n{b64}";
            _txt.SelectionStart = 0;
            _txt.SelectionLength = 0;
            _btnMerge.Text = $"✓ 已合并 {unique} 节点";
            _btnBack.Enabled = true;
        }
        catch (Exception)
        {
            _btnMerge.Text = "合并失败";
            _btnMerge.Enabled = true;
        }
    }

    private void FeedbackButton(ModernButton btn, string feedback, string normal)
    {
        btn.Text = feedback;
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (s2, e2) => { btn.Text = normal; t.Stop(); t.Dispose(); };
        t.Start();
    }
}
