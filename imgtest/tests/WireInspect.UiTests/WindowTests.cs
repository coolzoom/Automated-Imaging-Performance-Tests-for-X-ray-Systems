using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using OpenCvSharp;
using WireInspect.App;
using Window = Avalonia.Controls.Window;
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

    /// <summary>Detection fixture independent of products/ the user edits: same unit as the original
    /// 2_8_4_demo (tpl [300,140,220,150], one wire), rotation off so the result is deterministic.</summary>
    static string FixtureProduct(string home)
    {
        var p = new Product
        {
            Name = "fixture_2_8_4",
            Wires = [new WireSpec(79, 69.6, 141, 71)],
            MatchThr = 0.6, Polarity = "dark",
            WireWidthPx = 2.5, CorridorPx = 7, EndTolPx = 10, GapMaxPx = 8, ShiftMaxPx = 3.5,
            SweepMaxPct = 6, ZMissing = 6, ZSeg = 2.5, ZBall = 2.5, NSeg = 5,
            MaxAngleDeg = 0, AllowMirror = false,
        };
        using var g = ImageIO.ReadGray(RealImage);
        p.Template = new Mat(g, new OpenCvSharp.Rect(300, 140, 220, 150)).Clone();
        p.Save(Path.Combine(home, "products"));
        return p.Name;
    }

    static void Save(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        Directory.CreateDirectory(Path.Combine(Repo, "out"));
        frame?.Save(Path.Combine(Repo, "out", name));
    }

    static Button ToolButton(Window w, string text) => w.GetLogicalDescendants().OfType<Button>()
        .First(b => b.Content is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text == text));

    static List<string> MenuTexts(Button b)
    {
        b.Flyout!.ShowAt(b);
        Dispatcher.UIThread.RunJobs();
        var items = ((MenuFlyout)b.Flyout).Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();
        b.Flyout.Hide();
        return items;
    }

    [AvaloniaFact]
    public async Task RecentFolders_ListedDedupedAndRecorded()
    {
        var home = Home();
        var a = Path.Combine(home, "lotA");
        var b = Path.Combine(home, "lotB");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        using (var px = new OpenCvSharp.Mat(8, 8, OpenCvSharp.MatType.CV_8U, OpenCvSharp.Scalar.All(100)))
            ImageIO.WritePng(Path.Combine(b, "x.png"), px);   // an empty folder would open a modal "no images" box
        // written by another program (the Python version shares config.json): trailing slash duplicates, a gone folder
        File.WriteAllText(Path.Combine(home, "config.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            recent_folders = new[] { a + "/", a, Path.Combine(home, "gone") },
            product = "2_8_4_demo",
        }));
        var w = new MainWindow { WindowState = WindowState.Normal, Width = 1400, Height = 900 };
        w.Show();
        var recent = ToolButton(w, "最近文件夹");
        var texts = MenuTexts(recent);
        Assert.Equal(2 + 1, texts.Count);   // lotA once, gone (disabled), 清除最近记录
        Assert.StartsWith("lotA", texts[0]);
        Assert.Contains("（不存在）", texts[1]);

        // clicking a menu entry must open that folder
        ((MenuFlyout)recent.Flyout!).Items.OfType<MenuItem>().First(i => i.Header!.ToString()!.StartsWith("lotA"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("lotA", w.Title);

        await w.OpenFolderAsync(b);
        texts = MenuTexts(recent);
        Assert.StartsWith("lotB", texts[0]);
        Assert.StartsWith("lotA", texts[1]);
        var saved = Json.Load<AppConfig>(Path.Combine(home, "config.json")).RecentFolders;
        Assert.Equal([b, a, Path.Combine(home, "gone")], saved);

        // another program adds a folder while this one runs: the menu reads the file again
        var cfg = Json.Load<AppConfig>(Path.Combine(home, "config.json"));
        cfg.RecentFolders.Insert(0, a);
        Json.Save(Path.Combine(home, "config.json"), cfg);
        Assert.StartsWith("lotA", MenuTexts(recent)[0]);
        w.Close();
    }

    [AvaloniaFact]
    public async Task MainWindow_DetectsFolderAndShowsResults()
    {
        if (!File.Exists(RealImage)) return;
        var home = Home();
        var data = Path.Combine(home, "images");
        Directory.CreateDirectory(Path.Combine(data, "lot1"));
        File.Copy(RealImage, Path.Combine(data, "lot1", "2_8_4.jpg"));
        var product = Product.Load(Path.Combine(home, "products", FixtureProduct(home)));
        AppConfig.Update(c => c.Product = product.Name);
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
