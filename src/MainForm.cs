using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProxyNodeHub;

public class MainForm : Form
{
    private const int InspectorW = 320;

    private TableLayoutPanel _root = null!;
    private Panel _content = null!, _inspectorHost = null!, _inspector = null!, _progressLine = null!;
    private SpeedTestPanel _speedPanel = null!;
    private TextBox _txtToken = null!, _txtFilter = null!;
    private NumericUpDown _numRepos = null!, _numMinCommits = null!, _numActiveDays = null!, _numInactiveDays = null!, _numSaveDays = null!, _numLogSaveDays = null!;
    private ComboBox _cmbQueryMode = null!, _cmbProxy = null!;
    private ModernButton _btnTestProxy = null!;
    private ModernButton _btnSearch = null!, _btnExport = null!, _btnInspector = null!, _btnFavorites = null!, _btnFeatureLib = null!, _btnSpeedTest = null!;
    private ModernButton _btnTabDetails = null!, _btnTabLog = null!, _btnEye = null!, _btnResetHistory = null!;
    private CheckBox _chkAutoClean = null!;
    private DataGridView _dgv = null!;
    // 流式刷新节流：分析阶段每个仓库完成都触发 RefreshGrid，100 个仓库就是
    // 100 次整表重建。这里把 120ms 内的多次刷新合并成一次。
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _refreshQueued = false;
    private Panel _emptyPanel = null!;
    private Label _emptyHead = null!, _emptySub = null!;
    private Panel _detailsPanel = null!, _logPanel = null!;
    private SpeedResultPanel _speedResultPanel = null!;
    private ModernButton _btnTabSpeed = null!;
    private TextBox _txtLog = null!;
    private ToolTip _tooltip = null!;
    private ContextMenuStrip _ctxMenu = null!;
    private bool _showToken;

    // Toast
    private Panel _toast = null!;
    private Label _toastLabel = null!;
    private bool _toastPersistent;
    private string _lastToastMsg = "";
    private System.Windows.Forms.Timer? _toastAnim, _toastDelay;

    // 检查器
    private bool _inspectorOpen;
    private string _inspectorTab = "details";

    // 代理测速缓存
    private DateTime _lastProxyTestTime = DateTime.MinValue;

    // 日志缓冲
    private System.Text.StringBuilder _logBuffer = new();
    private System.Windows.Forms.Timer? _logFlushTimer;

    // 收藏
    private List<RepoInfo> _favorites = new();
    private string _viewMode = "search";
    private bool _speedView;
    private bool IsFavoritesView => _viewMode == "favorites";

    private int _progressPct;

    private List<RepoInfo> _allRepos = new();
    private List<RepoInfo> _filteredRepos = new();
    private RepoInfo? _contextRepo;
    private GitHubService _github = new();
    private AppSettings _settings = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _sortField = "Usable";
    private bool _sortDesc = true;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int EM_SETCUEBANNER = 0x1501;

    public MainForm()
    {
        _settings = AppSettings.Load();
        _favorites = FavoritesStore.Load();
        SpeedTestDefaults.Ensure();
        SpeedTestDefaults.EnsureExampleStub();
        SpeedTestStore.Load();
        BuildUi();
        ApplySettings();
        LoadCache();
        LoadLog();
    }

    /// <summary>加载历史日志</summary>
    private void LoadLog()
    {
        try
        {
            LogStore.SaveDays = (int)_numLogSaveDays.Value;
            var log = LogStore.Load();
            if (!string.IsNullOrEmpty(log))
                _txtLog.AppendText(log);
        }
        catch { }
    }

    // ══════════════════════ UI 构建 ══════════════════════

