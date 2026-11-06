using WireInspect.Core;
using static WireInspect.Tests.Synthetic;

namespace WireInspect.Tests;

public class InspectorTests
{
    static ImageResult Run(Opts? o = null)
    {
        using var good = Make();
        var product = MakeProduct(good);
        using var img = o == null ? good.Clone() : Make(o);
        return Inspector.Detect(img, product);
    }

    static string Bad(ImageResult r) => string.Join("; ", r.Wires.Where(w => w.Bad).Select(w => $"u{w.Unit}w{w.Index}:{string.Join("|", w.Flags)}"));

    [Fact]
    public void GoodImage_AllWiresOk()
    {
        var r = Run();
        Assert.Equal(9, r.Units.Count);
        Assert.Equal(18, r.Wires.Count);
        Assert.True(r.Wires.All(w => !w.Bad), Bad(r));
        Assert.Empty(r.Defects);
    }

    [Fact]
    public void MissingWire_OnlyThatWireIsMissing()
    {
        var r = Run(new Opts { Missing = (4, 1) });
        var bad = r.Wires.Where(w => w.Bad).ToList();
        Assert.Single(bad);
        Assert.Equal((4, 1), (bad[0].Unit, bad[0].Index));
        Assert.Contains("MISSING", bad[0].Flags);
        Assert.Equal(DefectTypes.Missing, Assert.Single(r.Defects).Type);
    }

    [Fact]
    public void BrokenWire_IsBroken()
    {
        var r = Run(new Opts { Broken = (2, 0) });
        var target = r.Wires.First(w => w.Unit == 2 && w.Index == 0);
        Assert.True(target.Bad, $"z_seg={string.Join(" ", target.ZSeg.Select(z => z.ToString("F1")))} gap_mid={target.GapMid}");
        var bad = Assert.Single(r.Wires, w => w.Bad);
        Assert.Equal((2, 0), (bad.Unit, bad.Index));
        Assert.Contains("BROKEN", bad.Flags);
    }

    [Fact]
    public void SweptWire_IsSweepAndMeasured()
    {
        var r = Run(new Opts { Sweep = (6, 0), SweepPx = 6 });
        var bad = Assert.Single(r.Wires, w => w.Bad);
        Assert.Equal((6, 0), (bad.Unit, bad.Index));
        Assert.Contains("SWEEP", bad.Flags);
        Assert.InRange(bad.SweepPx, 5.0, 7.0);
    }

    [Fact]
    public void ShiftedImage_StillMatchesWithStandaloneTemplate()
    {
        var r = Run(new Opts { ShiftX = 7, ShiftY = 5 });
        Assert.Equal(9, r.Units.Count);
        Assert.True(r.Wires.All(w => !w.Bad), Bad(r));
        Assert.Equal(Ox + 7, r.Units[0].Box[0]);
    }

    [Fact]
    public void PixelParams_FollowMicrometres()
    {
        var p = new Product { PixelUm = 10, CorridorUm = 70, EndTolUm = 100, ShiftMaxUm = 35, WireDiamUm = 25 };
        var a = p.Params();
        Assert.Equal((7, 3, 4, 2, 6, 7), (a.HalfW, a.BgOff, a.BgW, a.D, a.Ext, a.Along));
        Assert.Equal(10, a.EndTol, 6);
        Assert.Equal(3.5, a.ShiftMax, 6);
        var b = new Product { PixelUm = 5, CorridorUm = 70, WireDiamUm = 25 }.Params();
        Assert.Equal(14, b.HalfW);
        Assert.True(b.Along > a.Along && b.D >= a.D);
    }

    [Fact]
    public void AutoWires_FindsBothWires()
    {
        using var good = Make();
        var p = MakeProduct(good);
        var found = AutoWires.Find(p.Template!, p);
        Assert.Equal(2, found.Count);
        foreach (var truth in Wires)
        {
            // direction (which end is the ball) is offered to the user for confirmation, so either way is fine here
            var w = found.Select(f => f.P1[0] < f.P2[0] ? f : f.Reversed())
                .OrderBy(f => Math.Abs(f.P1[1] - truth.P1[1]) + Math.Abs(f.P2[1] - truth.P2[1])).First();
            Assert.InRange(Math.Abs(w.P1[0] - truth.P1[0]), 0, 6);
            Assert.InRange(Math.Abs(w.P1[1] - truth.P1[1]), 0, 3);
            Assert.InRange(Math.Abs(w.P2[0] - truth.P2[0]), 0, 8);
            Assert.InRange(Math.Abs(w.P2[1] - truth.P2[1]), 0, 3);
        }
    }

    [Fact]
    public void CalibratePitch_FromUnitSpacing()
    {
        using var good = Make();
        var p = MakeProduct(good);
        var um = AutoWires.CalibratePitch(good, p.Template!, p.MatchThr, 1.5);
        Assert.NotNull(um);
        Assert.Equal(1500.0 / PitchX, um!.Value, 3);
    }
}
