using OpenCvSharp;
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

    [Theory]
    [InlineData(4)]
    [InlineData(-7)]
    [InlineData(35)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(-100)]
    public void RotatedImage_AnyAngle(double deg)
    {
        using var good = Make();
        var product = MakeProduct(good);
        using var img = Rotate(good, deg);
        var r = Inspector.Detect(img, product);
        Assert.InRange((r.Angle - deg + 540) % 360 - 180, -0.3, 0.3);
        Assert.Equal(9, r.Units.Count);
        Assert.True(r.Wires.All(w => !w.Bad), Bad(r));
        Assert.True(r.Wires.All(w => w.ShiftPx < 1));
    }

    [Fact]
    public void RotatedMissingWire_FoundInPlace()
    {
        using var good = Make();
        using var miss = Make(new Opts { Missing = (4, 1) });
        using var img = Rotate(miss, 137);
        var r = Inspector.Detect(img, MakeProduct(good));
        var bad = Assert.Single(r.Wires, w => w.Bad);
        Assert.Equal((4, 1), (bad.Unit, bad.Index));
        Assert.Contains("MISSING", bad.Flags);
        var d = Assert.Single(r.Defects);   // in original image coordinates: covers where the wire should be
        Assert.InRange(bad.P1[0], d.X0, d.X1);
        Assert.InRange(bad.P1[1], d.Y0, d.Y1);
    }

    public static TheoryData<string> MixedCases => ["none", "missing", "broken", "sweep"];

    [Theory]
    [MemberData(nameof(MixedCases))]
    public void UnitsInDifferentDirections(string defect)
    {
        double[] angles = [0, 35, 90, -120, 180, 15, -60, 145, 270];
        var (o, target, flag) = defect switch
        {
            "missing" => (new Opts { Missing = (4, 1) }, (4, 1), "MISSING"),
            "broken" => (new Opts { Broken = (2, 0) }, (2, 0), "BROKEN"),
            "sweep" => (new Opts { Sweep = (6, 0) }, (6, 0), "SWEEP"),
            _ => (new Opts(), (-1, -1), ""),
        };
        using var good = Make();
        using var img = MakeMixed(angles, o);
        var r = Inspector.Detect(img, MakeProduct(good));
        Assert.Equal(9, r.Units.Count);
        for (int i = 0; i < 9; i++)   // image row order = drawing order
            Assert.InRange((r.Units[i].Angle - angles[i] + 540) % 360 - 180, -0.5, 0.5);
        var bad = r.Wires.Where(w => w.Bad).ToList();
        if (flag == "") Assert.True(bad.Count == 0, Bad(r));
        else
        {
            var b = Assert.Single(bad);
            Assert.Equal(target, (b.Unit, b.Index));
            Assert.Contains(flag, b.Flags);
        }
    }

    [Fact]
    public void AlignmentOff_KeepsTranslation()
    {
        using var good = Make();
        var p = MakeProduct(good);
        p.LocalAlign = false;
        p.MaxAngleDeg = 0;
        var r = Inspector.Detect(good, p);
        Assert.Equal(0, r.Angle);
        Assert.Equal([1, 0, Ox, 0, 1, Oy], r.Units[0].Affine!);
    }

    [Theory]
    [InlineData(0.03)]
    [InlineData(-0.03)]
    public void DistortedImage_AllOk(double k)
    {
        using var good = Make();
        using var img = Barrel(good, k);
        var r = Inspector.Detect(img, MakeProduct(good));
        Assert.Equal(9, r.Units.Count);
        Assert.True(r.Wires.All(w => !w.Bad), Bad(r));
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(0.8)]
    public void ScaledImage_UnitsFoundAndWiresOk(double s)
    {
        using var good = Make();
        var p = MakeProduct(good);
        p.MaxAngleDeg = 0;
        p.ScaleRange = [0.7, 1.5];
        using var img = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Resize(good, img, new OpenCvSharp.Size(0, 0), s, s, OpenCvSharp.InterpolationFlags.Area);
        var r = Inspector.Detect(img, p);
        Assert.Equal(9, r.Units.Count);
        Assert.InRange(r.Scale, s - 0.05, s + 0.05);
        Assert.Equal(18, r.Wires.Count);
        Assert.True(r.Wires.All(w => !w.Bad), Bad(r));
        // wire ends must land on the zoomed picture: affine carries the scale
        Assert.InRange(r.Units[0].Box[0], Synthetic.Ox * s - 3, Synthetic.Ox * s + 3);
    }

    [Fact]
    public void PixelParams()
    {
        var a = new Product { CorridorPx = 7, EndTolPx = 10, ShiftMaxPx = 3.5, WireWidthPx = 2.5 }.Params();
        Assert.Equal((7, 3, 4, 2, 6, 7), (a.HalfW, a.BgOff, a.BgW, a.D, a.Ext, a.Along));
        Assert.Equal(10, a.EndTol, 6);
        Assert.Equal(3.5, a.ShiftMax, 6);
        var b = new Product { CorridorPx = 14, WireWidthPx = 5 }.Params();
        Assert.Equal(14, b.HalfW);
        Assert.True(b.Along > a.Along && b.D >= a.D);
    }

    [Fact]
    public void OldMicrometreProduct_IsConverted()
    {
        var p = Product.FromJson("""{"name":"old","pixel_um":5,"wire_diam_um":25,"corridor_um":70,"end_tol_um":100,"gap_max_um":80,"shift_max_um":35,"align_search_um":50}""");
        Assert.Equal((5.0, 14.0, 20.0, 16.0, 7.0, 10.0), (p.WireWidthPx, p.CorridorPx, p.EndTolPx, p.GapMaxPx, p.ShiftMaxPx, p.AlignSearchPx));
        Assert.DoesNotContain("pixel_um", System.Text.Json.JsonSerializer.Serialize(p, Json.Options));
    }

    [Fact]
    public void MirroredUnits_Found()
    {
        using var good = Make();
        var product = MakeProduct(good);
        using var miss = Make(new Opts { Missing = (4, 1) });
        using var img = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Flip(miss, img, OpenCvSharp.FlipMode.Y);
        var r = Inspector.Detect(img, product);
        Assert.Equal(9, r.Units.Count);
        Assert.True(r.Units.All(u => u.Mirror));
        var bad = Assert.Single(r.Wires, w => w.Bad);
        Assert.Contains("MISSING", bad.Flags);
        product.AllowMirror = false;   // only rotations: a 180° turn fits, but clearly worse than the mirror
        var r2 = Inspector.Detect(img, product);
        Assert.DoesNotContain(r2.Units, u => u.Mirror);
        Assert.True(r.Units.Min(u => u.Match) > r2.Units.Max(u => u.Match));
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
    public void SlopeWire_IsMeasured_AndStepIsNot()
    {
        var a = new Product { WireWidthPx = 2.5, CorridorPx = 7, EndTolPx = 30, GapMaxPx = 40, ZBall = 0 }.Params();
        var wire = Inspector.InspectWire(Ramp(false), new Point2d(12, 24), new Point2d(77, 24), a);
        var edge = Inspector.InspectWire(Ramp(true), new Point2d(12, 24), new Point2d(77, 24), a);
        Assert.DoesNotContain("MISSING", wire.Flags);
        Assert.Contains("MISSING", edge.Flags);
    }

    static FImage Ramp(bool step)
    {
        const int h = 48, w = 90, y0 = 24;
        var img = new FImage(w, h);
        var rnd = new Random(1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = step ? (y >= y0 ? 190 : 70) : 70 + 5.0 * (y - y0);
                if (!step && x >= 12 && x < 78)
                {
                    int dy = y - y0;
                    if (dy == 0) v -= 16;
                    else if (Math.Abs(dy) == 1) v -= 7;
                }
                v += rnd.NextDouble() * 6 - 3;
                img[y, x] = (float)v;
            }
        return img;
    }
}
