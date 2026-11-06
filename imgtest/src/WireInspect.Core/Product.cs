using System.Text.Json.Serialization;
using OpenCvSharp;

namespace WireInspect.Core;

public sealed class WireSpec
{
    public double[] P1 { get; set; } = [0, 0];   // bond 1 (chip side), template coordinates
    public double[] P2 { get; set; } = [0, 0];   // bond 2 (lead side)

    public WireSpec() { }
    public WireSpec(double x1, double y1, double x2, double y2) { P1 = [x1, y1]; P2 = [x2, y2]; }
    public WireSpec Reversed() => new(P2[0], P2[1], P1[0], P1[1]);
    [JsonIgnore] public double Length => Math.Sqrt(Math.Pow(P2[0] - P1[0], 2) + Math.Pow(P2[1] - P1[1], 2));
}

/// <summary>Pixel-unit parameters of one inspection, derived from a product's µm settings.</summary>
public sealed record PixelParams(int HalfW, int BgOff, int BgW, int D, int Ext, int Along, int NSeg, double SelZ,
    double OnZ, double ZMissing, double ZSeg, double ZBall, double EndTol, double GapMax, double SweepMaxPct,
    double ShiftMax, double MatchThr, bool Dark);

/// <summary>One product: a unit template image (tpl.png) plus its wires and thresholds (product.json).</summary>
public sealed class Product
{
    public const string TemplateFile = "tpl.png", ConfigFile = "product.json";

    public string Name { get; set; } = "";
    public double PixelUm { get; set; } = 10.0;
    public int[] TplSize { get; set; } = [0, 0];
    public List<WireSpec> Wires { get; set; } = [];
    public double MatchThr { get; set; } = 0.6;
    public string Polarity { get; set; } = "dark";
    public double WireDiamUm { get; set; } = 25;
    public double CorridorUm { get; set; } = 70;
    public double EndTolUm { get; set; } = 100;
    public double GapMaxUm { get; set; } = 80;
    public double ShiftMaxUm { get; set; } = 35;
    public double SweepMaxPct { get; set; } = 6.0;
    public double ZMissing { get; set; } = 6.0;
    public double ZSeg { get; set; } = 2.5;
    public double ZBall { get; set; } = 2.5;
    public int NSeg { get; set; } = 5;
    public string Note { get; set; } = "";

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public Mat? Template { get; set; }

    public double Px(double um) => um / PixelUm;
    public double Um(double px) => px * PixelUm;

    public PixelParams Params()
    {
        double wpx = Math.Max(0.5, WireDiamUm / PixelUm);
        int along = Math.Max(5, (int)Math.Round(wpx * 2.8));
        if (along % 2 == 0) along++;
        return new PixelParams(
            HalfW: Math.Max(3, (int)Math.Round(Px(CorridorUm))),
            BgOff: Math.Max(2, (int)Math.Ceiling(wpx)),
            BgW: Math.Max(3, (int)Math.Ceiling(wpx * 1.5)),
            D: Math.Max(2, (int)Math.Round(wpx * 0.8)),
            Ext: Math.Max(4, (int)Math.Ceiling(wpx * 2.4)),
            Along: along, NSeg: Math.Max(3, NSeg), SelZ: 2.5, OnZ: 1.5,
            ZMissing: ZMissing, ZSeg: ZSeg, ZBall: ZBall,
            EndTol: Px(EndTolUm), GapMax: Math.Max(3, Px(GapMaxUm)), SweepMaxPct: SweepMaxPct, ShiftMax: Px(ShiftMaxUm),
            MatchThr: MatchThr, Dark: Polarity != "bright");
    }

    public Product Clone()
    {
        var p = (Product)MemberwiseClone();
        p.Wires = Wires.Select(w => new WireSpec(w.P1[0], w.P1[1], w.P2[0], w.P2[1])).ToList();
        p.TplSize = [.. TplSize];
        p.Template = Template?.Clone();
        return p;
    }

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var s = new string(name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return s.Length == 0 ? "product" : s;
    }

    public static Product Load(string dir)
    {
        var p = Json.Load<Product>(Path.Combine(dir, ConfigFile));
        p.Dir = dir;
        if (string.IsNullOrEmpty(p.Name)) p.Name = Path.GetFileName(dir);
        var tpl = Path.Combine(dir, TemplateFile);
        if (File.Exists(tpl)) p.Template = ImageIO.ReadGray(tpl);
        return p;
    }

    public void Save(string? productsRoot = null)
    {
        if (Template == null || Template.Empty()) throw new InvalidOperationException("产品没有模板图");
        Dir = Path.Combine(productsRoot ?? AppPaths.Products, SafeName(Name));
        Directory.CreateDirectory(Dir);
        TplSize = [Template.Width, Template.Height];
        ImageIO.WritePng(Path.Combine(Dir, TemplateFile), Template);
        Json.Save(Path.Combine(Dir, ConfigFile), this);
    }

    public static List<string> List(string? productsRoot = null)
    {
        var root = productsRoot ?? AppPaths.Products;
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, ConfigFile)) && File.Exists(Path.Combine(d, TemplateFile)))
            .Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
