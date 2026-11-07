using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WireInspect.Core;

namespace WireInspect.App;

/// <summary>Shows the machine code, imports a .lic file and optionally starts the 7-day demo.</summary>
public sealed class LicenseDialog : Window
{
    public License? Result { get; private set; }

    /// <summary><paramref name="trial"/>: null (no trial button), "available", or a message why not.</summary>
    public LicenseDialog(string reason, string? trial)
    {
        Title = "软件授权";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 560;
        Background = Brush.Parse("#0e2138");

        var text = reason + "\n\n";
        if (trial == "available") text += $"· 先体验：点“试用 {License.TrialDays} 天演示版”直接使用。\n· 正式使用：";
        var msg = new TextBlock { Text = text + "把下面的机器码发给供应商，收到授权文件（.lic）后点“导入授权文件”。", TextWrapping = TextWrapping.Wrap };

        var code = new TextBox
        {
            Text = License.MachineCode(), IsReadOnly = true, FontSize = 18, FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        var copy = new Button { Content = "复制" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null) await Clipboard.SetTextAsync(code.Text!);
            copy.Content = "已复制";
        };
        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "机器码", VerticalAlignment = VerticalAlignment.Center }, code, copy } };
        code.HorizontalAlignment = HorizontalAlignment.Stretch;

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (trial != null)
        {
            var t = new Button { Content = $"试用 {License.TrialDays} 天演示版", IsEnabled = trial == "available" };
            if (trial != "available") ToolTip.SetTip(t, trial);
            t.Click += async (_, _) => await StartTrial();
            btns.Children.Add(t);
        }
        var imp = new Button { Content = "导入授权文件…", IsDefault = true };
        imp.Click += async (_, _) => await ImportFile();
        var quit = new Button { Content = trial != null ? "退出" : "取消" };
        quit.Click += (_, _) => Close();
        btns.Children.Add(imp);
        btns.Children.Add(quit);

        Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children = { msg, codeRow, btns } };
    }

    async Task StartTrial()
    {
        if (!await Dialogs.Confirm(this, "演示版",
            $"演示版从现在起可使用 {License.TrialDays} 天，期间可检测、复核和保存报告。\n演示期只能开始一次，确定开始吗？"))
            return;
        try { Result = License.StartTrial(); }
        catch (LicenseError e) { await Dialogs.Info(this, "演示版", e.Message); return; }
        Close();
    }

    async Task ImportFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择授权文件", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("授权文件") { Patterns = ["*.lic"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        try { Result = License.Install(files[0].Path.LocalPath); }
        catch (LicenseError e) { await Dialogs.Info(this, "授权", e.Message); return; }
        await Dialogs.Info(this, "授权", "授权成功：" + Result.Summary());
        Close();
    }

    /// <summary>A valid license file wins; otherwise a running demo period; otherwise ask. Null if the user quits.</summary>
    public static async Task<License?> Obtain(Window owner)
    {
        string reason;
        try { return License.Check(); }
        catch (LicenseError e) { reason = e.Message; }
        var (state, value) = License.TrialStatus();
        if (state == "active") return (License)value!;
        if (state == "ended" && !File.Exists(License.LicensePath())) reason = (string)value!;
        var dlg = new LicenseDialog(reason, state == "available" ? "available" : (string?)value);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }
}
