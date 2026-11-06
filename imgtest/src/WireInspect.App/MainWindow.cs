using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenCvSharp;
using WireInspect.Core;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Window = Avalonia.Controls.Window;

namespace WireInspect.App;

/// <summary>Main window, laid out like app-gpt's ui/main_window.py: toolbar on top, file tree on the
/// left, viewer + thumbnails in the middle, defect / wire tables and the verdict on the right.</summary>
public sealed class MainWindow : Window
{
    const string Pending = "pending";
    static readonly (string name, object? allowed)[] Filters =
    [
        ("全部", null), ("疑似缺陷", new[] { Session.Suspect }), ("FAIL", new[] { Session.Fail }), ("PASS", new[] { Session.Pass }),
        ("未复核", new[] { Session.Unchecked, Session.AutoOk, Session.Suspect }), ("未检出候选（待复核）", new[] { Session.AutoOk }),
        ("有未处理候选框", Pending),
    ];
    const string WireOk = "#2ee6ff", WireBad = "#ff4d4f";
    const int RecentMax = 10;

    readonly AppConfig _cfg = AppConfig.Load();
    Session? _session;
    Product? _product;
    string? _current;
    Mat? _gray;
    Bitmap? _bmpOriginal, _bmpEnhanced;
    string _mode = "wire";
    string? _selectedDefect;
    readonly Dictionary<string, TreeViewItem> _treeItems = [];
    readonly Dictionary<string, StatusDot> _dots = [];
    readonly Dictionary<string, TextBlock> _labels = [];
    readonly List<TreeViewItem> _folders = [];
    readonly Dictionary<string, RadioButton> _modeButtons = [];
    readonly HashSet<string> _busy = [];
    readonly Dictionary<string, Bitmap> _thumbCache = [];
    string? _thumbFolder;
    CancellationTokenSource? _thumbCts, _detectCts;
    bool _running, _loadingProducts;

