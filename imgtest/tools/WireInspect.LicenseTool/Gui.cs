using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using WireInspect.Core;
using System.Text.RegularExpressions;

namespace WireInspect.LicenseTool;

/// <summary>Avalonia port of app-gpt tools/license_tool.py — window for issuing customer licenses (注册机).</summary>
public sealed class GuiApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.MainWindow = new ToolWindow();
        base.OnFrameworkInitializationCompleted();
    }

    public static int Run(string[] args)
        => AppBuilder.Configure<GuiApp>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}

public sealed class ToolWindow : Window
{
    const string Ok = "#15803d", Bad = "#b91c1c";

    byte[]? _secret;
    readonly TextBlock _keyLabel = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox _machine = new() { Watermark = "客户授权窗口里显示的机器码，例如 3F2A-91C0-7B4D-E215" };
    readonly TextBlock _machineHint = new();
    readonly TextBox _customer = new() { Watermark = "显示在客户程序和报告水印里" };
    readonly RadioButton _demo = new() { Content = "演示版（报告带水印）", IsChecked = true, GroupName = "ed" };
    readonly RadioButton _full = new() { Content = "正式版", GroupName = "ed" };
    readonly RadioButton _byDays = new() { Content = "有效天数", IsChecked = true, GroupName = "ex" };
    readonly NumericUpDown _days = new() { Minimum = 1, Maximum = 3650, Value = 30, Increment = 1, Width = 90, FormatString = "0" };
    readonly RadioButton _byDate = new() { Content = "到期日", GroupName = "ex" };
    readonly DatePicker _date = new() { SelectedDate = DateTimeOffset.Now.AddDays(29) };
    readonly TextBlock _expiryHint = new();
    readonly TextBox _outDir = new() { Text = Program.IssuedDir };
    readonly Button _issue = new() { Content = "生成授权文件", IsDefault = true, MinHeight = 34 };
    readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    readonly ListBox _table = new();

    public ToolWindow()
    {
        Title = "WireInspect 授权签发工具（注册机）";
        Width = 780; Height = 660;
        Background = Brush.Parse("#0e2138");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
        int row = 0;
        void Row(string label, Control c)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            l[Grid.RowProperty] = row; l[Grid.ColumnProperty] = 0;
            c[Grid.RowProperty] = row; c[Grid.ColumnProperty] = 1; c.Margin = new Thickness(0, 3);
            grid.Children.Add(l); grid.Children.Add(c);
            row++;
        }

