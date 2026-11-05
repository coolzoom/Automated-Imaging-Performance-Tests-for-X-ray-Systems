using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using WireInspect.App;
using WireInspect.Core;

[assembly: AvaloniaTestApplication(typeof(WireInspect.UiTests.TestAppBuilder))]

namespace WireInspect.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .With(new FontManagerOptions { DefaultFamilyName = OperatingSystem.IsMacOS() ? "PingFang SC" : "Microsoft YaHei UI" });
}

/// <summary>Smoke test of the whole GUI on a real image: open a folder, detect, show results; the rendered
/// frames are written to imgtest/out/ui_*.png for a visual check against the app-gpt layout.</summary>
public class WindowTests
{
    const string RealImage = "/Users/mac/Downloads/2026-11-03-13-56-22/2_8_4.jpg";
    static readonly string Repo = FindRepo();

    static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "products"))) return d.FullName;
        throw new DirectoryNotFoundException("imgtest/products");
    }

    static string Home()
    {
        var home = Path.Combine(Path.GetTempPath(), "wi_ui_home");
        if (Directory.Exists(home)) Directory.Delete(home, true);
        CopyDir(Path.Combine(Repo, "products"), Path.Combine(home, "products"));
        Environment.SetEnvironmentVariable("WIREINSPECT_HOME", home);
        File.WriteAllText(Path.Combine(home, "config.json"), "{\"product\": \"2_8_4_demo\", \"operator\": \"测试员\"}");
        return home;
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static void Save(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Directory.CreateDirectory(Path.Combine(Repo, "out"));
        frame?.Save(Path.Combine(Repo, "out", name));
    }

    [AvaloniaFact]
    public async Task MainWindow_DetectsFolderAndShowsResults()
    {
        if (!File.Exists(RealImage)) return;
        var home = Home();
        var data = Path.Combine(home, "images");
        Directory.CreateDirectory(Path.Combine(data, "lot1"));
        File.Copy(RealImage, Path.Combine(data, "lot1", "2_8_4.jpg"));
        var product = Product.Load(Path.Combine(home, "products", "2_8_4_demo"));
        using (var g = ImageIO.ReadGray(RealImage))
        {
            var units = Inspector.FindUnits(g, product.Template!, product.MatchThr);
            using var sim = Simulator.Apply(g, product, units, 1, 4, 7);
            ImageIO.WritePng(Path.Combine(data, "lot1", "2_8_4_sim.png"), sim);
            ImageIO.WritePng(Path.Combine(data, "2_8_4_copy.png"), g);
        }

        var w = new MainWindow { WindowState = WindowState.Normal, Width = 1680, Height = 980 };
        w.Show();
        await w.OpenFolderAsync(data);
        await w.StartDetection(false);
        w.SelectRel("lot1/2_8_4_sim.png");
        Save(w, "ui_main.png");

        var s = new Session(data);
        Assert.Equal(3, s.Files.Count);
        Assert.Equal(Session.Suspect, s.Status("lot1/2_8_4_sim.png"));
        var types = s.Rec("lot1/2_8_4_sim.png").Defects.Select(d => d.Type).ToList();
        Assert.Contains(DefectTypes.Missing, types);
        Assert.Contains(DefectTypes.Broken, types);
        Assert.Contains(DefectTypes.Sweep, types);
        w.Close();
    }

    [AvaloniaFact]
    public void ProductDialog_EditShowsTemplateAndWires()
    {
        if (!File.Exists(RealImage)) return;
        var home = Home();
        var product = Product.Load(Path.Combine(home, "products", "2_8_4_demo"));
        var dlg = new ProductDialog(product, ImageIO.ReadGray(RealImage)) { Width = 1500, Height = 950 };
        dlg.Show();
        Save(dlg, "ui_product.png");
        dlg.Close();
    }
}