    // controls
    readonly Button _btnOpen, _btnRecent, _btnStart, _btnStop, _btnRerun, _btnClear, _btnReport, _btnNewProduct, _btnEditProduct;
    readonly ComboBox _productCombo = new() { MinWidth = 150 };
    readonly CheckBox _chkFollow = new() { Content = "自动显示最新", IsChecked = true };
    readonly TextBox _operator = new() { Width = 120 };
    readonly ComboBox _filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TreeView _tree = new();
    readonly ImageViewer _viewer = new();
    readonly ListBox _thumbs = new() { Height = 140 };
    readonly TextBlock _zoomLabel = new() { Text = "100%", MinWidth = 50, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _chkMarks = new() { Content = "显示标记", IsChecked = true };
    readonly Border _banner = new() { MinHeight = 40, IsVisible = false, Padding = new Thickness(8) };
    readonly TextBlock _bannerText = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    readonly SelectableTextBlock _info = new() { Text = "请先导入文件夹", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 6) };
    readonly TabControl _tabs = new();
    readonly SimpleTable _defectTable = new(["类型", "候选分数", "来源", "状态"], "*,64,92,56");
    readonly SimpleTable _wireTable = new(["单元", "线", "长度µm", "sweep%", "偏移µm", "z", "判定"], "40,30,56,56,56,40,*");
    readonly TextBlock _wireInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 6) };
    readonly Button _btnAccept = new() { Content = "确认缺陷(A)" }, _btnReject = new() { Content = "误报(X)" }, _btnDelete = new() { Content = "删除" };
    readonly ComboBox _typeCombo = new() { ItemsSource = DefectTypes.All, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly Button _btnType = new() { Content = "改为此类型" };
    readonly ToggleButton _btnDraw = new() { Content = "手动框选(B)", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button _btnPass = new() { Content = "PASS (P)" }, _btnFail = new() { Content = "FAIL (F)" }, _btnClearVerdict = new() { Content = "清除判定", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly CheckBox _chkAutoNext = new() { Content = "判定后自动跳到下一张", IsChecked = true };
    readonly TextBox _comment = new() { Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
    readonly TextBlock _counts = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    readonly ProgressBar _progress = new() { Width = 220, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _progressText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), IsVisible = false };
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    public MainWindow()
    {
        Title = "X-Ray 焊线检测";
        Width = 1640;
        Height = 960;
        WindowState = WindowState.Maximized;
        _btnOpen = Icons.ToolButton("open", "导入文件夹");
        _btnRecent = Icons.ToolButton("recent", "最近文件夹", "重新打开最近用过的文件夹");
        _btnStart = Icons.ToolButton("start", "开始检测", "检测还没有结果的图片");
        _btnStop = Icons.ToolButton("stop", "停止");
        _btnRerun = Icons.ToolButton("rerun", "重新检测全部");
        _btnClear = Icons.ToolButton("clear", "清除标记", "清除当前图片 / 当前子文件夹 / 整个文件夹的所有缺陷标记");
        _btnReport = Icons.ToolButton("report", "保存报告", "整个文件夹的检测与复核结果导出为 CSV（汇总 + 导线明细）");
        _btnNewProduct = Icons.ToolButton("add", "新建产品", "在当前图片上框选一个单元，自动找线，生成产品配置");
        _btnEditProduct = Icons.ToolButton("edit", "编辑产品");
        Content = Build();
        Wire();
        LoadProducts(_cfg.Product);
        UpdateEnabled();
        var autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        autosave.Tick += (_, _) => { if (_session?.Dirty == true && !_running) _session.Save(); };
        autosave.Start();
        var spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        spin.Tick += (_, _) =>
        {
            if (_busy.Count == 0) return;
            StatusDot.Frame = (StatusDot.Frame + 1) % 12;
            foreach (var r in _busy) if (_dots.TryGetValue(r, out var d)) d.InvalidateVisual();
        };
        spin.Start();
        _statusTimer.Tick += (_, _) => { _status.Text = ""; _statusTimer.Stop(); };
    }

    // ------------------------------------------------------------------ layout
    Control Build()
    {
        var tb = new WrapPanel { Margin = new Thickness(4, 2) };
        Control Sep() => new Border { Width = 1, Height = 24, Background = Brush.Parse("#d0d4da"), Margin = new Thickness(6, 0) };
        TextBlock Label(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        foreach (var c in new Control[] { _btnOpen, _btnRecent, Sep(), _btnStart, _btnStop, _btnRerun, _btnClear, Sep(), _btnReport, Sep(),
                     Label("产品:"), _productCombo, _btnNewProduct, _btnEditProduct, Sep(), _chkFollow, Sep(), Label("操作员:"), _operator })
        {
            c.VerticalAlignment = VerticalAlignment.Center;
            tb.Children.Add(c);
        }
        _operator.Text = _cfg.Operator;

        // left: filter + tree + legend
        foreach (var (name, _) in Filters) _filter.Items.Add(name);
        _filter.SelectedIndex = 0;
        var legend = new WrapPanel { Margin = new Thickness(2, 4) };
        foreach (var (k, v) in Session.StatusText)
            legend.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 0, 8, 2),
                Inlines = [new Run("● ") { Foreground = Brush.Parse(Session.StatusColors[k]) }, new Run(v)],
            });
        var left = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(_filter, Dock.Top);
        DockPanel.SetDock(legend, Dock.Bottom);
        left.Children.Add(_filter);
        left.Children.Add(legend);
        left.Children.Add(new Border { BorderBrush = Brush.Parse("#d0d4da"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4), Child = _tree });

        // center: mode bar, banner, viewer, thumbnails
        var bar = new DockPanel { Margin = new Thickness(4, 2) };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var (k, t) in new[] { ("original", "原图"), ("enhanced", "增强"), ("wire", "导线高亮") })
        {
            var rb = new RadioButton { Content = t, GroupName = "mode", IsChecked = k == _mode };
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) SetMode(k); };
            _modeButtons[k] = rb;
            modes.Children.Add(rb);
        }
        modes.Children.Add(new Border { Width = 16 });
        modes.Children.Add(_chkMarks);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (t, fn) in new (string, Action)[] { ("适应窗口(0)", _viewer.Fit), ("1:1 (1)", _viewer.ActualSize),
                     ("放大", () => _viewer.Zoom(1.25)), ("缩小", () => _viewer.Zoom(0.8)) })
        {
            var b = new Button { Content = t, Padding = new Thickness(8, 3) };
            b.Click += (_, _) => fn();
            zoom.Children.Add(b);
        }
        zoom.Children.Add(_zoomLabel);
        DockPanel.SetDock(zoom, Dock.Right);
        bar.Children.Add(zoom);
        bar.Children.Add(modes);
        _banner.Child = _bannerText;

        _thumbs.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { Orientation = Orientation.Horizontal });
        ScrollViewer.SetHorizontalScrollBarVisibility(_thumbs, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_thumbs, ScrollBarVisibility.Disabled);
        var centerGrid = new Grid { RowDefinitions = RowDefinitions.Parse("*,4,Auto") };
        centerGrid.Children.Add(_viewer);
        centerGrid.Children.Add(new GridSplitter { [Grid.RowProperty] = 1, ResizeDirection = GridResizeDirection.Rows, Background = Brush.Parse("#e3e6ea") });
        _thumbs[Grid.RowProperty] = 2;
        centerGrid.Children.Add(_thumbs);
        var center = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_banner, Dock.Top);
        center.Children.Add(bar);
        center.Children.Add(_banner);
        center.Children.Add(centerGrid);

        // right: info, tabs, verdict
        var defectPage = new DockPanel { Margin = new Thickness(2) };
        var r1 = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in new[] { _btnAccept, _btnReject, _btnDelete })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.Margin = new Thickness(1);
            r1.Children.Add(b);
        }
        var r2 = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(_btnType, Dock.Right);
        r2.Children.Add(_btnType);
        r2.Children.Add(_typeCombo);
        var defectButtons = new StackPanel { Children = { r1, r2, _btnDraw }, Spacing = 2 };
        _btnDraw.Margin = new Thickness(0, 4, 0, 0);
        DockPanel.SetDock(defectButtons, Dock.Bottom);
        defectPage.Children.Add(defectButtons);
        defectPage.Children.Add(_defectTable);

        var wirePage = new DockPanel { Margin = new Thickness(2) };
        var btnWireCsv = new Button { Content = "导出CSV", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(btnWireCsv, "把本图所有导线的长度/弓高/偏移等导出为 CSV");
        btnWireCsv.Click += async (_, _) => await ExportWires();
        DockPanel.SetDock(_wireInfo, Dock.Top);
        DockPanel.SetDock(btnWireCsv, Dock.Bottom);
        wirePage.Children.Add(_wireInfo);
        wirePage.Children.Add(btnWireCsv);
        wirePage.Children.Add(_wireTable);
        _tabs.Items.Add(new TabItem { Header = "缺陷标记", Content = defectPage, FontSize = 14 });
        _tabs.Items.Add(new TabItem { Header = "导线统计", Content = wirePage, FontSize = 14 });

        var verdictRow = new UniformGrid { Columns = 2 };
        foreach (var (b, c) in new[] { (_btnPass, "#18a848"), (_btnFail, "#e02424") })
        {
            b.MinHeight = 54;
            b.Margin = new Thickness(2);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.VerticalContentAlignment = VerticalAlignment.Center;
            b.Background = Brush.Parse(c);
            b.Foreground = Brushes.White;
            b.FontSize = 20;
            b.FontWeight = FontWeight.Bold;
            b.CornerRadius = new CornerRadius(6);
            verdictRow.Children.Add(b);
        }
        var verdictBox = new Border
        {
            BorderBrush = Brush.Parse("#d0d4da"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6),
            Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "判定", FontWeight = FontWeight.SemiBold }, verdictRow, _btnClearVerdict, _chkAutoNext, new TextBlock { Text = "备注:" }, _comment } },
        };
        var keys = new TextBlock { Text = "快捷键: P/F 判定, ←/→ 上/下一张, 空格 下一张未复核,\nB 框选, A 确认, X 误报, 0 适应, 1 原尺寸", Foreground = Brush.Parse("#555"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        var rightBottom = new StackPanel { Children = { verdictBox, keys } };
        var right = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(rightBottom, Dock.Bottom);
        right.Children.Add(_info);
        right.Children.Add(rightBottom);
        right.Children.Add(_tabs);

        var main = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("300,4,*,4,360") };
        main.Children.Add(left);
        main.Children.Add(new GridSplitter { [Grid.ColumnProperty] = 1, Background = Brush.Parse("#e3e6ea") });
        center[Grid.ColumnProperty] = 2;
        main.Children.Add(center);
        main.Children.Add(new GridSplitter { [Grid.ColumnProperty] = 3, Background = Brush.Parse("#e3e6ea") });
        right[Grid.ColumnProperty] = 4;
        main.Children.Add(right);

        var statusBar = new DockPanel { Background = Brush.Parse("#f3f4f6"), Height = 26 };
        var perm = new StackPanel { Orientation = Orientation.Horizontal, Children = { _counts, _progressText, _progress } };
        DockPanel.SetDock(perm, Dock.Right);
        statusBar.Children.Add(perm);
        statusBar.Children.Add(_status);

        var root = new DockPanel();
        var toolbar = new Border { Background = Brush.Parse("#f7f8fa"), BorderBrush = Brush.Parse("#d0d4da"), BorderThickness = new Thickness(0, 0, 0, 1), Child = tb };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(statusBar);
        root.Children.Add(main);
        return root;
    }

    void Wire()
    {
        _btnOpen.Click += async (_, _) => await PickFolder();
        _btnRecent.Flyout = new MenuFlyout();
        ((MenuFlyout)_btnRecent.Flyout).Opening += (_, _) => FillRecent();
        _btnStart.Click += async (_, _) => await StartDetection(false);
        _btnRerun.Click += async (_, _) => await StartDetection(true);
        _btnStop.Click += (_, _) => { _detectCts?.Cancel(); _btnStop.IsEnabled = false; Status("正在停止…"); };
        var clear = new MenuFlyout();
        void AddClear(string text, string scope, Func<bool>? enabled = null)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += async (_, _) => await ClearMarks(scope);
            clear.Items.Add(mi);
            if (enabled != null) clear.Opening += (_, _) => mi.IsEnabled = enabled();
        }
        AddClear("清除当前图片的所有标记", "image");
        AddClear("清除当前子文件夹的所有标记", "subfolder", () => _current?.Contains('/') == true);
        AddClear("清除整个文件夹的所有标记", "folder");
        _btnClear.Flyout = clear;
        _btnReport.Click += async (_, _) => await SaveReport();
        _btnNewProduct.Click += async (_, _) => await OpenProductDialog(false);
        _btnEditProduct.Click += async (_, _) => await OpenProductDialog(true);
        _productCombo.SelectionChanged += (_, _) => { if (!_loadingProducts) SelectProduct(_productCombo.SelectedItem as string); };
        _operator.LostFocus += (_, _) =>
        {
            _cfg.Operator = _operator.Text ?? "";
            _cfg.Save();
            if (_session != null) { _session.Data.Operator = _cfg.Operator; _session.Dirty = true; }
        };
        _filter.SelectionChanged += (_, _) => ApplyFilter();
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: string rel } && rel != _current) ShowImage(rel);
        };
        _thumbs.SelectionChanged += (_, _) =>
        {
            if (_thumbs.SelectedItem is Control { Tag: string rel } && rel != _current) SelectRel(rel);
        };
        _chkMarks.IsCheckedChanged += (_, _) => { _viewer.ShowMarks = _chkMarks.IsChecked == true; _viewer.Refresh(); };
        _viewer.ZoomChanged += s => _zoomLabel.Text = $"{Math.Round(s * 100)}%";
        _viewer.BoxDrawn += BoxDrawn;
        _viewer.DefectClicked += id => SelectDefect(id, false);
        _viewer.WireClicked += i => { _tabs.SelectedIndex = 1; _wireTable.SelectTag(i); };
        _defectTable.List.SelectionChanged += (_, _) => { if (_defectTable.SelectedTag is string id && id != _selectedDefect) SelectDefect(id, true); };
        _wireTable.List.SelectionChanged += (_, _) => SelectWireRow();
        _btnAccept.Click += (_, _) => SetDefectStatus("accepted");
        _btnReject.Click += (_, _) => SetDefectStatus("rejected");
        _btnDelete.Click += (_, _) => DeleteDefect();
        _btnType.Click += (_, _) => ChangeType();
        _btnDraw.IsCheckedChanged += (_, _) => ToggleDraw(_btnDraw.IsChecked == true);
        _btnPass.Click += (_, _) => SetVerdict(Session.Pass);
        _btnFail.Click += (_, _) => SetVerdict(Session.Fail);
        _btnClearVerdict.Click += (_, _) => SetVerdict(null);
        _comment.TextChanged += (_, _) =>
        {
            if (_current != null && _session != null && (_session.Rec(_current).Comment ?? "") != (_comment.Text ?? ""))
                _session.SetComment(_current, _comment.Text ?? "");
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Closing += (_, _) =>
        {
            _detectCts?.Cancel();
            _session?.Save();
            _cfg.Save();
        };
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        bool typing = FocusManager?.GetFocusedElement() is TextBox;
        if (e.Key == Key.Escape) { _btnDraw.IsChecked = false; e.Handled = true; return; }
        if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
        {
            _session?.Save();
            Status("已保存");
            e.Handled = true;
            return;
        }
        if (typing || e.KeyModifiers != KeyModifiers.None) return;
        Action? act = e.Key switch
        {
            Key.P => () => SetVerdict(Session.Pass),
            Key.F => () => SetVerdict(Session.Fail),
            Key.Right or Key.Down => () => Step(1),
            Key.Left or Key.Up => () => Step(-1),
            Key.Space => NextUnreviewed,
            Key.B => () => _btnDraw.IsChecked = !(_btnDraw.IsChecked ?? false),
            Key.A => () => SetDefectStatus("accepted"),
            Key.X => () => SetDefectStatus("rejected"),
            Key.Delete or Key.Back => DeleteDefect,
            Key.D0 or Key.NumPad0 => _viewer.Fit,
            Key.D1 or Key.NumPad1 => _viewer.ActualSize,
            _ => null,
        };
        if (act == null) return;
        act();
        e.Handled = true;
    }

    void Status(string text)
    {
        _status.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    void ShowBanner(string? text, string color = "#1f6fd1")
    {
        _banner.IsVisible = text != null;
        _bannerText.Text = text ?? "";
        _banner.Background = Brush.Parse(color);
    }

    void UpdateEnabled()
    {
        bool has = _session != null, img = has && _current != null;
        _btnStart.IsEnabled = _btnRerun.IsEnabled = has && !_running && _product != null;
        _btnStop.IsEnabled = _running;
        _btnReport.IsEnabled = _btnClear.IsEnabled = has && !_running;
        _btnOpen.IsEnabled = _btnRecent.IsEnabled = !_running;
        _productCombo.IsEnabled = _btnNewProduct.IsEnabled = !_running;
        _btnEditProduct.IsEnabled = !_running && _product != null;
        foreach (var c in new Control[] { _btnPass, _btnFail, _btnClearVerdict, _btnAccept, _btnReject, _btnDelete, _btnType, _btnDraw, _comment })
            c.IsEnabled = img;
    }

    // ------------------------------------------------------------------ products
    void LoadProducts(string? select)
    {
        _loadingProducts = true;
        var names = Product.List();
        _productCombo.ItemsSource = names;
        _loadingProducts = false;
        var pick = names.Contains(select ?? "") ? select : names.FirstOrDefault();
        _productCombo.SelectedItem = pick;
        SelectProduct(pick);
    }

    void SelectProduct(string? name)
    {
        if (name == null)
        {
            _product = null;
            ToolTip.SetTip(_productCombo, "还没有产品：点“新建产品”");
        }
        else if (_product?.Name != name || _product.Dir != Path.Combine(AppPaths.Products, name))
        {
            try
            {
                var p = Product.Load(Path.Combine(AppPaths.Products, name));
                _product?.Template?.Dispose();
                _product = p;
                _cfg.Product = name;
                _cfg.Save();
                ToolTip.SetTip(_productCombo, $"{p.Name}：{p.Wires.Count} 根线/单元，{p.PixelUm:G4} µm/px，模板 {p.TplSize[0]}×{p.TplSize[1]}");
            }
            catch (Exception e)
            {
                _product = null;
                Status($"产品 {name} 读取失败：{e.Message}");
            }
        }
        UpdateEnabled();
        if (_current != null) RefreshDefects();
    }

    async Task OpenProductDialog(bool edit)
    {
        if (edit && _product == null) return;
        if (!edit && _gray == null)
        {
            await Dialogs.Info(this, "新建产品", "请先导入文件夹并选中一张清晰的良品图片，再新建产品。");
            return;
        }
        var dlg = new ProductDialog(edit ? _product!.Clone() : null, _gray?.Clone());
        await dlg.ShowDialog(this);
        if (dlg.Saved is { } name)
        {
            _product = null;
            LoadProducts(name);
            Status($"产品「{name}」已保存");
        }
    }

    // ------------------------------------------------------------------ folder / tree
    async Task PickFolder()
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择图片文件夹", AllowMultiple = false });
        var path = res.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await OpenFolderAsync(path);
    }

    public void OpenFolder(string path) => _ = OpenFolderAsync(path);

    public async Task OpenFolderAsync(string path)
    {
        if (_running) return;
        _session?.Save();
        _session = new Session(path) { };
        if (!string.IsNullOrEmpty(_operator.Text)) _session.Data.Operator = _operator.Text;
        _thumbCache.Clear();
        _thumbFolder = null;
        _current = null;
        _gray?.Dispose();
        _gray = null;
        _viewer.ClearOverlays();
        _viewer.SetImage(null);
        BuildTree();
        Title = $"X-Ray 焊线检测 - {_session.Root}";
        UpdateCounts();
        AddRecent(_session.Root);
        UpdateEnabled();
        if (_session.Files.Count > 0) SelectRel(_session.Files[0]);
        if (_session.Empty.Count > 0)
            await Dialogs.Info(this, "提示", $"有 {_session.Empty.Count} 张图片是 0 字节的空文件（解压或拷贝不完整），已从列表中隐藏，请重新获取原图。\n例如：{string.Join("、", _session.Empty.Take(3))}");
        if (_session.Files.Count == 0) await Dialogs.Info(this, "提示", "该文件夹内没有图片。");
    }

    void AddRecent(string path)
    {
        _cfg.RecentFolders = [path, .. _cfg.RecentFolders.Where(p => !string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.Ordinal)).Take(RecentMax - 1)];
        _cfg.Save();
    }

    void FillRecent()
    {
        var m = (MenuFlyout)_btnRecent.Flyout!;
        m.Items.Clear();
        if (_cfg.RecentFolders.Count == 0)
        {
            m.Items.Add(new MenuItem { Header = "（暂无记录）", IsEnabled = false });
            return;
        }
        foreach (var p in _cfg.RecentFolders)
        {
            bool ok = Directory.Exists(p);
            var mi = new MenuItem { Header = $"{Path.GetFileName(p.TrimEnd('/', '\\'))}    {p}{(ok ? "" : "（不存在）")}", IsEnabled = ok && !_running };
            mi.Click += async (_, _) => await OpenFolderAsync(p);
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        var clr = new MenuItem { Header = "清除最近记录" };
        clr.Click += (_, _) => { _cfg.RecentFolders = []; _cfg.Save(); };
        m.Items.Add(clr);
    }

    void BuildTree()
    {
        _treeItems.Clear();
        _dots.Clear();
        _labels.Clear();
        _folders.Clear();
        var root = new TreeViewItem { Header = Path.GetFileName(_session!.Root.TrimEnd('/', '\\')), IsExpanded = true };
        var folders = new Dictionary<string, TreeViewItem> { [""] = root };
        foreach (var rel in _session.Files)
        {
            var parts = rel.Split('/');
            var parent = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var key = string.Join('/', parts[..(i + 1)]);
                if (!folders.TryGetValue(key, out var f))
                {
                    f = new TreeViewItem { Header = parts[i], IsExpanded = true };
                    parent.Items.Add(f);
                    folders[key] = f;
                }
                parent = f;
            }
            var dot = new StatusDot();
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var it = new TreeViewItem
            {
                Tag = rel,
                Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { dot, label } },
            };
            parent.Items.Add(it);
            _treeItems[rel] = it;
            _dots[rel] = dot;
            _labels[rel] = label;
        }
        _folders.AddRange(folders.OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value));
        _tree.ItemsSource = null;
        _tree.Items.Clear();
        _tree.Items.Add(root);
        ApplyFilter();
    }

    void UpdateItem(string rel)
    {
        if (_session == null || !_treeItems.TryGetValue(rel, out var it)) return;
        var st = _session.Status(rel);
        _dots[rel].Set(Session.StatusColors[st], _busy.Contains(rel));
        int n = st is Session.Suspect or Session.Fail ? _session.LiveDefects(rel).Count : 0;
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        _labels[rel].Text = n > 0 ? $"{name}  [{n}]" : name;
        ToolTip.SetTip(it, $"{rel} - {Session.StatusText[st]}");
        var allowed = Filters[Math.Max(0, _filter.SelectedIndex)].allowed;
        it.IsVisible = allowed switch
        {
            null => true,
            string => _session.HasPending(rel),
            string[] set => set.Contains(st),
            _ => true,
        };
        if (ThumbItem(rel) is { } t) t.Background = StatusBrush(st);
    }

    static IBrush StatusBrush(string st)
    {
        var c = Color.Parse(Session.StatusColors[st]);
        return new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
    }

    void ApplyFilter()
    {
        if (_session == null) return;
        foreach (var rel in _session.Files) UpdateItem(rel);
        foreach (var f in _folders)
            if (f.Parent is TreeViewItem) f.IsVisible = f.Items.OfType<TreeViewItem>().Any(c => c.IsVisible);
    }

    List<string> VisibleFiles() => _session?.Files.Where(r => _treeItems[r].IsVisible).ToList() ?? [];

    public void SelectRel(string rel)
    {
        if (_treeItems.TryGetValue(rel, out var it))
        {
            _tree.SelectedItem = it;
            it.BringIntoView();
        }
        if (rel != _current) ShowImage(rel);
    }

    void Step(int d)
    {
        var files = VisibleFiles();
        if (files.Count == 0) return;
        int i = _current != null && files.Contains(_current) ? files.IndexOf(_current) + d : 0;
        if (i >= 0 && i < files.Count) SelectRel(files[i]);
    }

    void NextUnreviewed()
    {
        if (_session == null) return;
        var files = _session.Files;
        int start = _current != null ? files.IndexOf(_current) + 1 : 0;
        foreach (var rel in files.Skip(start).Concat(files.Take(start)))
            if (_session.Status(rel) is not (Session.Pass or Session.Fail) && _treeItems[rel].IsVisible)
            {
                SelectRel(rel);
                return;
            }
        Status("全部图片均已判定");
    }

    void UpdateCounts()
    {
        if (_session == null) { _counts.Text = ""; return; }
        var c = _session.Counts();
        _counts.Text = $"共 {_session.Files.Count} 张  " + string.Join("  ", Session.StatusText.Select(kv => $"{kv.Value} {c[kv.Key]}"));
    }

    // ------------------------------------------------------------------ image display
    void ShowImage(string rel)
    {
        _btnDraw.IsChecked = false;
        _current = rel;
        _selectedDefect = null;
        _gray?.Dispose();
        _gray = null;
        _bmpOriginal = _bmpEnhanced = null;
        try
        {
            _gray = ImageIO.ReadGray(Path.Combine(_session!.Root, rel));
            _bmpOriginal = Bitmaps.FromGray(_gray);
        }
        catch (Exception e)
        {
            _viewer.ClearOverlays();
            _viewer.SetImage(null);
            _info.Inlines = [new Run(rel) { FontWeight = FontWeight.Bold }, new LineBreak(), new Run(e.Message) { Foreground = Brushes.Red }];
            UpdateEnabled();
            return;
        }
        _viewer.SetImage(ModeBitmap());
        _comment.Text = _session.Rec(rel).Comment;
        RefreshDefects();
        FillThumbs(rel);
        UpdateEnabled();
    }

    Bitmap? ModeBitmap()
    {
        if (_gray == null) return null;
        if (_mode == "original") return _bmpOriginal;
        if (_bmpEnhanced == null)
        {
            using var c = ImageIO.Clahe(_gray);
            _bmpEnhanced = Bitmaps.FromGray(c);
        }
        return _bmpEnhanced;
    }

    void SetMode(string mode)
    {
        _mode = mode;
        _viewer.SetImage(ModeBitmap(), keepView: true);
        _tabs.SelectedIndex = mode == "wire" ? 1 : 0;
        RefreshDefects();
    }

    void RefreshDefects()
    {
        if (_session == null || _current == null) return;
        var rel = _current;
        var r = _session.Rec(rel);
        _viewer.ClearOverlays();
        _viewer.Defects.AddRange(r.Defects);
        _viewer.SelectedDefect = _selectedDefect;
        _viewer.ShowMarks = _chkMarks.IsChecked == true;
        if (_mode == "wire") FillWireOverlay(r);
        _viewer.Refresh();

        var srcNames = new Dictionary<string, string> { ["auto"] = "算法", ["manual"] = "手动" };
        var stNames = new Dictionary<string, string> { ["pending"] = "待确认", ["accepted"] = "已确认", ["rejected"] = "误报" };
        _defectTable.SetRows(r.Defects.Select(d =>
        {
            var gray = d.Status == "rejected" ? Brush.Parse("#999999") : null;
            return new TableRow([d.Type, d.Score.ToString("F2"), d.Source == "manual" ? "手动" : d.By.Length > 0 ? d.By : srcNames.GetValueOrDefault(d.Source, d.Source),
                stNames.GetValueOrDefault(d.Status, d.Status)], [gray ?? Brush.Parse(DefectTypes.Color(d.Type)), gray, gray, gray], d.Id);
        }));
        _defectTable.SelectTag(_selectedDefect);
        FillWireTable(r);
        RefreshInfo();
        if (_btnDraw.IsChecked != true)
            ShowBanner(r.Product != null && _product != null && r.Product != _product.Name
                ? $"本图结果来自产品「{r.Product}」，当前产品是「{_product.Name}」；点“重新检测全部”可按当前产品重新检测" : null, "#d97706");
    }

    void FillWireOverlay(ImageRecord r)
    {
        var units = r.Units ?? [];
        var wires = r.Wires ?? [];
        double um = r.PixelUm > 0 ? r.PixelUm : 1;
        foreach (var u in units)
        {
            bool bad = wires.Any(w => w.Unit == u.Index && w.Bad);
            var missing = wires.Any(w => w.Unit == u.Index && w.Flags.Contains("MISSING"));
            _viewer.Units.Add(new UnitMark(new Rect(u.Box[0], u.Box[1], u.Box[2], u.Box[3]), Color.Parse(bad ? WireBad : "#67d69c"),
                $"单元 {u.Index + 1}{(missing ? " · 漏线" : bad ? " · 异常" : "")}"));
        }
        for (int i = 0; i < wires.Count; i++)
        {
            var w = wires[i];
            var color = Color.Parse(w.Bad ? WireBad : WireOk);
            var tip = $"单元 {w.Unit + 1} 线 {w.Index + 1}  长度 {w.L * um:F0} µm  sweep {w.SweepPct:F1}%  偏移 {w.ShiftPx * um:F0} µm  z {w.ZAll:F1}"
                      + (w.Bad ? "\n" + CsvReport.FlagText(w.Flags) : "");
            var label = w.Bad ? string.Join("、", w.Flags.Take(2).Select(f => DefectTypes.FromFlag[f])) : null;
            if (w.Flags.Contains("MISSING"))
                _viewer.Wires.Add(new WireMark([new Point(w.P1[0], w.P1[1]), new Point(w.P2[0], w.P2[1])], color, 2.5, tip, i, label, true));
            else
                _viewer.Wires.Add(new WireMark(w.Curve.Select(p => new Point(p[0], p[1])).ToList(), color, w.Bad ? 2.3 : 1.8, tip, i, label));
            _viewer.Dots.Add(new DotMark(new Point(w.Ball[0], w.Ball[1]), 2.5, Color.Parse(w.Flags.Contains("BOND1") ? WireBad : "#ffb000")));
            _viewer.Dots.Add(new DotMark(new Point(w.P2[0], w.P2[1]), 2.5, Color.Parse(w.Flags.Contains("BOND2") ? WireBad : "#d070ff")));
        }
    }

    void FillWireTable(ImageRecord r)
    {
        var wires = r.Wires ?? [];
        double um = r.PixelUm > 0 ? r.PixelUm : 1;
        var red = Brush.Parse("#e02424");
        _wireTable.SetRows(wires.Select((w, i) =>
        {
            var c = w.Bad ? red : null;
            return new TableRow([(w.Unit + 1).ToString(), (w.Index + 1).ToString(), (w.L * um).ToString("F0"), w.SweepPct.ToString("F1"),
                (w.ShiftPx * um).ToString("F0"), w.ZAll.ToString("F1"), w.Bad ? CsvReport.FlagText(w.Flags) : "正常"],
                [c, c, c, c, c, c, c], i, $"z_ball {w.ZBall:F1}  起点缺口 {w.Gap1 * um:F0} µm  终点缺口 {w.Gap2 * um:F0} µm  中段缺口 {w.GapMid * um:F0} µm");
        }));
        if (wires.Count == 0)
            _wireInfo.Text = r.DetectedAt == null ? "本图还没有检测结果：点“开始检测”" : "本图没有找到单元（检查产品是否选对、模板匹配阈值）";
        else
        {
            int bad = wires.Count(w => w.Bad);
            _wireInfo.Inlines =
            [
                new Run($"单元 {r.Units?.Count ?? 0} 个，导线 "), new Run(wires.Count.ToString()) { FontWeight = FontWeight.Bold },
                new Run(" 条，其中 "), new Run(bad.ToString()) { FontWeight = FontWeight.Bold, Foreground = red },
                new Run($" 条异常。中位长度 {Inspector.Median(wires.Select(w => w.L * um)):F0} µm，中位 sweep {Inspector.Median(wires.Select(w => w.SweepPct)):F1}%。"),
            ];
        }
    }

    void RefreshInfo()
    {
        if (_session == null || _current == null) return;
        var rel = _current;
        var r = _session.Rec(rel);
        var st = _session.Status(rel);
        var inl = new InlineCollection
        {
            new Run(rel) { FontWeight = FontWeight.Bold }, new LineBreak(),
            new Run($"第 {_session.Files.IndexOf(rel) + 1} / {_session.Files.Count} 张"), new LineBreak(),
            new Run("状态: "), new Run(Session.StatusText[st]) { FontWeight = FontWeight.Bold, Foreground = Brush.Parse(Session.StatusColors[st]) },
        };
        if (r.Width > 0)
        {
            inl.Add(new LineBreak());
            inl.Add(new Run($"尺寸: {r.Width}×{r.Height}   产品: {r.Product}  {r.PixelUm:G4} µm/px"));
        }
        if (r.Error != null)
        {
            inl.Add(new LineBreak());
            inl.Add(new Run($"检测出错: {r.Error}") { Foreground = Brushes.Red });
        }
        if (r.Verdict != null)
        {
            inl.Add(new LineBreak());
            inl.Add(new Run($"判定人: {(r.Reviewer.Length > 0 ? r.Reviewer : "-")}  {r.ReviewedAt}"));
        }
        _info.Inlines = inl;
    }

    void SelectDefect(string id, bool center)
    {
        _selectedDefect = id;
        _viewer.SelectedDefect = id;
        _defectTable.SelectTag(id);
        if (center && _session != null && _current != null && _session.Rec(_current).Defects.FirstOrDefault(d => d.Id == id) is { } d)
            _viewer.CenterOn(new Rect(d.X0, d.Y0, d.X1 - d.X0, d.Y1 - d.Y0));
        _viewer.Refresh();
    }

    void SelectWireRow()
    {
        if (_wireTable.SelectedTag is not int i || _session == null || _current == null) return;
        if (_mode != "wire") _modeButtons["wire"].IsChecked = true;
        _viewer.SelectedWire = i;
        var w = _session.Rec(_current).Wires?.ElementAtOrDefault(i);
        if (w != null)
        {
            var pts = w.Curve.Count > 0 ? w.Curve : [w.P1, w.P2];
            double x0 = pts.Min(p => p[0]), y0 = pts.Min(p => p[1]), x1 = pts.Max(p => p[0]), y1 = pts.Max(p => p[1]);
            _viewer.CenterOn(new Rect(x0, y0, Math.Max(x1 - x0, 8), Math.Max(y1 - y0, 8)), 1);
        }
        _viewer.Refresh();
    }

    // ------------------------------------------------------------------ editing
    void EditDefects(Func<List<Defect>, List<Defect>> fn)
    {
        if (_session == null || _current == null || _selectedDefect == null) return;
        _session.SetDefects(_current, fn(_session.Rec(_current).Defects));
        RefreshDefects();
        UpdateItem(_current);
        UpdateCounts();
    }

    void SetDefectStatus(string status) => EditDefects(ds =>
    {
        foreach (var d in ds.Where(d => d.Id == _selectedDefect)) d.Status = d.Status == status ? "pending" : status;
        return ds;
    });

    void DeleteDefect()
    {
        EditDefects(ds => ds.Where(d => d.Id != _selectedDefect).ToList());
        _selectedDefect = null;
    }

    void ChangeType()
    {
        var t = _typeCombo.SelectedItem as string ?? DefectTypes.Other;
        EditDefects(ds =>
        {
            foreach (var d in ds.Where(d => d.Id == _selectedDefect)) (d.Type, d.Status) = (t, "accepted");
            return ds;
        });
    }

    void ToggleDraw(bool on)
    {
        if (on && _current == null) { _btnDraw.IsChecked = false; return; }
        _viewer.SetTool(on ? ImageViewer.Tool.DrawBox : ImageViewer.Tool.Pan);
        if (on) ShowBanner("手动框选：在图上拖框标出缺陷，类型取右侧下拉框（Esc 取消）");
        else RefreshDefects();
    }

    void BoxDrawn(Rect r)
    {
        if (_session == null || _current == null || _btnDraw.IsChecked != true) return;
        var d = new Defect
        {
            X0 = (int)r.X, Y0 = (int)r.Y, X1 = (int)r.Right, Y1 = (int)r.Bottom, Type = _typeCombo.SelectedItem as string ?? DefectTypes.Other,
            Score = 1, Source = "manual", Status = "accepted",
        };
        _session.SetDefects(_current, [.. _session.Rec(_current).Defects, d]);
        _selectedDefect = d.Id;
        _btnDraw.IsChecked = false;
        RefreshDefects();
        UpdateItem(_current);
        UpdateCounts();
    }

    void SetVerdict(string? v)
    {
        if (_session == null || _current == null) return;
        _session.SetVerdict(_current, v, _operator.Text ?? "");
        UpdateItem(_current);
        UpdateCounts();
        RefreshInfo();
        if (v == null || _chkAutoNext.IsChecked != true) return;
        var files = VisibleFiles();
        if (files.Contains(_current) && files.IndexOf(_current) + 1 < files.Count) Step(1);
        else if (!files.Contains(_current)) NextUnreviewed();
    }

    // ------------------------------------------------------------------ thumbnails
    Border? ThumbItem(string rel) => (_thumbs.ItemsSource as List<Border>)?.FirstOrDefault(b => b.Tag as string == rel);

    void FillThumbs(string rel)
    {
        var folder = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
        if (_thumbFolder != folder)
        {
            _thumbFolder = folder;
            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            var items = new List<Border>();
            var todo = new List<(string rel, Image img)>();
            foreach (var f in _session!.Files.Where(f => (f.Contains('/') ? f[..f.LastIndexOf('/')] : "") == folder))
            {
                var img = new Image { Width = 140, Height = 90, Stretch = Stretch.Uniform };
                if (_thumbCache.TryGetValue(f, out var bmp)) img.Source = bmp;
                else todo.Add((f, img));
                items.Add(new Border
                {
                    Tag = f, Width = 150, Height = 118, Padding = new Thickness(3), Background = StatusBrush(_session.Status(f)),
                    Child = new StackPanel { Children = { img, new TextBlock { Text = f[(f.LastIndexOf('/') + 1)..], FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center } } },
                });
            }
            _thumbs.ItemsSource = items;
            var root = _session.Root;
            var token = _thumbCts.Token;
            _ = Task.Run(() =>
            {
                foreach (var (f, img) in todo)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        using var g = ImageIO.ReadGray(Path.Combine(root, f));
                        var bmp = Bitmaps.Thumbnail(g);
                        Dispatcher.UIThread.Post(() => { _thumbCache[f] = bmp; img.Source = bmp; });
                    }
                    catch (Exception) { }
                }
            }, token);
        }
        var it = ThumbItem(rel);
        _thumbs.SelectedItem = it;
        if (it != null) _thumbs.ScrollIntoView(it);
    }

    // ------------------------------------------------------------------ detection
    public async Task StartDetection(bool rerunAll)
    {
        if (_session == null || _running) return;
        if (_product == null)
        {
            await Dialogs.Info(this, "提示", "请先在工具栏选择产品，或点“新建产品”。");
            return;
        }
        var s = _session;
        var todo = rerunAll ? s.Files.ToList() : s.Files.Where(r => !s.HasDetection(r)).ToList();
        if (rerunAll && s.Files.Any(s.HasDetection)
            && !await Dialogs.Confirm(this, "确认", "重新检测会替换算法标记（手动标记、已复核的标记和判定保留），继续？"))
            return;
        if (todo.Count == 0)
        {
            Status("所有图片已检测完毕；如需重新检测请点“重新检测全部”。");
            return;
        }
        await RunBatch(todo);
    }

    async Task RunBatch(List<string> rels)
    {
        var s = _session!;
        var product = _product!;
        _running = true;
        _detectCts = new CancellationTokenSource();
        var token = _detectCts.Token;
        UpdateEnabled();
        _progress.IsVisible = _progressText.IsVisible = true;
        _progress.Maximum = rels.Count;
        _progress.Value = 0;
        _progressText.Text = $"检测中 0 / {rels.Count}";
        int done = 0, n = _cfg.Workers > 0 ? _cfg.Workers : Math.Max(1, Environment.ProcessorCount - 1);
        bool finished = true;
        try
        {
            await Task.Run(() => Parallel.ForEach(rels, new ParallelOptions { MaxDegreeOfParallelism = n, CancellationToken = token }, rel =>
            {
                Dispatcher.UIThread.Post(() => { _busy.Add(rel); UpdateItem(rel); });
                ImageResult? res = null;
                string? err = null;
                try
                {
                    using var g = ImageIO.ReadGray(Path.Combine(s.Root, rel));
                    res = Inspector.Detect(g, product);
                }
                catch (Exception e) { err = $"{e.GetType().Name}: {e.Message}"; }
                Dispatcher.UIThread.Post(() => OnResult(rel, res, err, ++done, rels.Count));
            }), token);
        }
        catch (OperationCanceledException) { finished = false; }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        _busy.Clear();
        _running = false;
        _progress.IsVisible = _progressText.IsVisible = false;
        s.Save();
        ApplyFilter();
        UpdateCounts();
        UpdateEnabled();
        Status(finished ? "检测完成" : "检测已停止，可再次点“开始检测”继续");
    }

    void OnResult(string rel, ImageResult? res, string? err, int k, int total)
    {
        if (_session == null) return;
        _busy.Remove(rel);
        if (res != null) _session.SetDetection(rel, res);
        else _session.SetError(rel, err ?? "未知错误");
        UpdateItem(rel);
        _progress.Value = k;
        _progressText.Text = $"检测中 {k} / {total}";
        if (k % 5 == 0 || k == total) UpdateCounts();
        if (_chkFollow.IsChecked == true && rel != _current) SelectRel(rel);
        else if (rel == _current) RefreshDefects();
    }

    // ------------------------------------------------------------------ clear / report / export
    async Task ClearMarks(string scope)
    {
        if (_session == null || _running) return;
        List<string> rels;
        string what;
        if (scope == "image")
        {
            if (_current == null) return;
            (rels, what) = ([_current], $"当前图片「{_current[(_current.LastIndexOf('/') + 1)..]}」");
        }
        else if (scope == "subfolder")
        {
            if (_current == null || !_current.Contains('/')) return;
            var sub = _current[..(_current.LastIndexOf('/') + 1)];
            (rels, what) = (_session.Files.Where(r => r.StartsWith(sub, StringComparison.Ordinal)).ToList(), $"子文件夹「{sub.TrimEnd('/')}」");
        }
        else (rels, what) = (_session.Files.ToList(), "整个文件夹");
        int n = rels.Sum(r => _session.Rec(r).Defects.Count);
        var (btn, verdicts) = await Dialogs.Show(this, "清除标记",
            $"将清除{what}共 {rels.Count} 张图片的全部 {n} 个缺陷标记（包括手动框选的标记），图片恢复为“未检测”。\n此操作不能撤销，确定继续？",
            ["确定", "取消"], "同时清除 PASS / FAIL 判定", "取消");
        if (btn != "确定") return;
        _session.ClearMarks(rels, verdicts);
        _session.Save();
        _selectedDefect = null;
        ApplyFilter();
        UpdateCounts();
        RefreshDefects();
        Status($"已清除{what}的标记（{rels.Count} 张图片）");
    }

    async Task SaveReport()
    {
        if (_session == null) return;
        var name = Path.GetFileName(_session.Root.TrimEnd('/', '\\'));
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存报告", SuggestedFileName = $"{name}_报告_{DateTime.Now:yyyyMMdd_HHmm}.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_session.Root),
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            _session.Save();
            var (sum, det) = CsvReport.Write(_session, path);
            await Dialogs.Info(this, "保存报告", $"已保存：\n{sum}\n{det}");
        }
        catch (Exception e) { await Dialogs.Info(this, "保存失败", e.Message); }
    }

    async Task ExportWires()
    {
        if (_session == null || _current == null || (_session.Rec(_current).Wires?.Count ?? 0) == 0)
        {
            await Dialogs.Info(this, "导线统计", "当前图片还没有导线数据，请先检测。");
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出导线统计", SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_current)}_导线.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_session.Root),
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try
        {
            CsvReport.WriteWires(path, _session.Rec(_current));
            Status($"已导出 {_session.Rec(_current).Wires!.Count} 条导线: {path}");
        }
        catch (Exception e) { await Dialogs.Info(this, "导出失败", e.Message); }
    }
}