        var machineRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var paste = new Button { Content = "粘贴" };
        paste.Click += async (_, _) =>
        {
#pragma warning disable CS0618 // GetTextAsync is the only text getter on this Avalonia version's IClipboard
            if (Clipboard != null) _machine.Text = await Clipboard.GetTextAsync();
#pragma warning restore CS0618
        };
        _machine.HorizontalAlignment = HorizontalAlignment.Stretch;
        machineRow.Children.Add(_machine); machineRow.Children.Add(paste);
        Row("机器码", machineRow);
        _machineHint[Grid.RowProperty] = row; _machineHint[Grid.ColumnProperty] = 1; grid.Children.Add(_machineHint); row++;
        Row("客户名称", _customer);
        Row("版本", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _demo, _full } });
        Row("有效期", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _byDays, _days, _byDate, _date } });
        _expiryHint[Grid.RowProperty] = row; _expiryHint[Grid.ColumnProperty] = 1; grid.Children.Add(_expiryHint); row++;

        var outRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var choose = new Button { Content = "选择…" };
        choose.Click += async (_, _) =>
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择保存授权文件的文件夹" });
            if (dirs.Count > 0) { _outDir.Text = dirs[0].Path.LocalPath; LoadHistory(); }
        };
        _outDir.HorizontalAlignment = HorizontalAlignment.Stretch;
        outRow.Children.Add(_outDir); outRow.Children.Add(choose);
        Row("保存到", outRow);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var open = new Button { Content = "打开所在文件夹" };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(_outDir.Text!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_outDir.Text!) { UseShellExecute = true });
        };
        _issue.Click += async (_, _) => await Issue();
        btns.Children.Add(_issue); btns.Children.Add(open);

        var historyLabel = new TextBlock { Text = "签发记录（保存在上面文件夹的 签发记录.csv）", Margin = new Thickness(0, 10, 0, 4) };
        var layout = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel { Spacing = 10, Children = { _keyLabel, grid, btns, _result, historyLabel } };
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(_table);
        Content = layout;

        _machine.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Update(); };
        _customer.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Update(); };
        _days.ValueChanged += (_, _) => Update();
        _date.SelectedDateChanged += (_, _) => Update();
        _byDays.IsCheckedChanged += (_, _) => Update();
        _outDir.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) LoadHistory(); };

        LoadKey();
        LoadHistory();
        Update();
    }

    void LoadKey()
    {
        _secret = Program.TryLoadKey(Program.KeyPath, out var err);
        _keyLabel.Text = _secret != null
            ? "● 签名私钥已就绪　本工具含私钥，仅限内部使用，请勿发给客户。"
            : "● " + err;
        _keyLabel.Foreground = Brush.Parse(_secret != null ? Ok : Bad);
    }

    DateOnly Expires() => _byDays.IsChecked == true
        ? DateOnly.FromDateTime(DateTime.Today).AddDays((int)(_days.Value ?? 30) - 1)
        : DateOnly.FromDateTime((_date.SelectedDate ?? DateTimeOffset.Now).Date);

    void Update()
    {
        _days.IsEnabled = _byDays.IsChecked == true;
        _date.IsEnabled = _byDays.IsChecked != true;
        var text = _machine.Text?.Trim() ?? "";
        var code = FindMachine(text);
        _machineHint.Text = text.Length == 0 ? "" : code != null ? "✓ " + code : "格式应为 XXXX-XXXX-XXXX-XXXX（16 位 0-9 / A-F）";
        _machineHint.Foreground = Brush.Parse(code != null || text.Length == 0 ? Ok : Bad);
        var exp = Expires();
        _expiryHint.Text = $"到期日 {exp:yyyy-MM-dd}（当天仍可用，共 {exp.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber + 1} 天）";
        _issue.IsEnabled = _secret != null && code != null && (_customer.Text?.Trim().Length ?? 0) > 0;
    }

    static string? FindMachine(string text)
    {
        var m = Regex.Match(text.ToUpper(), @"[0-9A-F]{4}[\s-]*[0-9A-F]{4}[\s-]*[0-9A-F]{4}[\s-]*[0-9A-F]{4}");
        if (!m.Success) return null;
        var h = Regex.Replace(m.Value, @"[^0-9A-F]", "");
        return string.Join('-', Enumerable.Range(0, 4).Select(i => h.Substring(i * 4, 4)));
    }

    void LoadHistory()
    {
        _table.Items.Clear();
        var log = Path.Combine(_outDir.Text ?? "", "签发记录.csv");
        if (!File.Exists(log)) return;
        var lines = File.ReadAllLines(log).Skip(1).Reverse().Take(50);
        foreach (var line in lines) _table.Items.Add(line);
    }

    async Task Issue()
    {
        var machine = FindMachine(_machine.Text ?? "");
        var customer = _customer.Text?.Trim() ?? "";
        if (_secret == null || machine == null || customer.Length == 0) return;
        var edition = _full.IsChecked == true ? "full" : "demo";
        var exp = Expires();
        if (exp < DateOnly.FromDateTime(DateTime.Today))
        {
            await Msg("注册机", "到期日早于今天。");
            return;
        }
        try
        {
            var path = Program.IssueLicense(_secret, machine, customer, exp, edition, outDir: _outDir.Text);
            _result.Text = $"已生成：{path}\n把这个 .lic 文件发给客户，在授权窗口点“导入授权文件”即可。";
            _result.Foreground = Brush.Parse(Ok);
        }
        catch (Exception e)
        {
            await Msg("注册机", "生成失败：\n" + e.Message);
            return;
        }
        LoadHistory();
    }

    Task Msg(string title, string text)
    {
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0e2138"),
            Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 } } },
        };
        return w.ShowDialog(this);
    }
}
