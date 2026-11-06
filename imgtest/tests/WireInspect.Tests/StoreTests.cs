using WireInspect.Core;

namespace WireInspect.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("wi_test_").FullName;
    public void Dispose() => Directory.Delete(Path, true);
}

public class StoreTests
{
    [Fact]
    public void Product_SaveLoad_RoundTrip()
    {
        using var tmp = new TempDir();
        using var gray = Synthetic.Make();
        var p = Synthetic.MakeProduct(gray);
        p.Name = "型号 A/1";
        p.CorridorPx = 7.5;
        p.Save(tmp.Path);
        Assert.Equal(["型号 A_1"], Product.List(tmp.Path));
        var q = Product.Load(Path.Combine(tmp.Path, "型号 A_1"));
        Assert.Equal("型号 A/1", q.Name);
        Assert.Equal(7.5, q.CorridorPx);
        Assert.Equal(2, q.Wires.Count);
        Assert.Equal([Synthetic.UnitW, Synthetic.UnitH], q.TplSize);
        Assert.Equal(Synthetic.UnitW, q.Template!.Width);
    }

    [Fact]
    public void Session_DetectionKeepsManualAndReviewed_StatusAndPersist()
    {
        using var tmp = new TempDir();
        using (var gray = Synthetic.Make(new Synthetic.Opts { Missing = (0, 0) }))
            ImageIO.WritePng(Path.Combine(tmp.Path, "a.png"), gray);
        File.WriteAllBytes(Path.Combine(tmp.Path, "empty.jpg"), []);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "sub"));
        using (var gray = Synthetic.Make())
            ImageIO.WritePng(Path.Combine(tmp.Path, "sub", "b.png"), gray);

        var s = new Session(tmp.Path);
        Assert.Equal(["a.png", "sub/b.png"], s.Files);
        Assert.Equal(["empty.jpg"], s.Empty);
        Assert.Equal(Session.Unchecked, s.Status("a.png"));

        using var good = Synthetic.Make();
        var product = Synthetic.MakeProduct(good);
        using (var g = ImageIO.ReadGray(Path.Combine(tmp.Path, "a.png"))) s.SetDetection("a.png", Inspector.Detect(g, product));
        using (var g = ImageIO.ReadGray(Path.Combine(tmp.Path, "sub", "b.png"))) s.SetDetection("sub/b.png", Inspector.Detect(g, product));
        Assert.Equal(Session.Suspect, s.Status("a.png"));
        Assert.Equal(Session.AutoOk, s.Status("sub/b.png"));
        Assert.True(s.HasPending("a.png"));

        var auto = s.Rec("a.png").Defects.Single();
        auto.Status = "rejected";
        s.Rec("a.png").Defects.Add(new Defect { X0 = 1, Y0 = 1, X1 = 9, Y1 = 9, Source = "manual", Status = "accepted" });
        Assert.Equal(Session.Suspect, s.Status("a.png"));
        using (var g = ImageIO.ReadGray(Path.Combine(tmp.Path, "a.png"))) s.SetDetection("a.png", Inspector.Detect(g, product));
        var ds = s.Rec("a.png").Defects;
        Assert.Equal(2, ds.Count);   // reviewed auto box kept, the same new box dropped, manual kept
        Assert.Contains(ds, d => d.Id == auto.Id && d.Status == "rejected");

        s.SetVerdict("sub/b.png", Session.Pass, "张三");
        s.Save();
        var s2 = new Session(tmp.Path);
        Assert.Equal(Session.Pass, s2.Status("sub/b.png"));
        Assert.Equal("张三", s2.Rec("sub/b.png").Reviewer);
        Assert.Equal(18, s2.Rec("a.png").Wires!.Count);

        var (sum, det) = CsvReport.Write(s2, Path.Combine(tmp.Path, "report.csv"));
        Assert.Equal(3, File.ReadAllLines(sum).Length);
        Assert.Equal(1 + 36, File.ReadAllLines(det).Length);

        s2.ClearMarks(["a.png"]);
        Assert.Equal(Session.Unchecked, s2.Status("a.png"));
    }
}