    private void BuildUi()
    {
        Text = "ProxyNodeHub";
        Size = new Size(1180, 740);
        MinimumSize = new Size(1040, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Fonts.Ui9;
        DoubleBuffered = true;
        KeyPreview = true;

        // 设置窗体图标（从 EXE 资源中提取）
        try
        {
            var assemblyIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (assemblyIcon != null) Icon = assemblyIcon;
        }
        catch { /* 忽略图标加载失败 */ }
        Opacity = 0;

        KeyDown += OnKeyDown;
        Load += OnLoad;
        Resize += (s, e) => { if (_toast.Visible) CenterToastX(); };
        FormClosing += OnFormClosing;

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 8),
            RowCount = 3,
            BackColor = Theme.Paper
        };
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132f));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 4f));
        Controls.Add(_root);

        _tooltip = new ToolTip
        {
            InitialDelay = 2200, AutoPopDelay = 12000, ReshowDelay = 800, ShowAlways = true
        };

        BuildHeader();
        BuildContent();
        BuildProgressLine();
        BuildToast();
        SetupTooltips();
    }

    private void BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Theme.PaperHi,
            Padding = new Padding(16, 8, 16, 4), Margin = new Padding(0, 0, 0, 10)
        };
        header.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Hairline, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, header.Width - 1, header.Height - 1);
        };

        var brandRow = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.Transparent };
        var brand = new Label
        {
            Text = "ProxyNodeHub", Font = Fonts.SerifBrand, ForeColor = Theme.Ink,
            AutoSize = true, Dock = DockStyle.Left
        };
        var brandTag = new Label
        {
            Text = "NODE INTELLIGENCE · 节点情报台", Font = Fonts.Mono8, ForeColor = Theme.InkMid,
            Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleRight, AutoSize = true,
            Padding = new Padding(0, 5, 0, 0), Cursor = Cursors.Hand
        };
        brandTag.Click += (s, e) => { using var dlg = new AboutDialog(); dlg.ShowDialog(this); };
        brandRow.Controls.Add(brand);
        brandRow.Controls.Add(brandTag);

        var inkRule = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Theme.Rule };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent, WrapContents = false };
        toolbar.Controls.Add(MakeLabel("GitHub秘钥："));
        _txtToken = new TextBox
        {
            Font = Fonts.Mono9,
            BackColor = Theme.PaperDeep, ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.None, Margin = new Padding(0),
            PasswordChar = '•'
        };
        toolbar.Controls.Add(MakeField(_txtToken, 150));

        _btnEye = new ModernButton { Text = "👁", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(28, 26), CornerRadius = 2, Margin = new Padding(0, 4, 6, 0) };
        _btnEye.Click += (s, e) =>
        {
            _showToken = !_showToken;
            _txtToken.PasswordChar = _showToken ? '\0' : '•';
            _btnEye.Text = _showToken ? "🙈" : "👁";
        };
        toolbar.Controls.Add(_btnEye);

        _btnSearch = new ModernButton { Text = "🔍 搜索并分析", BaseColor = Theme.Stamp, ForeColor = Theme.Paper, Size = new Size(110, 26), Margin = new Padding(0, 4, 6, 0) };
        UiMetrics.FitWidth(_btnSearch);
        _btnSearch.Click += (s, e) => OnSearchClick();
        toolbar.Controls.Add(_btnSearch);

        // 处理模式
        toolbar.Controls.Add(MakeLabel("处理模式"));
        _cmbQueryMode = new ComboBox
        {
            Width = 86, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.PaperDeep, ForeColor = Theme.Ink,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 6, 0),
            Font = Fonts.Mono85
        };
        _cmbQueryMode.Items.AddRange(new object[] { "🚀 极速", "⚖️ 标准", "🔍 深度", "🐢 兼容" });
        _cmbQueryMode.SelectedIndex = 1;
        toolbar.Controls.Add(_cmbQueryMode);

        _btnExport = new ModernButton { Text = "📋 订阅链接", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(96, 26), Margin = new Padding(0, 4, 6, 0), Enabled = false };
        UiMetrics.FitWidth(_btnExport);
        _btnExport.Click += (s, e) => ShowExportDialog();
        toolbar.Controls.Add(_btnExport);

        // 特征库按钮（原「复制最佳」的位置）
        _btnFeatureLib = new ModernButton { Text = "🗃 特征库", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(88, 26), Margin = new Padding(0, 4, 6, 0) };
        UiMetrics.FitWidth(_btnFeatureLib);
        _btnFeatureLib.Click += (s, e) => ShowFeatureLibraryDialog();
        toolbar.Controls.Add(_btnFeatureLib);

        _btnFavorites = new ModernButton { Text = "★ 收藏", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(72, 26), Margin = new Padding(0, 4, 6, 0) };
        UiMetrics.FitWidth(_btnFavorites);
        _btnFavorites.Click += (s, e) => ToggleViewMode();
        toolbar.Controls.Add(_btnFavorites);

        _txtFilter = new TextBox
        {
            Font = Fonts.Ui9,
            BackColor = Theme.PaperDeep, ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.None, Margin = new Padding(0)
        };
        _txtFilter.TextChanged += (s, e) => ApplyFilters();
        _txtFilter.HandleCreated += (s, e) => SendMessage(_txtFilter.Handle, EM_SETCUEBANNER, (IntPtr)1, "🔍 筛选 (Ctrl+F)");
        var filterField = MakeField(_txtFilter, 120);
        filterField.Margin = new Padding(10, 4, 8, 0);
        toolbar.Controls.Add(filterField);

        // 筛选行: 两个明确的 FlowLayoutPanel 行 (Dock=Top, 固定高度)
        //
        // 不用 AutoScroll：FlowLayoutPanel 的 AutoScroll 同时拉横竖两个条，
        // 窄窗口下主界面底部会多一条横滚动条。改为允许折行 —— 宽度不足时
        // 次要控件自动换到第二行，容器高度随之长高，不产生滚动条。
        var filterRow1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent,
            WrapContents = true, AutoScroll = false,
            Padding = new Padding(4, 2, 4, 2),
            Margin = new Padding(0, 2, 0, 0),
        };
        filterRow1.Controls.Add(MakeLabel("仓库数"));
        _numRepos = MakeNum(38, 10, 100, 30);
        filterRow1.Controls.Add(_numRepos);
        filterRow1.Controls.Add(MakeLabel("日均≥"));
        _numMinCommits = MakeNum(38, 0, 50, 0);
        _numMinCommits.ValueChanged += (s, e) => ApplyFilters();
        filterRow1.Controls.Add(_numMinCommits);
        filterRow1.Controls.Add(MakeLabel("库龄≥"));
        _numActiveDays = MakeNum(44, 0, 3650, 0);
        _numActiveDays.ValueChanged += (s, e) => ApplyFilters();
        filterRow1.Controls.Add(_numActiveDays);
        filterRow1.Controls.Add(MakeLabel("不活跃>"));
        _numInactiveDays = MakeNum(38, 3, 30, 7);
        _numInactiveDays.ValueChanged += (s, e) => ApplyFilters();
        filterRow1.Controls.Add(_numInactiveDays);
        _chkAutoClean = new CheckBox { Text = "清理", Checked = true, AutoSize = true, ForeColor = Theme.Ink, Margin = new Padding(4, 5, 6, 0) };
        _chkAutoClean.CheckedChanged += (s, e) => ApplyFilters();
        filterRow1.Controls.Add(_chkAutoClean);

        // 保存天数
        filterRow1.Controls.Add(MakeLabel("保存天数"));
        _numSaveDays = MakeNum(32, 1, 30, 1);
        filterRow1.Controls.Add(_numSaveDays);

        // 重置按钮
        _btnResetHistory = new ModernButton { Text = "重置天数", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(72, 26), CornerRadius = 2, Margin = new Padding(4, 4, 6, 0), Font = Fonts.Ui9 };
        UiMetrics.FitWidth(_btnResetHistory);
        _btnResetHistory.Click += (s, e) =>
        {
            SearchHistory.Clear();
            ShowToast($"已重置搜索历史 · 已清除记录");
        };
        filterRow1.Controls.Add(_btnResetHistory);

        // 加速代理下拉框 (同一行)
        _cmbProxy = new ComboBox
        {
            Width = 120, DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.PaperDeep, ForeColor = Theme.Ink,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 5, 4, 0),
            Font = Fonts.Mono85
        };
        _cmbProxy.Items.Add("自动代理");
        _cmbProxy.Items.Add("直连(无代理)");
        _cmbProxy.SelectedIndex = 0;
        _cmbProxy.SelectedIndexChanged += (s, e) => OnProxyChanged();
        filterRow1.Controls.Add(_cmbProxy);

        // 测速按钮 (同一行)
        _btnTestProxy = new ModernButton
        {
            Text = "📡", Ghost = true, BackColor = Theme.PaperHi,
            Size = new Size(32, 26), CornerRadius = 2, Margin = new Padding(0, 4, 0, 0),
            Font = Fonts.Ui9
        };
        _btnTestProxy.Click += async (s, e) => await TestProxiesAsync();
        filterRow1.Controls.Add(_btnTestProxy);

        // 节点测速按钮 (详情日志左侧)
        _btnSpeedTest = new ModernButton { Text = "⚡ 节点测速", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(100, 26), CornerRadius = 2, Margin = new Padding(6, 4, 6, 0) };
        UiMetrics.FitWidth(_btnSpeedTest);
        _btnSpeedTest.Click += (s, e) => ToggleSpeedTest();
        filterRow1.Controls.Add(_btnSpeedTest);

        // 详情日志按钮 (第二行结尾)
        _btnInspector = new ModernButton { Text = "▤ 详情日志", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(94, 26), CornerRadius = 2, Margin = new Padding(0, 4, 0, 0) };
        UiMetrics.FitWidth(_btnInspector);
        _btnInspector.Click += (s, e) => ToggleInspector();
        filterRow1.Controls.Add(_btnInspector);

        header.Controls.Add(filterRow1);
        header.Controls.Add(toolbar);
        header.Controls.Add(inkRule);
        header.Controls.Add(brandRow);
        _root.Controls.Add(header, 0, 0);
    }

    private void BuildContent()
    {
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper, Margin = new Padding(0) };

        _dgv = new BufferedGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Paper,
            ForeColor = Theme.Ink,
            GridColor = Theme.Hairline,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.Single,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false,
            RowHeadersVisible = false,
            AllowUserToResizeRows = false,
            MultiSelect = true,
        };
        _dgv.ColumnHeadersDefaultCellStyle.BackColor = Theme.PaperDeep;
        _dgv.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Ink;
        _dgv.ColumnHeadersDefaultCellStyle.Font = Fonts.Ui9Bold;
        _dgv.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.PaperDeep;
        _dgv.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;  // 禁止列标题换行
        _dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _dgv.ColumnHeadersHeight = 34;
        _dgv.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _dgv.ScrollBars = ScrollBars.Both;  // 始终显示滚动条, 避免布局跳动
        _dgv.DefaultCellStyle.BackColor = Theme.GridRow;
        _dgv.DefaultCellStyle.ForeColor = Theme.Ink;
        _dgv.DefaultCellStyle.SelectionBackColor = Theme.GridSel;
        _dgv.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        _dgv.DefaultCellStyle.Font = Fonts.Ui9;
        _dgv.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        _dgv.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridRowAlt;
        _dgv.RowTemplate.Height = 30;
        _dgv.CellFormatting += Dgv_CellFormatting;
        _dgv.CellPainting += Dgv_CellPainting;
        _dgv.CellMouseClick += Dgv_CellMouseClick;
        _dgv.CellDoubleClick += Dgv_CellDoubleClick;
        _dgv.ColumnHeaderMouseClick += Dgv_ColumnHeaderMouseClick;
        _dgv.SelectionChanged += (s, e) => { UpdateDetails(); UpdateSelectedCount(); };

        SetupGridColumns();
        BuildContextMenu();
        _content.Controls.Add(_dgv);

        // 节点测速页 (与表格共用 _content，按视图切换可见性)
        _speedPanel = new SpeedTestPanel { Dock = DockStyle.Fill, Visible = false };
        _speedPanel.GetSubSources = () => CollectSubSources();
        // 内核下载/查询复用主窗体的最快镜像，避免各走各的
        _speedPanel.KernelProxy = url =>
        {
            var p = _github.CurrentProxy;
            return p == null ? url : p.Prefix + url;
        };
        _speedPanel.OnRoundApplied += _ => OnSpeedRoundApplied();
        WireSpeedPanel();
        _content.Controls.Add(_speedPanel);

        // 空状态
        _emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var emptyStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        _emptyHead = new Label
        {
            Text = "NO DATA — 卷宗为空",
            Font = Fonts.SerifTitle, ForeColor = Theme.Ink,
            AutoSize = false, Width = 400, Height = 30, TextAlign = ContentAlignment.MiddleCenter
        };
        _emptySub = new Label
        {
            Font = Fonts.Ui10, ForeColor = Theme.InkMid,
            AutoSize = false, Width = 420, Height = 130, TextAlign = ContentAlignment.MiddleCenter
        };
        UpdateEmptyText();
        emptyStack.Controls.Add(_emptyHead);
        emptyStack.Controls.Add(_emptySub);
        _emptyPanel.Controls.Add(emptyStack);
        _emptyPanel.Resize += (s, e) =>
        {
            emptyStack.Location = new Point(
                Math.Max(0, (_emptyPanel.Width - emptyStack.Width) / 2),
                Math.Max(20, _emptyPanel.Height / 2 - 80));
        };
        _content.Controls.Add(_emptyPanel);
        _emptyPanel.BringToFront();

        // 检查器: 宽度动画实现表格变窄效果
        _inspectorHost = new Panel
        {
            Dock = DockStyle.Right,
            Width = 0,  // 初始收起
            BackColor = Theme.PaperHi,
            Margin = new Padding(0),
            Visible = false
        };

        _inspector = new Panel { Dock = DockStyle.Left, Width = InspectorW, BackColor = Theme.PaperHi };
        _inspector.Paint += (s, e) =>
        {
            using var rule = new Pen(Theme.Rule, 2f);
            e.Graphics.DrawLine(rule, 1, 0, 1, _inspector.Height);
        };

        var tabHeader = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.PaperHi, Padding = new Padding(12, 4, 12, 0) };
        var tabFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, WrapContents = false };
        _btnTabDetails = new ModernButton { Text = "详情", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(72, 26), CornerRadius = 0, Margin = new Padding(0, 2, 6, 0) };
        UiMetrics.FitWidth(_btnTabDetails);
        _btnTabLog = new ModernButton { Text = "日志", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(72, 26), CornerRadius = 0, Margin = new Padding(0, 2, 6, 0) };
        UiMetrics.FitWidth(_btnTabLog);
        _btnTabDetails.Click += (s, e) => SetInspectorTab("details");
        _btnTabLog.Click += (s, e) => SetInspectorTab("log");
        _btnTabSpeed = new ModernButton { Text = "测速", Ghost = true, BackColor = Theme.PaperHi, Size = new Size(72, 26), CornerRadius = 0, Margin = new Padding(0, 2, 0, 0), Visible = false };
        UiMetrics.FitWidth(_btnTabSpeed);
        _btnTabSpeed.Click += (s, e) => SetInspectorTab("speed");
        tabFlow.Controls.Add(_btnTabDetails);
        tabFlow.Controls.Add(_btnTabLog);
        tabFlow.Controls.Add(_btnTabSpeed);
        tabHeader.Controls.Add(tabFlow);
        Common.StyleTab(_btnTabDetails, true);
        Common.StyleTab(_btnTabLog, false);
        Common.StyleTab(_btnTabSpeed, false);

        _detailsPanel = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Theme.PaperHi,
            AutoScroll = true, Padding = new Padding(16, 10, 14, 12)
        };

        // 日志工具栏
        var logToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 30, BackColor = Theme.PaperDeep,
            WrapContents = false, Padding = new Padding(4, 2, 4, 0)
        };
        var lblLogSave = new Label
        {
            Text = "日志保存", AutoSize = true, ForeColor = Theme.InkMid,
            Font = Fonts.Mono85, BackColor = Color.Transparent, Margin = new Padding(0, 3, 2, 0)
        };
        _numLogSaveDays = new NumericUpDown
        {
            Width = 32, Minimum = 1, Maximum = 30, Value = 1,
            BackColor = Theme.PaperDeep, ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 1, 2, 0),
            Font = Fonts.Mono85, AutoSize = false, Height = 24
        };
        _numLogSaveDays.ValueChanged += (s, e) => LogStore.SaveDays = (int)_numLogSaveDays.Value;
        var lblLogDay = new Label
        {
            Text = "天", AutoSize = true, ForeColor = Theme.InkMid,
            Font = Fonts.Mono85, BackColor = Color.Transparent, Margin = new Padding(0, 3, 8, 0)
        };
        var btnClearLog = new ModernButton
        {
            Text = "删除日志", Ghost = true, BackColor = Theme.PaperDeep,
            Size = new Size(64, 22), CornerRadius = 2, Margin = new Padding(0, 1, 0, 0), Font = Fonts.Ui9
        };
        btnClearLog.Click += (s, e) =>
        {
            _txtLog.Clear();
            LogStore.Clear();
            ShowToast("已清空日志");
        };
        logToolbar.Controls.Add(lblLogSave);
        logToolbar.Controls.Add(_numLogSaveDays);
        logToolbar.Controls.Add(lblLogDay);
        logToolbar.Controls.Add(btnClearLog);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.PaperDeep,
            ForeColor = Theme.LogGreen,
            Font = Fonts.Mono85,
            BorderStyle = BorderStyle.None
        };
        _logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PaperDeep, Padding = new Padding(10), Visible = false };
        _logPanel.Controls.Add(_txtLog);
        _logPanel.Controls.Add(logToolbar);

        _speedResultPanel = new SpeedResultPanel { Dock = DockStyle.Fill, Visible = false };
        _inspector.Controls.Add(_detailsPanel);
        _inspector.Controls.Add(_logPanel);
        _inspector.Controls.Add(_speedResultPanel);
        _inspector.Controls.Add(tabHeader);
        _inspectorHost.Controls.Add(_inspector);
        _content.Controls.Add(_inspectorHost);

        _root.Controls.Add(_content, 0, 1);
    }

    private void UpdateEmptyText()
    {
        if (IsFavoritesView)
        {
            _emptyHead.Text = "FAVORITES — 收藏夹为空";
            _emptySub.Text = "\n在搜索结果中右键仓库\n选择「★ 加入收藏」\n\n收藏的仓库不会再出现在搜索结果中";
        }
        else
        {
            _emptyHead.Text = "NO DATA — 卷宗为空";
            _emptySub.Text = "\n① 粘贴 GitHub Token（可选，提速约 10 倍）\n② 点击「🔍 搜索并分析」(F5)\n③ 打开详情面板挑选链接\n\n低星 · 高频更新 · 自动维护的仓库排在前面";
        }
    }

    /// <summary>更新状态栏选中计数</summary>
    private void UpdateSelectedCount()
    {
        // 状态提示通过 Toast 显示 (选中多个时)
    }

    // ══════════════════════ 加速代理 ══════════════════════

    private void OnProxyChanged()
    {
        if (_cmbProxy.SelectedIndex < 0) return;
        var selectedText = _cmbProxy.SelectedItem?.ToString() ?? "";

        // 自动代理模式
        if (selectedText == "自动代理")
        {
            _github.CurrentProxy = null;
            Log($"加速代理切换: 自动代理");
            _ = AutoDetectProxyAsync();
            return;
        }

        // 下拉框显示的是 "名称 (延迟)" 格式, 解析出代理名称
        var name = selectedText.Split(" (")[0];
        var proxy = GitHubService.AllProxies.FirstOrDefault(p => p.Name == name);
        if (proxy != null)
        {
            _github.CurrentProxy = proxy.IsDefault ? null : proxy;
            Log($"加速代理切换: {proxy.Name}");
        }
    }

    /// <summary>
    /// 自动探测最快代理并启用
    /// </summary>
    private async Task AutoDetectProxyAsync(CancellationToken ct = default)
    {
        try
        {
            ShowToast("正在自动探测加速代理...", persistent: true);
            var sorted = await GitHubService.TestAllProxiesAsync(ct);

            var direct = sorted.FirstOrDefault(p => p.IsDefault);
            var bestProxy = sorted.FirstOrDefault(p => !p.IsDefault && p.LatencyMs > 0);
            var anyAvailable = sorted.Any(p => p.LatencyMs > 0);

            // 输出完整测速结果到日志
            Log("自动代理 · 测速结果:");
            foreach (var p in sorted)
            {
                string status = p.LatencyMs switch
                {
                    > 0 => $"{p.LatencyMs}ms",
                    -2 => "超时",
                    _ => "未测"
                };
                Log($"  {(p.IsDefault ? "直连" : p.Name),-22} {status}");
            }

            // 情况1: 直连正常且最快
            if (direct != null && direct.LatencyMs > 0 &&
                (bestProxy == null || direct.LatencyMs <= bestProxy.LatencyMs))
            {
                _github.CurrentProxy = null;
                Log($"自动代理: 直连最快 ({direct.LatencyMs}ms), 使用直连");
                ShowToast($"自动代理: 直连 ({direct.LatencyMs}ms)");
            }
            // 情况2: 有可用代理, 且(直连超时 或 代理比直连快)
            else if (bestProxy != null &&
                     (direct == null || direct.LatencyMs < 0 || bestProxy.LatencyMs < direct.LatencyMs))
            {
                _github.CurrentProxy = bestProxy;
                Log($"自动代理: {bestProxy.Name} 最快 ({bestProxy.LatencyMs}ms), 已启用");
                ShowToast($"自动代理: {bestProxy.Name} ({bestProxy.LatencyMs}ms)");
            }
            // 情况3: 直连超时且无可用代理
            else if (!anyAvailable)
            {
                _github.CurrentProxy = null;
                Log("自动代理: 直连和所有代理均超时, 网络可能异常");
                ShowToast("自动代理: 网络异常, 建议检查网络或代理");
            }
            // 情况4: 直连正常但代理也正常且代理不快于直连 (兜底)
            else
            {
                _github.CurrentProxy = null;
                Log($"自动代理: 直连最快 ({direct?.LatencyMs}ms), 使用直连");
                ShowToast($"自动代理: 直连 ({direct?.LatencyMs}ms)");
            }
        }
        catch (OperationCanceledException)
        {
            Log("自动代理探测已取消");
        }
        catch (Exception ex)
        {
            Log($"自动代理探测失败: {ex.Message}");
            ShowToast("自动代理探测失败");
        }
    }

    private async Task TestProxiesAsync()
    {
        _btnTestProxy.Enabled = false;
        _btnTestProxy.Text = "⏳ 测速中...";
        ShowToast("正在测试所有加速代理延迟...", persistent: true);

        try
        {
            var sorted = await GitHubService.TestAllProxiesAsync();

            // 重建下拉框 (按延迟排序)
            _cmbProxy.Items.Clear();
            foreach (var proxy in sorted)
            {
                string latency = proxy.LatencyMs switch
                {
                    >= 0 => $"{proxy.LatencyMs}ms",
                    -2 => "超时",
                    _ => "未测"
                };
                _cmbProxy.Items.Add($"{proxy.Name} ({latency})");
            }

            // 默认选中延迟最低的可用代理 (如果有比直连快的)
            var best = sorted.FirstOrDefault(p => p.LatencyMs > 0);
            var direct = sorted.FirstOrDefault(p => p.IsDefault);
            if (best != null && direct != null && best.LatencyMs < direct.LatencyMs)
            {
                var bestIdx = sorted.IndexOf(best);
                _cmbProxy.SelectedIndex = bestIdx;
                ShowToast($"已自动选择最快代理: {best.Name} ({best.LatencyMs}ms)");
            }
            else
            {
                _cmbProxy.SelectedIndex = sorted.IndexOf(direct!);
                ShowToast($"直连最快 ({direct?.LatencyMs}ms), 保持直连");
            }

            // 更新 GitHubService 的代理映射
            // 注意: 下拉框索引与 sorted 顺序一致
            Log($"\n代理测速结果 (按延迟排序):");
            foreach (var p in sorted)
            {
                string status = p.LatencyMs switch
                {
                    >= 0 => $"{p.LatencyMs}ms",
                    -2 => "超时/失败",
                    _ => "未测"
                };
                Log($"  {p.Name,-25} {p.Type,-4} {status}");
            }
            Log("");
        }
        catch (Exception ex)
        {
            Log($"测速失败: {ex.Message}");
            ShowToast("测速失败");
        }

        _btnTestProxy.Enabled = true;
        _btnTestProxy.Text = "📡 测速";
    }

    private void BuildProgressLine()
    {
        _progressLine = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Paper, Margin = new Padding(0, 3, 0, 0) };
        _progressLine.Paint += (s, e) =>
        {
            var g = e.Graphics;
            using (var pen = new Pen(Theme.Hairline, 1f))
                g.DrawLine(pen, 0, 0, _progressLine.Width, 0);
            if (_progressPct > 0)
            {
                using var bar = new SolidBrush(Theme.Stamp);
                g.FillRectangle(bar, 0, 0, (int)(_progressLine.Width * _progressPct / 100.0), 4);
            }
        };
        _root.Controls.Add(_progressLine, 0, 2);
    }

    private void BuildToast()
    {
        _toast = new Panel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.PaperHi, Visible = false
        };
        var flow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.PaperHi, WrapContents = false,
            Padding = new Padding(16, 9, 16, 9)
        };
        var dot = new Label { Text = "●", ForeColor = Theme.Stamp, AutoSize = true, Font = Fonts.Ui9, Margin = new Padding(0, 1, 8, 0) };
        _toastLabel = new Label { AutoSize = true, ForeColor = Theme.Ink, Font = Fonts.Ui9, Margin = new Padding(0) };
        flow.Controls.Add(dot);
        flow.Controls.Add(_toastLabel);
        _toast.Controls.Add(flow);
        _toast.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Hairline, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, _toast.Width - 1, _toast.Height - 1);
        };
        Controls.Add(_toast);
    }

    private void CenterToastX()
    {
        _toast.Location = new Point(
            Math.Max(8, (ClientSize.Width - _toast.Width) / 2),
            _toast.Location.Y);
    }

    private void ShowToast(string msg, bool persistent = false)
    {
        if (IsDisposed) return;

        // 节流: 文本和状态都相同时跳过, 避免重复重绘
        if (_toast.Visible && msg == _lastToastMsg && persistent == _toastPersistent)
            return;

        _lastToastMsg = msg;
        _toastPersistent = persistent;
        _toastLabel.Text = msg;
        _toast.PerformLayout();
        bool alreadyShown = _toast.Visible && _toast.Location.Y < ClientSize.Height;
        _toast.Visible = true;
        _toast.BringToFront();
        CenterToastX();

        // 重新调度自动隐藏 (修复: 之前已可见时提前 return, 导致常驻不消失)
        _toastDelay?.Stop();
        _toastDelay?.Dispose();
        _toastDelay = null;
        if (!persistent)
        {
            _toastDelay = new System.Windows.Forms.Timer { Interval = 2800 };
            _toastDelay.Tick += (s, e) => { _toastDelay!.Stop(); _toastDelay.Dispose(); _toastDelay = null; HideToast(); };
            _toastDelay.Start();
        }
        if (alreadyShown) return;

        int targetY = ClientSize.Height - _toast.Height - 18;
        int y = ClientSize.Height + 4;
        _toast.Location = new Point(_toast.Location.X, y);

        _toastAnim?.Stop();
        _toastAnim?.Dispose();
        _toastAnim = new System.Windows.Forms.Timer { Interval = 12 };
        _toastAnim.Tick += (s, e) =>
        {
            y -= 4;
            if (y <= targetY) { y = targetY; _toastAnim!.Stop(); _toastAnim.Dispose(); _toastAnim = null; }
            _toast.Location = new Point(_toast.Location.X, y);
        };
        _toastAnim.Start();
    }

    private void HideToast()
    {
        _toastPersistent = false;
        int x = _toast.Location.X;
        int y = _toast.Location.Y;
        int hideY = ClientSize.Height + 4;
        _toastAnim?.Stop();
        _toastAnim?.Dispose();
        _toastAnim = new System.Windows.Forms.Timer { Interval = 12 };
        _toastAnim.Tick += (s, e) =>
        {
            y += 5;
            if (y >= hideY) { _toast.Visible = false; _toastAnim!.Stop(); _toastAnim.Dispose(); _toastAnim = null; }
            else _toast.Location = new Point(x, y);
        };
        _toastAnim.Start();
    }

    // ── 收藏视图切换 ──
    private void ToggleViewMode()
    {
        _viewMode = IsFavoritesView ? "search" : "favorites";
        _btnFavorites.Text = IsFavoritesView ? "❌ 关闭收藏" : "★ 收藏";
        _tooltip.SetToolTip(_btnFavorites, IsFavoritesView ? "返回搜索结果 (Ctrl+D)" : "查看收藏夹 (Ctrl+D)\n右键仓库 → ★ 加入收藏\n收藏的仓库不再被搜索收录");
        Common.StyleTab(_btnFavorites, IsFavoritesView);
        UpdateEmptyText();
        ApplyFilters();
        ShowToast(IsFavoritesView
            ? $"收藏夹 · {_favorites.Count} 个仓库"
            : $"搜索结果 · {_filteredRepos.Count} 个仓库");
    }

    // ── 节点测速视图切换 ──
    private void ToggleSpeedTest()
    {
        _speedView = !_speedView;

        if (_speedView)
        {
            // 退出其他视图态，避免收藏/测速同时生效
            if (IsFavoritesView)
            {
                _viewMode = "search";
                _btnFavorites.Text = "★ 收藏";
                Common.StyleTab(_btnFavorites, false);
                _tooltip.SetToolTip(_btnFavorites, "查看收藏夹 (Ctrl+D)\n右键仓库 → ★ 加入收藏\n收藏的仓库不再被搜索收录");
            }
            _dgv.Visible = false;
            _emptyPanel.Visible = false;
            _speedPanel.Visible = true;
            _speedPanel.BringToFront();
            Common.StyleTab(_btnSpeedTest, true);

            // 结果与日志都在右侧详情面板，进测速视图就把它展开并对到「测速」页
            _btnTabSpeed.Visible = true;
            if (!_inspectorOpen) ToggleInspector();
            SetInspectorTab("speed");
            _speedResultPanel.SetNodes(_speedPanel.Nodes);
            _speedResultPanel.Backfill(_speedPanel.LogTailText);

            ShowToast("节点测速 · 选择订阅来源后开始");
        }
        else
        {
            _speedPanel.Visible = false;
            _dgv.Visible = true;
            Common.StyleTab(_btnSpeedTest, false);
            _btnTabSpeed.Visible = false;
            if (_inspectorTab == "speed") SetInspectorTab("details");
            UpdateEmptyText();
            ApplyFilters();
        }
    }

    // ── 测速页事件：结果与日志统一送到右侧详情面板 ──
    private void WireSpeedPanel()
    {
        _speedPanel.OnNodesChanged += () =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(() => _speedResultPanel.SetNodes(_speedPanel.Nodes));
            }
            catch { }
        };

        _speedPanel.OnLogLine += (line, color) =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(() => _speedResultPanel.AppendLog(line, color));
            }
            catch { }
        };
    }


    /// <summary>测速页的「搜索结果」来源：当前表格里所有已验证的订阅链接。</summary>
    private List<SubSource> CollectSubSources()
    {
        var list = new List<SubSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _filteredRepos.Count > 0 ? _filteredRepos : _allRepos)
        {
            foreach (var l in r.Links)
            {
                if (!l.IsValid || string.IsNullOrEmpty(l.Url)) continue;
                if (!seen.Add(l.Url)) continue;
                list.Add(new SubSource { Url = l.Url, Repo = r.FullName });
            }
        }
        return list;
    }

    /// <summary>收藏来源。</summary>
    private List<SubSource> CollectFavoriteSubSources()
    {
        var list = new List<SubSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _favorites)
        {
            foreach (var l in r.Links)
            {
                if (!l.IsValid || string.IsNullOrEmpty(l.Url)) continue;
                if (!seen.Add(l.Url)) continue;
                list.Add(new SubSource { Url = l.Url, Repo = r.FullName });
            }
        }
        return list;
    }

    /// <summary>把来源列表摊平成带备注的 URL —— 备注让内核把来源写进节点名，
    /// 测速结果才能映射回仓库。</summary>
    /// <summary>测速页完成一轮后回调：刷新排序与表格，提示降权情况。</summary>
    private void OnSpeedRoundApplied()
    {
        try
        {
            BeginInvoke(() =>
            {
                ApplyFilters();
                var demoted = SpeedTestStore.All.Count(s => s.Demoted);
                if (demoted > 0)
                    ShowToast($"已更新可用分 · {demoted} 个仓库连续 {SpeedTestStore.DemoteAfterRounds} 轮零通过已降权");
                else
                    ShowToast("已更新可用分，按「可用分」排序查看");
            });
        }
        catch { }
    }

    /// <summary>解除选中仓库的降权。</summary>
    private void UndemoteSelected()
    {
        var sel = _dgv.CurrentRow?.Cells["Repo"]?.Value?.ToString();
        if (string.IsNullOrEmpty(sel)) return;
        SpeedTestStore.Undemote(sel);
        ApplyFilters();
        ShowToast($"{sel} 已解除降权");
    }

    // ── 检查器开合 (无动画, 直接显示/隐藏) ──
    private void ToggleInspector()
    {
        if (_inspectorOpen)
        {
            _inspectorOpen = false;
            _btnInspector.Ghost = true;
            _btnInspector.Invalidate();
            _inspectorHost.Visible = false;
            _inspectorHost.Width = 0;
        }
        else
        {
            _inspectorOpen = true;
            _btnInspector.Ghost = false;
            _btnInspector.BaseColor = Theme.Stamp;
            _btnInspector.ForeColor = Theme.Paper;
            _btnInspector.Invalidate();
            if (_inspectorTab == "details") UpdateDetails();
            _inspectorHost.Width = InspectorW;
            _inspectorHost.Visible = true;
        }
    }

    private void SetInspectorTab(string which)
    {
        _inspectorTab = which;
        _detailsPanel.Visible = which == "details";
        _logPanel.Visible = which == "log";
        _speedResultPanel.Visible = which == "speed";
        Common.StyleTab(_btnTabDetails, which == "details");
        Common.StyleTab(_btnTabLog, which == "log");
        Common.StyleTab(_btnTabSpeed, which == "speed");
        if (which == "details") UpdateDetails();
    }

    /// <summary>
    /// 自动检查并安装内核新版本。仅在用户开启了「自动更新并安装」时触发，
    /// 全程后台静默进行 —— 失败不影响任何现有功能。
    /// </summary>
    private async Task CheckKernelUpdateAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            if (!settings.KernelAutoUpdate) return;

            var local = SubCheckKernel.LocalVersion();
            var rel = await SubCheckKernel.FetchLatestAsync(
                url => _github.CurrentProxy?.Prefix + url ?? url);

            if (rel == null) return;                       // 网络不通，静默跳过
            if (!SubCheckKernel.IsNewer(rel.Version, local)) return;

            BeginInvoke(() =>
            {
                try
                {
                    ShowToast($"发现新内核 {rel.Version}，正在后台更新…");
                    Log($"内核自动更新: {local ?? "未安装"} → {rel.Version}");
                }
                catch { }
            });

            var ok = await SubCheckKernel.InstallAsync(rel,
                p => { }, CancellationToken.None,
                url => _github.CurrentProxy?.Prefix + url ?? url);

            BeginInvoke(() =>
            {
                try
                {
                    if (ok)
                    {
                        SubCheckKernel.RemoveBackup();
                        ShowToast($"内核已更新至 {rel.Version}");
                        Log($"内核 {rel.Version} 安装完成（SHA256 已校验）");
                    }
                    else
                    {
                        Log("内核自动更新失败，可手动从「⚙ 内核状态」更新");
                    }
                }
                catch { }
            });
        }
        catch { /* 自动更新绝不影响主流程 */ }
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        Win32Interop.EnableDarkTitle(this, false);
        Win32Interop.EnableRoundedCorners(this);
        for (double o = 0.0; o <= 1.0; o += 0.12)
        {
            Opacity = o;
            await Task.Delay(14);
        }
        Opacity = 1;

        // 界面就绪后再查内核，避免拖慢首屏
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            try { BeginInvoke(new Action(() => _ = CheckKernelUpdateAsync())); } catch { }
        }, TaskScheduler.Default);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyData == (Keys.Control | Keys.F))
        {
            _txtFilter.Focus();
            _txtFilter.SelectAll();
            e.Handled = true;
        }
        else if (e.KeyData == (Keys.Control | Keys.C))
        {
            CopySelectedSubLinks();
            e.Handled = true;
        }
        else if (e.KeyData == (Keys.Control | Keys.L))
        {
            ToggleInspector();
            e.Handled = true;
        }
        else if (e.KeyData == (Keys.Control | Keys.D))
        {
            ToggleViewMode();
            e.Handled = true;
        }
        else if (e.KeyData == Keys.F5 && !_isRunning)
        {
            OnSearchClick();
            e.Handled = true;
        }
    }

    private Label MakeLabel(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.InkMid,
        Margin = new Padding(0, 7, 2, 0), BackColor = Color.Transparent, Font = Fonts.Mono85
    };

    private NumericUpDown MakeNum(int width, int min, int max, int value) => new()
    {
        Width = width, Minimum = min, Maximum = max, Value = value,
        BackColor = Theme.PaperDeep, ForeColor = Theme.Ink,
        BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 4, 6, 0),
        Font = Fonts.Mono85, AutoSize = false, Height = 26
    };

    private Panel MakeField(TextBox tb, int width)
    {
        var p = new Panel
        {
            Width = width, Height = 26,
            BackColor = Theme.PaperDeep,
            Margin = new Padding(0, 4, 6, 0),
            Padding = new Padding(6, 3, 6, 3),
            Cursor = Cursors.IBeam
        };
        tb.Dock = DockStyle.Fill;
        p.Controls.Add(tb);
        p.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Hairline, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        p.Click += (s, e) => tb.Focus();
        return p;
    }

    private void SetupGridColumns()
    {
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Repo", HeaderText = "仓库", FillWeight = 26 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stars", HeaderText = "Star", FillWeight = 6 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastPush", HeaderText = "最后更新", FillWeight = 10 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", FillWeight = 10 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Commits", HeaderText = "7天提交", FillWeight = 9 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Age", HeaderText = "库龄", FillWeight = 6 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Processing", HeaderText = "方式", FillWeight = 8 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Score", HeaderText = "活跃度", FillWeight = 8 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Nodes", HeaderText = "节点", FillWeight = 7 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Links", HeaderText = "链接", FillWeight = 8 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "Usable", HeaderText = "可用分", FillWeight = 8 });
        _dgv.Columns.Add(new DataGridViewTextBoxColumn { Name = "AvgSpeed", HeaderText = "均速", FillWeight = 9 });

        _dgv.Columns["Repo"]!.DefaultCellStyle.Font = Fonts.Mono9;
        _dgv.Columns["LastPush"]!.DefaultCellStyle.Font = Fonts.Mono9;
        foreach (var col in new[] { "Stars", "Commits", "Age", "Score", "Nodes", "Links", "Usable", "AvgSpeed" })
            _dgv.Columns[col]!.DefaultCellStyle.Font = Fonts.Mono9;
    }

    private void SetupTooltips()
    {
        _tooltip = new ToolTip
        {
            InitialDelay = 300,
            AutoPopDelay = 15000,
            ReshowDelay = 200,
            ShowAlways = true,
            AutomaticDelay = 5000,
            IsBalloon = false,
            ToolTipTitle = "功能说明",
            ToolTipIcon = ToolTipIcon.Info
        };

        _tooltip.SetToolTip(_btnSearch,
            "搜索并分析 GitHub 免费节点仓库\n\n" +
            "功能说明:\n" +
            "· 使用 8 个关键词搜索 GitHub\n" +
            "· 逐个分析仓库活跃度\n" +
            "· 探测订阅链接和节点数量\n" +
            "· 按活跃度评分排序\n\n" +
            "快捷键: F5 开始/取消");

        _tooltip.SetToolTip(_btnExport,
            "打开订阅链接导出面板\n\n" +
            "功能说明:\n" +
            "· 查看所有订阅链接\n" +
            "· 复制全部链接到剪贴板\n" +
            "· 合并为单一 Base64 订阅\n" +
            "· 导出为文件保存\n\n" +
            "导出格式:\n" +
            "· Base64 (V2RayN/v2rayNG/Shadowrocket)\n" +
            "· Clash YAML\n" +
            "· Sing-box JSON");

        _tooltip.SetToolTip(_btnFavorites,
            "切换收藏夹视图\n\n" +
            "功能说明:\n" +
            "· 查看已收藏的仓库\n" +
            "· 右键仓库 → ★ 加入收藏\n" +
            "· 收藏的仓库不再被搜索收录\n" +
            "· 收藏夹数据持久保存\n\n" +
            "快捷键: Ctrl+D");

        _tooltip.SetToolTip(_txtFilter,
            "即时筛选仓库列表\n\n" +
            "功能说明:\n" +
            "· 按仓库名筛选\n" +
            "· 按描述内容筛选\n" +
            "· 实时更新结果\n\n" +
            "快捷键: Ctrl+F");

        _tooltip.SetToolTip(_numRepos,
            "设置搜索目标仓库数量\n\n" +
            "功能说明:\n" +
            "· 范围: 10 - 100\n" +
            "· 默认: 30\n" +
            "· 数量越多, 搜索时间越长\n\n" +
            "建议: 30-50 为最佳平衡");

        _tooltip.SetToolTip(_numMinCommits,
            "设置日均提交次数下限\n\n" +
            "功能说明:\n" +
            "· 只保留日均提交 ≥ 此值的仓库\n" +
            "· 0 = 不限制\n" +
            "· 过滤低活跃度仓库\n\n" +
            "建议: 1-3 过滤大部分无效仓库");

        _tooltip.SetToolTip(_numActiveDays,
            "设置仓库最低库龄\n\n" +
            "功能说明:\n" +
            "· 只保留创建 ≥ 此天数的仓库\n" +
            "· 0 = 不限制\n" +
            "· 过滤新建的测试仓库\n\n" +
            "建议: 7-30 天");

        _tooltip.SetToolTip(_numInactiveDays,
            "设置不活跃天数上限\n\n" +
            "功能说明:\n" +
            "· 连续超过此天数未更新则标记为不活跃\n" +
            "· 开启「清理」后自动移出名单\n" +
            "· 范围: 3 - 30 天\n\n" +
            "建议: 7-14 天");

        _tooltip.SetToolTip(_chkAutoClean,
            "自动清理不活跃仓库\n\n" +
            "功能说明:\n" +
            "· 开启后自动隐藏不活跃仓库\n" +
            "· 基于「不活跃>」设置的天数\n" +
            "· 关闭后可查看所有仓库\n\n" +
            "提示: 取消勾选可查看被过滤的仓库");

        _tooltip.SetToolTip(_numSaveDays,
            "保存天数\n\n" +
            "功能说明:\n" +
            "· 搜索完成的仓库会被自动记录\n" +
            "· 保存期内再次搜索时自动跳过\n" +
            "· 避免重复看到已筛选过的仓库\n\n" +
            "范围: 1 - 30 天\n" +
            "默认: 1 天\n\n" +
            "提示: 点击「重置天数」可清除所有记录");

        _tooltip.SetToolTip(_btnResetHistory,
            "重置搜索历史\n\n" +
            "功能说明:\n" +
            "· 清除所有已保存的搜索记录\n" +
            "· 之前隐藏的仓库将重新显示");

        _tooltip.SetToolTip(_cmbQueryMode,
            "选择分析模式\n\n" +
            "🚀 极速模式:\n" +
            "· 并发搜索 + 最大并发探测\n" +
            "· 智能预过滤无效仓库\n" +
            "· 速度最快\n\n" +
            "⚖️ 标准模式:\n" +
            "· 并发搜索 + 适度并发\n" +
            "· 平衡速度与准确性\n\n" +
            "🔍 深度模式:\n" +
            "· 完整探测所有层\n" +
            "· 最大化发现节点\n\n" +
            "🐢 兼容模式:\n" +
            "· 串行处理 + 低并发\n" +
            "· 避免 API 限速");

        _tooltip.SetToolTip(_cmbProxy,
            "选择 GitHub 加速代理\n\n" +
            "功能说明:\n" +
            "· 解决 GitHub 访问慢的问题\n" +
            "· 支持 13 个加速镜像\n" +
            "· 点击「📡 测速」自动选择最快\n\n" +
            "建议: 网络慢时务必配置");

        _tooltip.SetToolTip(_btnTestProxy,
            "测试所有加速代理延迟\n\n" +
            "功能说明:\n" +
            "· 测试所有代理的响应时间\n" +
            "· 按延迟排序\n" +
            "· 自动选择最快的代理\n\n" +
            "提示: 首次使用请先测速");

        _tooltip.SetToolTip(_btnFeatureLib,
            "特征库管理\n\n" +
            "功能说明:\n" +
            "· 查看默认探测特征库\n" +
            "· 导入/导出自定义特征码\n" +
            "· 复制文档给 AI 生成规则\n\n" +
            "特征码用于识别仓库中的订阅文件");

        _tooltip.SetToolTip(_btnSpeedTest,
            "节点测速\n\n" +
            "功能说明:\n" +
            "· 对收藏/搜索到的订阅链接做真实穿透测速\n" +
            "· 存活检测 + 下载测速 + 流媒体解锁 + IP 风险\n" +
            "· 按回数保留历史节点，下轮自动并入\n" +
            "· 结果可直接导出为可用订阅\n\n" +
            "依赖 subs-check 内核，可从本页下载安装");

        _tooltip.SetToolTip(_btnInspector,
            "展开 / 收起右侧详情面板\n\n" +
            "功能说明:\n" +
            "· 查看选中仓库的详细信息\n" +
            "· 显示订阅链接和节点数量\n" +
            "· 查看分析日志\n\n" +
            "快捷键: Ctrl+L");

        _tooltip.SetToolTip(_txtToken,
            "GitHub Personal Access Token\n\n" +
            "功能说明:\n" +
            "· 提升 API 限额 60 → 5000 次/小时\n" +
            "· 减少被限速的概率\n" +
            "· 获取方式:\n" +
            "  github.com/settings/tokens\n\n" +
            "提示: 留空也可运行, 但速度受限");

        _tooltip.SetToolTip(_btnEye, "显示 / 隐藏 Token 明文");
        _tooltip.SetToolTip(_dgv,
            "仓库列表操作说明\n\n" +
            "鼠标操作:\n" +
            "· 单击行 = 查看详情\n" +
            "· 双击行 = 打开 GitHub 仓库\n" +
            "· 右键 = 操作菜单\n" +
            "· 点击列头 = 排序\n\n" +
            "键盘操作:\n" +
            "· Ctrl+C = 复制选中订阅链接\n" +
            "· Ctrl+L = 切换详情面板\n" +
            "· Ctrl+D = 切换收藏夹\n" +
            "· Ctrl+F = 聚焦筛选框");
    }

    // ── 右键菜单: 手动 Show + ItemClicked ──
    private void BuildContextMenu()
    {
        _ctxMenu = new ContextMenuStrip
        {
            BackColor = Theme.PaperHi, ForeColor = Theme.Ink,
            Font = Fonts.Ui9, ShowImageMargin = false, AutoClose = true,
            Renderer = new DossierMenuRenderer()
        };
        _ctxMenu.Items.Add(new ToolStripMenuItem("进入仓库 (GitHub)") { Tag = "open" });
        _ctxMenu.Items.Add(new ToolStripMenuItem("复制订阅链接") { Tag = "link" });
        _ctxMenu.Items.Add(new ToolStripMenuItem("★ 加入收藏") { Tag = "fav" });
        _ctxMenu.Items.Add(new ToolStripMenuItem("复制仓库名") { Tag = "name" });
        _ctxMenu.Items.Add(new ToolStripMenuItem("解除测速降权") { Tag = "undemote" });
        _ctxMenu.Items.Add(new ToolStripSeparator());
        _ctxMenu.Items.Add(new ToolStripMenuItem("重新检查选中的仓库") { Tag = "recheck" });
        _ctxMenu.Opening += (s, e) =>
        {
            var selectedCount = _dgv.SelectedRows.Count;
            // 根据选中数量动态调整菜单项文本
            var linkItem = _ctxMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag as string == "link");
            var nameItem = _ctxMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag as string == "name");
            if (linkItem != null) linkItem.Text = selectedCount > 1 ? $"复制选中的 {selectedCount} 个订阅链接" : "复制订阅链接";
            if (nameItem != null) nameItem.Text = selectedCount > 1 ? $"复制选中的 {selectedCount} 个仓库名" : "复制仓库名";

            // 收藏视图下，「加入收藏」变成「删除」，且支持多选删除
            var favItem = _ctxMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag as string == "fav");
            if (favItem != null)
            {
                if (IsFavoritesView)
                    favItem.Text = selectedCount > 1 ? $"删除选中的 {selectedCount} 个收藏" : "从收藏夹删除";
                else
                    favItem.Text = selectedCount > 1 ? $"加入收藏 ({selectedCount} 个)" : "★ 加入收藏";
            }

            // 「解除降权」仅在有仓库真被降权时可点，否则是死菜单项
            var undemote = _ctxMenu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Tag as string == "undemote");
            if (undemote != null)
            {
                var any = _dgv.SelectedRows
                    .Cast<DataGridViewRow>()
                    .Any(r => SpeedTestStore.Get(r.Cells["Repo"]?.Value?.ToString() ?? "")?.Demoted == true);
                undemote.Enabled = any;
                undemote.Text = selectedCount > 1 ? "解除选中仓库的降权" : "解除测速降权";
            }
        };
        _ctxMenu.ItemClicked += CtxMenu_ItemClicked;
    }

    private async void CtxMenu_ItemClicked(object? sender, ToolStripItemClickedEventArgs e)
    {
        switch (e.ClickedItem?.Tag as string)
        {
            case "open":
                OpenSelectedReposInBrowser();
                break;
            case "link":
                CopySelectedSubLinks();
                break;
            case "fav":
                if (IsFavoritesView) RemoveSelectedFromFavorites();
                else ToggleSelectedFavorites();
                break;
            case "name":
                CopySelectedRepoNames();
                break;
            case "undemote":
                UndemoteSelected();
                break;
            case "recheck":
                await RecheckSelectedRepos();
                break;
        }
    }

    private void ToggleFavorite()
    {
        if (_contextRepo == null) return;
        var existing = _favorites.FirstOrDefault(f => f.FullName == _contextRepo.FullName);
        if (existing != null)
        {
            _favorites.Remove(existing);
            FavoritesStore.Save(_favorites);
            ShowToast($"已取消收藏 {_contextRepo.FullName}");
        }
        else
        {
            _favorites.Add(_contextRepo);
            FavoritesStore.Save(_favorites);
            ShowToast($"★ 已收藏 {_contextRepo.FullName} · 工具栏「★ 收藏」或 Ctrl+D 查看");
        }
        if (IsFavoritesView) ApplyFilters();
    }

    private class DossierMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rc = new Rectangle(Point.Empty, e.Item.Size);
            if (e.Item.Selected)
            {
                using var b = new SolidBrush(Theme.Stamp);
                e.Graphics.FillRectangle(b, rc);
            }
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Theme.Paper : Theme.Ink;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Theme.Hairline, 1f);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 2, y, e.Item.Width - 2, y);
        }
    }

    // ══════════════════════ 设置 / 缓存 ══════════════════════

    private void ApplySettings()
    {
        _numRepos.Value = Clamp(_settings.RepoCount, 10, 100);
        _numMinCommits.Value = Clamp(_settings.MinCommitsPerDay, 0, 50);
        _numActiveDays.Value = Clamp(_settings.MinActiveDays, 0, 3650);
        _numInactiveDays.Value = Clamp(_settings.InactiveDays, 3, 30);
        _chkAutoClean.Checked = _settings.AutoClean;

        // key 只来自 exe 同目录的 settings.json。不做环境变量 GITHUB_TOKEN 回退：
        // 否则有该环境变量的机器上"初次打开"key 就不是空的了，而且关闭时会
        // 把环境变量里的真实 token 明文写进 settings.json —— 用户不知情且违背
        // "初次打开应为空"的要求。
        _txtToken.Text = _settings.GetToken();

        if (_settings.WindowW >= MinimumSize.Width && _settings.WindowH >= MinimumSize.Height)
        {
            if (_settings.Maximized)
            {
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                var bounds = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowW, _settings.WindowH);
                if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(bounds)))
                {
                    StartPosition = FormStartPosition.Manual;
                    DesktopBounds = bounds;
                }
            }
        }
    }

    private static decimal Clamp(decimal v, int min, int max) => Math.Min(Math.Max(v, min), max);

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _cts?.Cancel();
        _settings.RepoCount = (int)_numRepos.Value;
        _settings.MinCommitsPerDay = (int)_numMinCommits.Value;
        _settings.MinActiveDays = (int)_numActiveDays.Value;
        _settings.InactiveDays = (int)_numInactiveDays.Value;
        _settings.AutoClean = _chkAutoClean.Checked;
        _settings.InspectorOpen = _inspectorOpen;
        _settings.SetToken(_txtToken.Text.Trim());
        if (WindowState == FormWindowState.Maximized)
        {
            _settings.Maximized = true;
            var rb = RestoreBounds;
            _settings.WindowX = rb.X; _settings.WindowY = rb.Y;
            _settings.WindowW = rb.Width; _settings.WindowH = rb.Height;
        }
        else
        {
            _settings.Maximized = false;
            _settings.WindowX = Location.X; _settings.WindowY = Location.Y;
            _settings.WindowW = Width; _settings.WindowH = Height;
        }
        _settings.Save();
    }

    private void LoadCache()
    {
        var cached = ResultCache.Load();
        if (cached is { Repos.Count: > 0 })
        {
            _allRepos = cached.Repos;
            ApplyFilters();
            var mins = Math.Max(0, (int)(DateTime.Now - cached.SavedAt).TotalMinutes);
            ShowToast($"已加载缓存 · {cached.Repos.Count} 个仓库 · {mins} 分钟前 · F5 刷新");
            if (_settings.InspectorOpen)
            {
                _inspectorOpen = true;
                _inspectorHost.Width = InspectorW;
                _inspectorHost.Visible = true;
                _btnInspector.Ghost = false;
                _btnInspector.BaseColor = Theme.Stamp;
                _btnInspector.ForeColor = Theme.Paper;
                UpdateDetails();
            }
            else
            {
                _inspectorHost.Width = 0;
                _inspectorHost.Visible = false;
            }
            _btnExport.Enabled = true;
        }
    }

    // ══════════════════════ Grid 事件 ══════════════════════

    private void Dgv_CellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.RowIndex < _filteredRepos.Count)
        {
            // 多选支持: 如果右键点击已选中的行, 保持多选; 否则选中当前行
            if (!_dgv.Rows[e.RowIndex].Selected)
            {
                _dgv.ClearSelection();
                _dgv.Rows[e.RowIndex].Selected = true;
            }
            try { _dgv.CurrentCell = _dgv.Rows[e.RowIndex].Cells[0]; } catch { }
            _ctxMenu.Show(Cursor.Position);
        }
    }

    private void Dgv_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && e.RowIndex < _filteredRepos.Count)
        {
            _contextRepo = _filteredRepos[e.RowIndex];
            OpenRepoInBrowser();
        }
    }

    private void Dgv_ColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        var col = _dgv.Columns[e.ColumnIndex].Name;
        if (_sortField == col) _sortDesc = !_sortDesc;
        else { _sortField = col; _sortDesc = true; }
        ApplyFilters();
    }

    private void Dgv_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _filteredRepos.Count) return;
        var repo = _filteredRepos[e.RowIndex];
        switch (_dgv.Columns[e.ColumnIndex].Name)
        {
            case "Status":
                e.CellStyle!.ForeColor = repo.DaysInactive <= 0 ? Theme.Live :
                    repo.DaysInactive >= (int)_numInactiveDays.Value ? Theme.StampDeep : Theme.Ochre;
                e.Value = repo.DaysInactive <= 0 ? "● 活跃中" : $"{repo.DaysInactive}天未更新";
                e.FormattingApplied = true;
                break;
            case "Score":
                e.CellStyle!.ForeColor = ScoreColor(repo.Score);
                break;
            case "Processing":
                if (repo.ProcessingType == "待分析")
                    e.CellStyle!.ForeColor = Theme.Ochre;  // 待分析 = 橙色
                else
                    e.CellStyle!.ForeColor = repo.ProcessingType.Contains("自动") ? Theme.Stamp : Theme.InkMid;
                break;
            case "Nodes":
                e.CellStyle!.ForeColor = repo.TotalNodes > 0 ? Theme.Live : Theme.InkLow;
                break;
            case "Stars":
                e.CellStyle!.ForeColor = Theme.InkLow;
                break;
            case "LastPush":
                e.CellStyle!.ForeColor = Theme.InkMid;
                break;
        }
    }

    /// <summary>活跃度列自绘: 数字 + 信号条 (防御式, 异常回退默认绘制保证可见)</summary>
    private void Dgv_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_dgv.Columns[e.ColumnIndex].Name != "Score") return;
        if (e.RowIndex >= _filteredRepos.Count) return;
        try
        {
            var repo = _filteredRepos[e.RowIndex];
            var g = e.Graphics;
            var b = e.CellBounds;

            var style = e.CellStyle ?? _dgv.DefaultCellStyle;
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            var bgColor = selected ? style.SelectionBackColor : style.BackColor;
            if (bgColor == Color.Empty) bgColor = selected ? Theme.GridSel : Theme.GridRow;

            using (var bg = new SolidBrush(bgColor))
                g!.FillRectangle(bg, b);

            var scoreColor = ScoreColor(repo.Score);

            var textRect = new Rectangle(b.X, b.Y, b.Width, b.Height - 7);
            TextRenderer.DrawText(g, repo.Score.ToString(), Fonts.Mono9, textRect, scoreColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            const int barH = 3, pad = 7;
            int innerW = b.Width - pad * 2;
            if (innerW > 4)
            {
                int barY = b.Bottom - barH - 4;
                using (var track = new SolidBrush(Theme.Hairline))
                    g.FillRectangle(track, b.X + pad, barY, innerW, barH);
                int barW = Math.Max(2, (int)(innerW * repo.Score / 100.0));
                using (var bar = new SolidBrush(scoreColor))
                    g.FillRectangle(bar, b.X + pad, barY, barW, barH);
            }
            e.Handled = true;   // 完整绘制成功才接管
        }
        catch { /* 异常时走默认绘制, CellFormatting 已保证数字着色 */ }
    }

    private void OpenRepoInBrowser()
    {
        if (_contextRepo == null) return;
        try
        {
            ShowToast($"正在打开 {_contextRepo.FullName} …");
            Process.Start(new ProcessStartInfo { FileName = _contextRepo.Url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"打开浏览器失败: {ex.Message}");
            ShowToast("打开浏览器失败");
        }
    }

    // ══════════════════════ 多选操作 ══════════════════════

    private List<RepoInfo> GetSelectedRepos()
    {
        var repos = new List<RepoInfo>();
        foreach (DataGridViewRow row in _dgv.SelectedRows)
        {
            if (row.Index >= 0 && row.Index < _filteredRepos.Count)
                repos.Add(_filteredRepos[row.Index]);
        }
        return repos;
    }

    private void OpenSelectedReposInBrowser()
    {
        var repos = GetSelectedRepos();
        if (repos.Count == 0) { ShowToast("请先选中仓库"); return; }
        foreach (var repo in repos)
        {
            try { Process.Start(new ProcessStartInfo { FileName = repo.Url, UseShellExecute = true }); }
            catch { }
        }
        ShowToast($"已打开 {repos.Count} 个仓库");
    }

    private void CopySelectedSubLinks()
    {
        var repos = GetSelectedRepos();
        if (repos.Count == 0) { ShowToast("请先选中仓库"); return; }

        var sb = new System.Text.StringBuilder();
        int linkCount = 0;
        foreach (var repo in repos)
        {
            foreach (var link in repo.Links.Where(l => !string.IsNullOrEmpty(l.Url)))
            {
                sb.AppendLine(link.Url);
                linkCount++;
            }
        }

        if (linkCount > 0)
        {
            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
                ShowToast($"已复制 {repos.Count} 个仓库的 {linkCount} 条订阅链接");
            }
            catch (Exception ex) { Log($"复制失败: {ex.Message}"); }
        }
        else
        {
            // 没有已分析的链接, 复制仓库 URL 作为备选
            var urls = repos.Select(r => r.Url);
            try
            {
                Clipboard.SetText(string.Join("\n", urls));
                ShowToast($"已复制 {repos.Count} 个仓库的 GitHub 链接 (订阅链接尚未分析完成)");
            }
            catch { }
        }
    }

    private void CopySelectedRepoNames()
    {
        var repos = GetSelectedRepos();
        if (repos.Count == 0) { ShowToast("请先选中仓库"); return; }
        try
        {
            Clipboard.SetText(string.Join("\n", repos.Select(r => r.FullName)));
            ShowToast($"已复制 {repos.Count} 个仓库名");
        }
        catch { }
    }

    private void ToggleSelectedFavorites()
    {
        var repos = GetSelectedRepos();
        if (repos.Count == 0) { ShowToast("请先选中仓库"); return; }
        int added = 0, removed = 0;
        foreach (var repo in repos)
        {
            var existing = _favorites.FirstOrDefault(f => f.FullName == repo.FullName);
            if (existing != null) { _favorites.Remove(existing); removed++; }
            else { _favorites.Add(repo); added++; }
        }
        FavoritesStore.Save(_favorites);
        ShowToast($"收藏 +{added} -{removed}");
        ApplyFilters();
    }

    /// <summary>
    /// 收藏视图下的右键「删除」：只从收藏夹移除，不做「加入收藏」的切换。
    /// 支持多选一次删多个。
    /// </summary>
    private void RemoveSelectedFromFavorites()
    {
        var repos = GetSelectedRepos();
        if (repos.Count == 0) { ShowToast("请先选中仓库"); return; }
        var names = new HashSet<string>(repos.Select(r => r.FullName), StringComparer.OrdinalIgnoreCase);
        int before = _favorites.Count;
        _favorites.RemoveAll(f => names.Contains(f.FullName));
        int removed = before - _favorites.Count;
        FavoritesStore.Save(_favorites);
        ShowToast($"已删除 {removed} 个收藏");
        ApplyFilters();
    }

    private async Task RecheckSelectedRepos()
    {
        var repos = GetSelectedRepos();
        if (repos.Count == 0) { ShowToast("请先选中仓库"); return; }
        if (_isRunning) return;

        ShowToast($"重新检查 {repos.Count} 个仓库…", persistent: true);
        foreach (var repo in repos)
        {
            try
            {
                await AnalyzeSingleRepo(repo, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log($"  ⚠ {repo.FullName}: {ex.Message}");
            }
        }
        ApplyFilters();
        ResultCache.Save(_allRepos);
        ShowToast($"重新检查完成 ({repos.Count} 个仓库)");
    }

    // ══════════════════════ 单选兼容 (保留) ══════════════════════

    private void CopySubLink()
    {
        if (_contextRepo == null) return;
        var link = _contextRepo.Links.FirstOrDefault(l => l.NodeCount > 0) ??
                   _contextRepo.Links.FirstOrDefault();
        if (link != null && !string.IsNullOrEmpty(link.Url))
        {
            try
            {
                Clipboard.SetText(link.Url);
                ShowToast($"已复制 {_contextRepo.FullName} 的订阅链接");
                Log($"已复制: {link.Url}");
            }
            catch (Exception ex) { Log($"复制失败: {ex.Message}"); }
        }
        else ShowToast($"{_contextRepo.FullName} 无订阅链接");
    }

    private void CopyRepoName()
    {
        if (_contextRepo == null) return;
        try { Clipboard.SetText(_contextRepo.FullName); ShowToast("已复制仓库名"); } catch { }
    }

    private async Task RecheckRepo()
    {
        if (_contextRepo == null || _isRunning) return;
        var repo = _contextRepo;
        Log($"重新检查: {repo.FullName}");
        var commits = await _github.GetRecentCommitsAsync(repo.FullName, 7);
        repo.CommitsLast7Days = commits.Count;
        repo.DistinctActiveDays = GitHubAnalyzer.CountDistinctDays(commits);
        repo.ProcessingType = GitHubAnalyzer.DetectProcessingType(commits);
        repo.Links = await SubscriptionFinder.FindLinksAsync(_github, repo.FullName, default, msg => Log(msg), repo.Branch);
        repo.TotalNodes = GitHubAnalyzer.BestNodeCount(repo);
        repo.Score = GitHubAnalyzer.CalculateScore(repo);
        ApplyFilters();
        UpdateDetails();
    }

    // ══════════════════════ 检查器: 详情 ══════════════════════

    private void UpdateDetails()
    {
        if (_detailsPanel == null || IsDisposed) return;
        _detailsPanel.SuspendLayout();
        _detailsPanel.Controls.Clear();

        // 多选时显示第一个选中行的详情
        int idx = -1;
        try
        {
            if (_dgv.SelectedRows.Count > 0)
                idx = _dgv.SelectedRows[0].Index;
            else
                idx = _dgv.CurrentRow?.Index ?? -1;
        }
        catch { }

        if (idx < 0 || idx >= _filteredRepos.Count)
        {
            int selectedCount = _dgv.SelectedRows.Count;
            string hint = selectedCount > 1
                ? $"已选中 {selectedCount} 个仓库\n\n右键菜单支持:\n• 复制全部订阅链接\n• 复制全部仓库名\n• 批量加入收藏\n• 批量重新检查"
                : "选择一行查看仓库详情\n按住 Ctrl/Shift 多选\n链接卡片点击即复制";
            _detailsPanel.Controls.Add(new Label
            {
                Text = hint,
                ForeColor = Theme.InkMid, AutoSize = true, Dock = DockStyle.Top,
                Padding = new Padding(4, 8, 0, 0), Font = Fonts.Ui9, BackColor = Theme.PaperHi
            });
            _detailsPanel.ResumeLayout();
            return;
        }

        var repo = _filteredRepos[idx];
        bool isFav = _favorites.Any(f => f.FullName == repo.FullName);
        var added = new List<Control>();

        if (repo.Links.Count == 0)
        {
            added.Add(new Label
            {
                Text = "未检测到订阅链接", ForeColor = Theme.Ochre,
                AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 4, 0, 4),
                Font = Fonts.Ui9, BackColor = Theme.PaperHi
            });
        }
        else
        {
            foreach (var link in repo.Links)
            {
                var url = link.Url;
                string status;
                Color color;

                if (!link.IsAnalyzed && _isRunning)
                {
                    status = "⏳ 分析中...";
                    color = Theme.Ochre;
                }
                else if (!link.IsAnalyzed)
                {
                    status = "○ 未验证";
                    color = Theme.InkMid;
                }
                else if (!link.IsValid)
                {
                    status = "○ 无效";
                    color = Theme.StampDeep;
                }
                else if (link.NodeCount > 0)
                {
                    status = $"● {link.NodeCount} 节点";
                    color = Theme.Live;
                }
                else
                {
                    status = "○ 无节点";
                    color = Theme.InkMid;
                }

                var card = new Panel
                {
                    Dock = DockStyle.Top, AutoSize = true,
                    BackColor = Theme.PaperDeep, Padding = new Padding(10, 7, 8, 8),
                    Margin = new Padding(0, 0, 0, 8)
                };
                var btn = new ModernButton
                {
                    Text = "复制", Ghost = true, BackColor = Theme.PaperDeep,
                    Size = new Size(52, 24), Dock = DockStyle.Right,
                    CornerRadius = 2, Margin = new Padding(6, 1, 0, 0), Font = Fonts.Ui9
                };
                btn.Click += (s, e) =>
                {
                    try { Clipboard.SetText(url); ShowToast($"已复制 {repo.FullName} 的订阅链接"); Log($"已复制: {url}"); }
                    catch { }
                };
                var name = new LinkLabel
                {
                    Text = $"{(link.IsValid ? "●" : "○")} [{link.Type}] {link.Name}  ·  {status}",
                    LinkColor = color, ActiveLinkColor = Theme.Stamp, VisitedLinkColor = Theme.InkMid,
                    AutoSize = true, MaximumSize = new Size(290, 0),
                    Dock = DockStyle.Top, Font = Fonts.Ui9, BackColor = Theme.PaperDeep
                };
                name.Click += (s, e) =>
                {
                    try { Clipboard.SetText(url); ShowToast($"已复制 {repo.FullName} 的订阅链接"); Log($"已复制: {url}"); }
                    catch { }
                };
                var urlLabel = new Label
                {
                    Text = url, ForeColor = Theme.InkLow,
                    AutoSize = true, MaximumSize = new Size(198, 0),
                    Dock = DockStyle.Top, Font = Fonts.Mono8,
                    BackColor = Theme.PaperDeep, Padding = new Padding(0, 3, 0, 0)
                };
                card.Controls.Add(urlLabel);
                card.Controls.Add(name);
                card.Controls.Add(btn);
                added.Add(card);
            }
        }

        if (!string.IsNullOrEmpty(repo.Description))
            added.Add(new Label
            {
                Text = repo.Description,
                ForeColor = Theme.DescGray,
                AutoSize = true, MaximumSize = new Size(268, 0),
                Dock = DockStyle.Top, Padding = new Padding(4, 4, 0, 10),
                Font = Fonts.Ui9, BackColor = Theme.PaperHi
            });

        added.Add(new Label
        {
            Text = $"{(isFav ? "★ 已收藏 · " : "")}Star {repo.Stars} · {repo.StatusText} · {repo.ProcessingType} · 7天 {repo.CommitsLast7Days} 次 ({repo.DistinctActiveDays} 天) · 库龄 {repo.AgeDays} 天 · 活跃度 {repo.Score} · 节点 {repo.TotalNodes}",
            ForeColor = Theme.InkMid, AutoSize = true, MaximumSize = new Size(268, 0),
            Dock = DockStyle.Top,
            Padding = new Padding(4, 2, 0, 4), Font = Fonts.Ui9, BackColor = Theme.PaperHi
        });

        var title = new LinkLabel
        {
            Text = repo.FullName,
            Font = Fonts.Mono10,
            LinkColor = Theme.Stamp, ActiveLinkColor = Theme.StampHi, VisitedLinkColor = Theme.Stamp,
            AutoSize = true, MaximumSize = new Size(268, 0),
            Dock = DockStyle.Top, Padding = new Padding(4, 2, 0, 6),
            Tag = repo.Url, BackColor = Theme.PaperHi
        };
        title.Click += (s, e) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = (string)((Control)s!).Tag!, UseShellExecute = true }); }
            catch { }
        };
        added.Add(title);

        for (int i = added.Count - 1; i >= 0; i--)
            _detailsPanel.Controls.Add(added[i]);

        _detailsPanel.ResumeLayout();
        if (_detailsPanel.Controls.Count > 0)
            _detailsPanel.ScrollControlIntoView(_detailsPanel.Controls[_detailsPanel.Controls.Count - 1]);
    }

    // ══════════════════════ 核心: 搜索 + 流式分析 ══════════════════════

    private async void OnSearchClick()
    {
        if (_isRunning) { _cts?.Cancel(); return; }
        await SearchAndAnalyze();
    }

    private async Task SearchAndAnalyze()
    {
        _isRunning = true;
        _btnSearch.Text = "⏹ 取消";
        _btnSearch.BaseColor = Theme.StampDeep;
        _btnSearch.Invalidate();
        _btnExport.Enabled = false;
        _progressPct = 1;
        _progressLine.Invalidate();

        // 搜索时切回搜索视图
        if (IsFavoritesView)
        {
            _viewMode = "search";
            Common.StyleTab(_btnFavorites, false);
            UpdateEmptyText();
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var token = _txtToken.Text.Trim();
        _github = new GitHubService(string.IsNullOrEmpty(token) ? null : token);

        // 自动代理模式: 搜索前自动探测最快代理 (5分钟内复用缓存)
        if (_cmbProxy.SelectedItem?.ToString() == "自动代理")
        {
            if ((DateTime.Now - _lastProxyTestTime).TotalMinutes > 5)
            {
                await AutoDetectProxyAsync(ct);
                _lastProxyTestTime = DateTime.Now;
            }
            else
            {
                Log("自动代理: 使用上次测速结果（缓存）");
            }
        }

        var targetCount = (int)_numRepos.Value;
        var since = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var queries = new[]
        {
            $"v2ray nodes pushed:>{since}",
            $"free proxy subscription pushed:>{since}",
            $"clash nodes pushed:>{since}",
            $"免费节点 pushed:>{since}",
            $"v2ray 订阅 pushed:>{since}",
            $"free v2ray pushed:>{since}",
            $"trojan nodes pushed:>{since}",
            $"sing-box subscription pushed:>{since}"
        };

        var seen = new HashSet<string>();
        var found = new List<RepoInfo>();
        _allRepos = found;   // 流式: 立即接管数据源
        ApplyFilters();

        int searched = 0;

        int processMode = _cmbQueryMode.SelectedIndex;

        // 处理模式配置
        int concurrencyLevel = processMode switch
        {
            0 => 10,  // 极速
            1 => 6,   // 标准
            2 => 8,   // 深度
            3 => 1,   // 兼容
            _ => 6
        };

        bool concurrentSearch = processMode != 3;  // 兼容模式串行搜索

        try
        {
            ShowToast("开始搜索仓库…", persistent: true);

            if (concurrentSearch)
            {
                // ===== 并发搜索 =====
                var searchTasks = queries.Select(async q => await _github.SearchReposAsync(q, 30, ct));
                var allResults = await Task.WhenAll(searchTasks);

                foreach (var results in allResults)
                {
                    foreach (var r in results)
                    {
                        if (found.Count >= targetCount) break;
                        if (seen.Contains(r.FullName)) continue;
                        if (r.Fork) continue;
                        if (_favorites.Any(f => f.FullName == r.FullName)) continue;
                        if (SearchHistory.IsExcluded(r.FullName)) continue;

                        if (GitHubAnalyzer.IsStale(r)) continue;

                        seen.Add(r.FullName);
                        found.Add(GitHubAnalyzer.ToRepoInfo(r));
                    }
                    ApplyFilters();
                }
            }
            else
            {
                // ===== 串行搜索 (兼容模式) =====
                foreach (var q in queries)
                {
                    if (found.Count >= targetCount || ct.IsCancellationRequested) break;
                    searched++;
                    _progressPct = (int)(searched * 100.0 / queries.Length * 0.3);
                    _progressLine.Invalidate();
                    Log($"搜索: {q}");
                    var results = await _github.SearchReposAsync(q, 30, ct);
                    int newCount = 0, skippedFork = 0, skippedFav = 0, skippedOld = 0;
                    foreach (var r in results)
                    {
                        if (found.Count >= targetCount) break;
                        if (seen.Contains(r.FullName)) continue;
                        if (r.Fork) { skippedFork++; continue; }
                        if (_favorites.Any(f => f.FullName == r.FullName)) { skippedFav++; continue; }

                        if (GitHubAnalyzer.IsStale(r)) { skippedOld++; continue; }

                        if (SearchHistory.IsExcluded(r.FullName)) continue;

                        seen.Add(r.FullName);
                        found.Add(GitHubAnalyzer.ToRepoInfo(r));
                        newCount++;
                    }
                    string hint = newCount == 0 ? " · 网络缓慢建议点击 📡 测速添加加速代理" : "";
                    ShowToast($"搜索中 {searched}/{queries.Length} · 已找到 {found.Count} 个 (本次+{newCount}){hint}", persistent: true);
                    Log($"  +{newCount} (累计 {found.Count})" +
                        (skippedFork > 0 ? $" 跳过 {skippedFork} fork" : "") +
                        (skippedFav > 0 ? $" 跳过 {skippedFav} 已收藏" : "") +
                        (skippedOld > 0 ? $" 跳过 {skippedOld} 超60天未更新" : ""));
                    ApplyFilters();
                    await Task.Delay(400, ct);
                }
            }

            Log($"\n共 {found.Count} 个候选仓库, 开始分析...\n");

            // 流式分析: 逐个仓库 REST 分析 (带重试), 每完成一个立即刷新
            int done = 0;
            var repoDone = new Progress<RepoInfo>(r =>
            {
                _progressPct = 30 + done * 70 / Math.Max(1, found.Count);
                _progressLine.Invalidate();
                ShowToast($"分析 {done}/{found.Count} · {r.FullName}", persistent: true);
                if (!IsFavoritesView) ApplyFilters();
            });

            // 分析阶段: 根据下拉框选择批量或逐个
            var analysisDone = new Progress<RepoInfo>(r =>
            {
                _progressPct = 30 + done * 70 / Math.Max(1, found.Count);
                _progressLine.Invalidate();
                ShowToast($"分析 {done}/{found.Count} · {r.FullName}", persistent: true);
                if (!IsFavoritesView) ApplyFilters();
            });

            using (ct.Register(() => Log("[取消] 令牌已取消! 堆栈: " + Environment.StackTrace)))
            {
                if (concurrencyLevel > 1)
                {
                    // ===== 并发分析 =====
                    await Task.Run(async () =>
                    {
                        var sem = new SemaphoreSlim(concurrencyLevel);
                        var tasks = found.Select(async repo =>
                        {
                            await sem.WaitAsync();
                            try
                            {
                                await AnalyzeSingleRepo(repo, ct);
                                Interlocked.Increment(ref done);
                                ((IProgress<RepoInfo>)analysisDone).Report(repo);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                Log($"  ⚠ {repo.FullName}: {ex.Message}");
                                repo.Score = 0;
                                Interlocked.Increment(ref done);
                            }
                            finally { sem.Release(); }
                        });
                        await Task.WhenAll(tasks);
                    }, ct);
                }
                else
                {
                    // ===== 串行分析 (兼容模式) =====
                    foreach (var repo in found.ToList())
                    {
                        if (ct.IsCancellationRequested) break;

                        try
                        {
                            await AnalyzeSingleRepo(repo, ct);
                            done++;
                            ((IProgress<RepoInfo>)repoDone).Report(repo);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log($"  ⚠ {repo.FullName}: {ex.Message}");
                            repo.Score = 0;
                            done++;
                        }
                    }
                }
            }

            int noCommitData = found.Count(r => r.CommitsLast7Days == 0 && r.DaysInactive > 0);
            if (noCommitData > found.Count / 2 && !_github.HasToken)
                Log($"⚠ {noCommitData} 个仓库提交数据为空 — 可能触发匿名 API 限流, 建议填写 Token\n");

            foreach (var repo in found.Take(8))
                Log($"  {repo.FullName,-48} 提交:{repo.CommitsLast7Days,3} ({repo.DistinctActiveDays}天) {repo.StatusText,-8} {repo.ProcessingType,-8} 评分:{repo.Score,3} 节点:{repo.TotalNodes,4}");
            if (found.Count > 8) Log($"  ... 其余 {found.Count - 8} 个见列表");

            // 分析结束: 将所有 IsAnalyzed=false 的链接标记为已处理 (避免永远显示"分析中")
            foreach (var repo in found)
            {
                foreach (var link in repo.Links)
                {
                    if (!link.IsAnalyzed)
                    {
                        link.IsAnalyzed = true;
                        link.IsValid = false;  // 未成功验证
                    }
                }
            }

            ApplyFilters();
            ResultCache.Save(_allRepos);

            // 代理失效检测: 节点为0的仓库比例过高时, 清空测速缓存, 下次自动重测
            if (found.Count > 0)
            {
                int zeroNodeCount = found.Count(r => r.TotalNodes == 0);
                double zeroRatio = (double)zeroNodeCount / found.Count;
                if (zeroRatio > 0.7)
                {
                    _lastProxyTestTime = DateTime.MinValue;
                    Log($"⚠ {zeroNodeCount}/{found.Count} 个仓库节点为0, 代理可能失效, 下次将重新测速");
                }
            }

            // 保存已搜索仓库到历史记录
            var searchedNames = found.Select(r => r.FullName).ToList();
            var saveDays = (int)_numSaveDays.Value;
            SearchHistory.AddRepos(searchedNames, saveDays);
            int excludedCount = SearchHistory.GetExcludedCount();

            _btnExport.Enabled = true;

            if (!_inspectorOpen) ToggleInspector();

            string historyHint = excludedCount > 0 ? $" · 已排除 {excludedCount} 个" : "";
            ShowToast($"完成 · {_filteredRepos.Count} 个仓库 · Ctrl+C 复制选中链接{historyHint}");
            Log($"\n完成: {found.Count} 个仓库, {_filteredRepos.Count} 个通过筛选, 已缓存\n");
        }
        catch (OperationCanceledException)
        {
            // 取消信息只进日志, 不弹 Toast
            Log("\n已取消 — 保留已分析的部分结果, 可继续使用\n");
            // 取消后也更新未分析的链接状态
            foreach (var repo in _allRepos)
            {
                foreach (var link in repo.Links)
                {
                    if (!link.IsAnalyzed)
                    {
                        link.IsAnalyzed = true;
                        link.IsValid = false;
                    }
                }
            }
            if (_allRepos.Count > 0)
            {
                ApplyFilters();
                _btnExport.Enabled = _filteredRepos.Count > 0;
            }
        }
        catch (Exception ex)
        {
            Log($"错误: {ex.Message}");
            ShowToast("发生错误 · 打开日志查看详情");
        }

        _progressPct = 0;
        _progressLine.Invalidate();
        _isRunning = false;
        _btnSearch.Text = "🔍 搜索并分析";
        _btnSearch.BaseColor = Theme.Stamp;
        _btnSearch.Invalidate();
    }

    // ══════════════════════ 分析辅助 (含重试) ══════════════════════

    /// <summary>分析仓库提交历史, 失败自动重试 (逐条重试, 非批量)</summary>
    /// <summary>分析单个仓库: 提交历史 + 订阅链接 + 节点统计 (含重试)</summary>
    private async Task AnalyzeSingleRepo(RepoInfo repo, CancellationToken ct)
    {
        const int maxRetries = 3;

        Log($"┌─ {repo.FullName}");

        // --- 分析提交历史 ---
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Log($"│  [重试 {attempt}] 获取提交历史...");
                    await Task.Delay(800 * attempt, ct);
                }
                Log($"│  获取提交历史...");
                var commits = await _github.GetRecentCommitsAsync(repo.FullName, 7, ct);
                repo.CommitsLast7Days = commits.Count;
                repo.DistinctActiveDays = GitHubAnalyzer.CountDistinctDays(commits);
                repo.ProcessingType = GitHubAnalyzer.DetectProcessingType(commits);
                repo.CommitsAnalyzed = true;
                Log($"│  ✓ 提交: {commits.Count} 次 (活跃 {repo.DistinctActiveDays} 天) [{repo.ProcessingType}]");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt < maxRetries)
                {
                    Log($"│  ⚠ 提交获取失败 ({ex.Message}), 重试 {attempt + 1}/{maxRetries}...");
                }
                else
                {
                    Log($"│  ✗ 提交分析失败: {ex.Message}");
                    repo.CommitsLast7Days = 0;
                    repo.ProcessingType = "分析失败";
                    repo.CommitsAnalyzed = true;
                }
            }
        }

        // --- 分析订阅链接 ---
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Log($"│  [重试 {attempt}] 获取订阅链接...");
                    await Task.Delay(800 * attempt, ct);
                }
                Log($"│  探测订阅链接...");
                repo.Links = await SubscriptionFinder.FindLinksAsync(_github, repo.FullName, ct, msg => Log(msg), repo.Branch);
                repo.TotalNodes = GitHubAnalyzer.BestNodeCount(repo);

                if (repo.Links.Count > 0)
                {
                    var validLinks = repo.Links.Where(l => l.NodeCount > 0).ToList();
                    if (validLinks.Count > 0)
                    {
                        Log($"│  ✓ 链接: {repo.Links.Count} 条 (有效 {validLinks.Count}), 节点: {repo.TotalNodes}");
                        foreach (var l in validLinks)
                            Log($"│    ● {l.Name} ({l.Type}) = {l.NodeCount} 节点");
                    }
                    else
                    {
                        Log($"│  ○ 链接: {repo.Links.Count} 条 (均未验证到节点)");
                    }
                }
                else
                {
                    Log($"│  ✗ 未找到订阅链接");
                }
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt < maxRetries)
                {
                    Log($"│  ⚠ 链接获取失败 ({ex.Message}), 重试 {attempt + 1}/{maxRetries}...");
                }
                else
                {
                    Log($"│  ✗ 链接分析失败: {ex.Message}");
                    repo.Links = new List<SubscriptionLink>();
                    repo.TotalNodes = 0;
                }
            }
        }

        repo.Score = GitHubAnalyzer.CalculateScore(repo);
        Log($"└─ 完成 [评分: {repo.Score}]");
    }



    /// <summary>节点数 = 已验证链接中的最大值 (排除待分析的)</summary>

    // ══════════════════════ 筛选 / 排序 / 显示 ══════════════════════

    private void ApplyFilters()
    {
        var source = IsFavoritesView ? _favorites : _allRepos;
        if (source.Count == 0) { _filteredRepos = new List<RepoInfo>(); RefreshGrid(); return; }

        int inactiveLimit = (int)_numInactiveDays.Value;
        int minPerDay = (int)_numMinCommits.Value;
        int minAge = (int)_numActiveDays.Value;
        bool autoClean = _chkAutoClean.Checked;
        var filterText = _txtFilter?.Text?.Trim() ?? "";

        IEnumerable<RepoInfo> query = source.Where(r =>
        {
            if (!IsFavoritesView)
            {
                // 运行中未分析的仓库先保留 (流式展示), 分析后按条件过滤
                if (autoClean && r.DaysInactive >= inactiveLimit && (!_isRunning || r.CommitsAnalyzed)) return false;
                if (minPerDay > 0 && r.CommitsAnalyzed && r.CommitsLast7Days / 7.0 < minPerDay) return false;
                if (minAge > 0 && r.AgeDays < minAge) return false;
            }
            return true;
        });

        if (filterText.Length > 0)
            query = query.Where(r =>
                r.FullName.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                (r.Description ?? "").Contains(filterText, StringComparison.OrdinalIgnoreCase));

        _filteredRepos = SortRepos(query.ToList());
        RefreshGrid();
    }

    /// <summary>活跃度分值 → 颜色。统一映射，别在 CellFormatting 和自绘里各写一份。</summary>
    private static Color ScoreColor(int score)
        => score >= 75 ? Theme.Stamp : score >= 45 ? Theme.Ochre : Theme.InkLow;

    private List<RepoInfo> SortRepos(List<RepoInfo> list)
    {
        // 「可用分/均速」的排序键来自 SpeedTestStore（字典查询）。若直接在比较器里
        // 调用，O(n log n) 次排序会做 O(n log n) 次字典查询。先按仓库名预计算一次，
        // 排序时只查内存字典。
        Dictionary<string, int>? keyCache = null;
        if (_sortField is "Usable" or "AvgSpeed")
        {
            keyCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in list)
                keyCache[r.FullName] = _sortField == "Usable"
                    ? SpeedTestStore.SortScore(r.FullName)
                    : SpeedTestStore.SortAvgSpeed(r.FullName);
        }

        Comparison<RepoInfo> cmp = _sortField switch
        {
            "Repo" => (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal),
            "Stars" => (a, b) => a.Stars.CompareTo(b.Stars),
            "LastPush" => (a, b) => b.DaysInactive.CompareTo(a.DaysInactive),
            "Status" => (a, b) => a.DaysInactive.CompareTo(b.DaysInactive),
            "Commits" => (a, b) => a.CommitsLast7Days.CompareTo(b.CommitsLast7Days),
            "Age" => (a, b) => a.AgeDays.CompareTo(b.AgeDays),
            "Processing" => (a, b) => string.Compare(a.ProcessingType, b.ProcessingType, StringComparison.Ordinal),
            "Nodes" => (a, b) => a.TotalNodes.CompareTo(b.TotalNodes),
            "Links" => (a, b) => a.Links.Count.CompareTo(b.Links.Count),
            "Usable" => (a, b) => keyCache![a.FullName].CompareTo(keyCache![b.FullName]),
            "AvgSpeed" => (a, b) => keyCache![a.FullName].CompareTo(keyCache![b.FullName]),
            _ => (a, b) => a.Score.CompareTo(b.Score)
        };
        var sorted = new List<RepoInfo>(list);
        sorted.Sort((a, b) => _sortDesc ? cmp(b, a) : cmp(a, b));
        return sorted;
    }

    private void RefreshGrid()
    {
        // 节流：120ms 内的连续刷新合并成一次。跳过的这次由延迟任务兜底补刷，
        // 保证最后一次操作后表格一定是最新的。
        if ((DateTime.UtcNow - _lastRefresh).TotalMilliseconds < 120)
        {
            if (!_refreshQueued)
            {
                _refreshQueued = true;
                _ = Task.Delay(120).ContinueWith(_ =>
                {
                    _refreshQueued = false;
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new Action(RefreshGrid));
                }, TaskScheduler.Default);
            }
            return;
        }
        _lastRefresh = DateTime.UtcNow;

        foreach (DataGridViewColumn col in _dgv.Columns)
        {
            col.HeaderText = col.Name switch
            {
                "Repo" => "仓库", "Stars" => "Star",
                "LastPush" => "最后更新", "Status" => "状态", "Commits" => "7天提交",
                "Age" => "库龄", "Processing" => "方式", "Score" => "活跃度",
                "Nodes" => "节点", "Links" => "链接",
                "Usable" => "可用分", "AvgSpeed" => "均速", _ => col.Name
            };
            if (col.Name == _sortField)
                col.HeaderText += _sortDesc ? " ▼" : " ▲";
        }

        // 保留当前选中 (流式刷新时选择不跳回第一行)
        string? keepSel = null;
        try { keepSel = _dgv.CurrentRow?.Cells["Repo"]?.Value?.ToString(); } catch { }

        // 批量操作包裹, 避免逐行重绘闪烁
        _dgv.SuspendLayout();
        try
        {
            _dgv.Rows.Clear();
            foreach (var r in _filteredRepos)
            {
                var st = SpeedTestStore.Get(r.FullName);
                var rowIdx = _dgv.Rows.Add(
                    r.FullName, r.Stars.ToString(), GitHubAnalyzer.RelativeTime(r.LastPush),
                    r.StatusText, r.CommitsLast7Days.ToString(), r.AgeDays.ToString(),
                    r.ProcessingType, r.Score.ToString(), r.TotalNodes.ToString(), r.Links.Count.ToString(),
                    st == null || st.Demoted ? "—" : st.Score.ToString(),
                    st == null || st.Demoted ? "—" : st.SpeedText
                );

                if (st != null)
                {
                    var row = _dgv.Rows[rowIdx];
                    var usableCell = row.Cells["Usable"]!;
                    // 降权的仓库连可用分一起沉底，视觉上不再伪装成有测速数据
                    if (st.Demoted)
                    {
                        usableCell.Style.ForeColor = Theme.InkLow;
                        usableCell.Style.SelectionForeColor = Theme.InkLow;
                        usableCell.ToolTipText = "连续多轮零通过，已降权。右键可解除。";
                    }
                    else if (st.Score >= 70)
                    {
                        usableCell.Style.ForeColor = Theme.Live;
                        usableCell.ToolTipText =
                            $"{st.TestedText}\n通过 {st.PassNodes}/{st.TotalNodes} · 存活 {st.AliveNodes}\n均速 {st.SpeedText} · 最快 {st.BestSpeedKbps / 1024.0:F1} MB/s";
                    }
                    else if (st.Score > 0)
                    {
                        usableCell.Style.ForeColor = Theme.Ochre;
                        usableCell.ToolTipText =
                            $"{st.TestedText}\n通过 {st.PassNodes}/{st.TotalNodes} · 存活 {st.AliveNodes}\n均速 {st.SpeedText}";
                    }
                    row.Cells["AvgSpeed"]!.ToolTipText = st.SpeedText;
                }
            }
        }
        finally
        {
            _dgv.ResumeLayout();
        }

        if (keepSel != null)
        {
            int idx = _filteredRepos.FindIndex(r => r.FullName == keepSel);
            if (idx >= 0)
            {
                try { _dgv.CurrentCell = _dgv.Rows[idx].Cells[0]; _lblEmptyIfAny(); return; } catch { }
            }
        }
        if (_dgv.Rows.Count > 0 && _dgv.CurrentCell == null)
        {
            try { _dgv.CurrentCell = _dgv.Rows[0].Cells[0]; } catch { }
        }
        _lblEmptyIfAny();
    }

    private void _lblEmptyIfAny() => _emptyPanel.Visible = _filteredRepos.Count == 0;

    private void ShowExportDialog()
    {
        if (_filteredRepos.Count == 0)
        {
            ShowToast("没有可导出的仓库, 请先搜索并分析");
            return;
        }
        using var dlg = new ExportDialog(_filteredRepos, _github, _settings.SubConverterUrl);
        dlg.ShowDialog(this);
    }

    private void ShowFeatureLibraryDialog()
    {
        using var dlg = new FeatureLibraryDialog();
        dlg.ShowDialog(this);
    }

    private void Log(string message)
    {
        if (IsDisposed) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

        // 写入缓冲区
        lock (_logBuffer)
        {
            _logBuffer.Append(line);
        }

        // 持久化到文件
        LogStore.Append(line);

        // 启动定时器批量刷入 (避免频繁重绘)
        if (_logFlushTimer == null && _txtLog != null && !_txtLog.IsDisposed)
        {
            _logFlushTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _logFlushTimer.Tick += (s, e) => FlushLogBuffer();
            _logFlushTimer.Start();
        }
    }

    /// <summary>批量刷入日志缓冲区到界面</summary>
    private void FlushLogBuffer()
    {
        string text;
        lock (_logBuffer)
        {
            if (_logBuffer.Length == 0) return;
            text = _logBuffer.ToString();
            _logBuffer.Clear();
        }

        if (_txtLog == null || _txtLog.IsDisposed) return;

        if (_txtLog.InvokeRequired)
            _txtLog.Invoke(() => _txtLog.AppendText(text));
        else
            _txtLog.AppendText(text);
    }
}
