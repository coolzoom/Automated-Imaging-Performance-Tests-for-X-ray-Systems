using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

namespace WireInspect.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow();
            desktop.MainWindow = w;
            var folder = desktop.Args?.FirstOrDefault(Directory.Exists);
            if (folder != null) w.Opened += (_, _) => w.OpenFolder(folder);
            if (WireInspect.Core.License.Required())
                w.Opened += async (_, _) =>
                {
                    var lic = await LicenseDialog.Obtain(w);
                    if (lic == null) w.Close();
                    else w.SetLicense(lic);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `WireInspect.App --mcp-stdio [--gui] [--port N] [--folder DIR]`: MCP over stdin/stdout for AI agents,
        // no window. --gui bridges to the HTTP endpoint of a running instance.
        if (args.Contains("--mcp-stdio", StringComparer.OrdinalIgnoreCase))
            return WireInspect.Mcp.McpHost.RunStdio(args, "1.0.0");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions
        {
            DefaultFamilyName = OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : OperatingSystem.IsMacOS() ? "PingFang SC" : null,
        })
        .LogToTrace();
}
