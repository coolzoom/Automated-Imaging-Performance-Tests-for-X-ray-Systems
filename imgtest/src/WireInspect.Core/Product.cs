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

/// <summary>Parameters of one inspection derived from a product's settings (all lengths in pixels).</summary>
public sealed record PixelParams(int HalfW, int BgOff, int BgW, int D, int Ext, int Along, int NSeg, double SelZ,
    double OnZ, double ZMissing, double ZSeg, double ZBall, double EndTol, double GapMax, double SweepMaxPct,
    double ShiftMax, double MatchThr, bool Dark, double MaxAngle, bool LocalAlign, int AlignSearch, bool AllowMirror);

/// <summary>One product: a unit template image (tpl.png) plus its wires and thresholds (product.json). Lengths are
/// in pixels of the inspected pictures (the image scale differs between machines, so no µm).</summary>
public sealed class Product
{
    public const string TemplateFile = "tpl.png", ConfigFile = "product.json";

    public string Name { get; set; } = "";
    public int[] TplSize { get; set; } = [0, 0];
    public List<WireSpec> Wires { get; set; } = [];
    public double MatchThr { get; set; } = 0.6;
    public string Polarity { get; set; } = "dark";
    public double WireWidthPx { get; set; } = 2.5;      // wire width on the picture
    public double CorridorPx { get; set; } = 7;         // half width of the search corridor around the expected wire
    public double EndTolPx { get; set; } = 10;          // allowed gap at either end before BOND1 / BOND2
    public double GapMaxPx { get; set; } = 8;           // longest interior gap before BROKEN
    public double ShiftMaxPx { get; set; } = 3.5;       // largest end point offset before SHIFT
    public double SweepMaxPct { get; set; } = 6.0;
    public double ZMissing { get; set; } = 6.0;
    public double ZSeg { get; set; } = 2.5;
    public double ZBall { get; set; } = 2.5;
    public int NSeg { get; set; } = 5;
    public double[] ScaleRange { get; set; } = [0.9, 1.4];   // camera zoom may differ per picture: [min,max] template scale, scanned only when the normal match finds nothing; [1,1] = off
    public double MaxAngleDeg { get; set; } = 180.0;    // units may lie rotated up to ± this (180 = any direction, 0 = upright only)
    public bool LocalAlign { get; set; } = true;        // per-unit affine correction for distortion
    public double AlignSearchPx { get; set; } = 5.0;    // search range of the per-unit block matching
    public bool AllowMirror { get; set; } = true;       // also find mirror-image units (part flipped over)
    public string Note { get; set; } = "";

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public Mat? Template { get; set; }

    public PixelParams Params()
    {
        double wpx = Math.Max(0.5, WireWidthPx);
        int along = Math.Max(5, (int)Math.Round(wpx * 2.8));
        if (along % 2 == 0) along++;
        return new PixelParams(
            HalfW: Math.Max(3, (int)Math.Round(CorridorPx)),
            BgOff: Math.Max(2, (int)Math.Ceiling(wpx)),
            BgW: Math.Max(3, (int)Math.Ceiling(wpx * 1.5)),
            D: Math.Max(2, (int)Math.Round(wpx * 0.8)),
            Ext: Math.Max(4, (int)Math.Ceiling(wpx * 2.4)),
            Along: along, NSeg: Math.Max(3, NSeg), SelZ: 2.5, OnZ: 1.5,
            ZMissing: ZMissing, ZSeg: ZSeg, ZBall: ZBall,
            EndTol: EndTolPx, GapMax: Math.Max(3, GapMaxPx), SweepMaxPct: SweepMaxPct, ShiftMax: ShiftMaxPx,
            MatchThr: MatchThr, Dark: Polarity != "bright", MaxAngle: Math.Max(0.0, MaxAngleDeg), LocalAlign: LocalAlign,
            AlignSearch: Math.Max(2, (int)Math.Round(AlignSearchPx)), AllowMirror: AllowMirror);
    }

    // product.json written before lengths were in pixels: <name>_um / pixel_um
    static readonly (string old, string now, Action<Product, double> set)[] LegacyUm =
    [
        ("wire_diam_um", "wire_width_px", (p, v) => p.WireWidthPx = v), ("corridor_um", "corridor_px", (p, v) => p.CorridorPx = v),
        ("end_tol_um", "end_tol_px", (p, v) => p.EndTolPx = v), ("gap_max_um", "gap_max_px", (p, v) => p.GapMaxPx = v),
        ("shift_max_um", "shift_max_px", (p, v) => p.ShiftMaxPx = v), ("align_search_um", "align_search_px", (p, v) => p.AlignSearchPx = v),
    ];

    public static Product FromJson(string json)
    {
        var p = System.Text.Json.JsonSerializer.Deserialize<Product>(json, Json.Options) ?? new Product();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        double um = root.TryGetProperty("pixel_um", out var pu) && pu.GetDouble() != 0 ? pu.GetDouble() : 10.0;
        foreach (var (old, now, set) in LegacyUm)
            if (!root.TryGetProperty(now, out _) && root.TryGetProperty(old, out var v)) set(p, v.GetDouble() / um);
        return p;
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
        var path = Path.Combine(dir, ConfigFile);
        var p = File.Exists(path) ? FromJson(File.ReadAllText(path)) : new Product();
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
