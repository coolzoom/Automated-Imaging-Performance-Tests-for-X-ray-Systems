using System.Globalization;
using OpenCvSharp;
using WireInspect.Core;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

const string Usage = """
用法:
  wireinspect detect <图片> [--product 名称] [--out 目录] [--simulate 缺线单元 断线单元 弯线单元] [--sim-sweep-px 5] [--rotate 度]
  wireinspect make-product --image <图片> --box x,y,w,h --name 名称 [--wire x1,y1,x2,y2 ...] [--auto]
      --wire 用整图坐标；不给 --wire 时用 --auto 自动找线
  wireinspect list
""";

if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
var opts = ParseOpts(args.Skip(1).ToArray(), out var positional);
try
{
    switch (args[0])
    {
        case "list":
            foreach (var n in Product.List()) Console.WriteLine(n);
            return 0;
        case "make-product":
            return MakeProduct(opts);
        case "detect" when positional.Count == 1:
            return Detect(positional[0], opts);
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"错误: {e.Message}");
    return 2;
}

static int MakeProduct(Dictionary<string, List<string>> o)
{
    using var gray = ImageIO.ReadGray(o["image"][0]);
    var b = o["box"][0].Split(',').Select(int.Parse).ToArray();
    var p = new Product
    {
        Name = o["name"][0],
        Template = new Mat(gray, new Rect(b[0], b[1], b[2], b[3])).Clone(),
    };
    if (o.TryGetValue("wire", out var ws))
        foreach (var w in ws)
        {
            var v = w.Split(',').Select(double.Parse).ToArray();
            p.Wires.Add(new WireSpec(v[0] - b[0], v[1] - b[1], v[2] - b[0], v[3] - b[1]));
        }
    else if (o.ContainsKey("auto"))
        p.Wires = AutoWires.Find(p.Template, p);
    p.Save();
    Console.WriteLine($"已保存产品 {p.Name}: {p.Dir}，{p.Wires.Count} 根线");
    foreach (var w in p.Wires) Console.WriteLine($"  ({w.P1[0]:F1},{w.P1[1]:F1}) -> ({w.P2[0]:F1},{w.P2[1]:F1})  L={w.Length:F1}px");
    return 0;
}

static int Detect(string path, Dictionary<string, List<string>> o)
{
    var cfg = AppConfig.Load();
    var name = o.TryGetValue("product", out var pn) ? pn[0] : cfg.Product;
    if (string.IsNullOrEmpty(name)) name = Product.List().FirstOrDefault() ?? throw new Exception("没有产品，请先 make-product");
    var product = Product.Load(Path.Combine(AppPaths.Products, name));
    var outDir = o.TryGetValue("out", out var od) ? od[0] : Path.Combine(AppPaths.Root, "out");
    Directory.CreateDirectory(outDir);
    var stem = Path.GetFileNameWithoutExtension(path);

    var gray = ImageIO.ReadGray(path);
    if (o.TryGetValue("simulate", out var sim))
    {
        var units = Inspector.FindUnits(gray, product.Template!, product.MatchThr);
        var px = o.TryGetValue("sim-sweep-px", out var sp) ? double.Parse(sp[0]) : 5.0;
        var s = sim.Select(int.Parse).ToArray();
        var simImg = Simulator.Apply(gray, product, units, s[0], s[1], s[2], px);
        gray.Dispose();
        gray = simImg;
        stem += "_sim";
        ImageIO.WritePng(Path.Combine(outDir, stem + ".png"), gray);
    }
    if (o.TryGetValue("rotate", out var rot) && double.Parse(rot[0]) != 0)   // test aid: rotate the picture (counter-clockwise, corners cut off)
    {
        var rotated = Align.RotateInscribed(gray, double.Parse(rot[0]));
        gray.Dispose();
        gray = rotated;
        stem += "_rot";
        ImageIO.WritePng(Path.Combine(outDir, stem + ".png"), gray);
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var res = Inspector.Detect(gray, product);
    sw.Stop();
    Console.WriteLine($"产品 {product.Name}  单元 {res.Units.Count}  导线 {res.Wires.Count}  旋转 {res.Angle:F1}°  用时 {sw.ElapsedMilliseconds} ms");
    var lines = new List<string> { "unit,wire,x,y,match,result,z_all,z_seg,z_ball,sweep_px,sweep_pct,shift_px,gap1,gap2,L" };
    foreach (var w in res.Wires)
    {
        var u = res.Units[w.Unit];
        var row = string.Join(",", w.Unit, w.Index, u.Box[0], u.Box[1], u.Match.ToString("F2"),
            w.Bad ? string.Join("|", w.Flags) : "OK", w.ZAll.ToString("F1"),
            string.Join(" ", w.ZSeg.Select(z => z.ToString("F1"))), w.ZBall.ToString("F1"),
            w.SweepPx.ToString("F2"), w.SweepPct.ToString("F1"), w.ShiftPx.ToString("F2"),
            w.Gap1.ToString("F0"), w.Gap2.ToString("F0"), w.L.ToString("F0"));
        lines.Add(row);
        Console.WriteLine(row.Replace(",", "  "));
    }
    File.WriteAllLines(Path.Combine(outDir, stem + "_result.csv"), lines, new System.Text.UTF8Encoding(true));
    using var annot = Annotator.Draw(gray, res, 2);
    ImageIO.WritePng(Path.Combine(outDir, stem + "_annot.png"), annot);
    Console.WriteLine($"NG: {res.Wires.Count(w => w.Bad)}/{res.Wires.Count}  -> {Path.Combine(outDir, stem + "_annot.png")}");
    gray.Dispose();
    return 0;
}

static Dictionary<string, List<string>> ParseOpts(string[] a, out List<string> positional)
{
    var d = new Dictionary<string, List<string>>();
    positional = [];
    string? key = null;
    foreach (var s in a)
    {
        if (s.StartsWith("--"))
        {
            key = s[2..];
            if (!d.ContainsKey(key)) d[key] = [];
        }
        else if (key != null) d[key].Add(s);
        else positional.Add(s);
    }
    return d;
}
