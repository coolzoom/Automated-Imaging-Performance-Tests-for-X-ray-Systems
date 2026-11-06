// usage: xval <out.json> <productDir> <img>...    -> detect each image, AutoWires on template, CalibratePitch(1.5mm)
//        xval session <folder>                      -> load session.json, print status per file, re-save as session_cs.json
using System.Text.Json;
using WireInspect.Core;

if (args[0] == "session")
{
    var s = new Session(args[1]);
    var st = s.Files.ToDictionary(f => f, f => new { status = s.Status(f), live = s.LiveDefects(f).Count, pending = s.HasPending(f),
        wires = s.Rec(f).Wires?.Count ?? 0, bad = s.Rec(f).Wires?.Count(w => w.Bad) ?? 0 });
    Console.WriteLine(JsonSerializer.Serialize(st, Json.Options));
    CsvReport.Write(s, Path.Combine(args[1], "report_cs.csv"));
    s.SetVerdict(s.Files[0], Session.Fail, "cs");
    s.Save();
    return 0;
}
var product = Product.Load(args[1]);
var outp = new Dictionary<string, object?>();
foreach (var path in args.Skip(2))
{
    using var g = ImageIO.ReadGray(path);
    outp[path] = Inspector.Detect(g, product);
}
outp["__auto_wires"] = AutoWires.Find(product.Template!, product);
outp["__params"] = product.Params();
using (var g0 = ImageIO.ReadGray(args[2])) outp["__pitch"] = AutoWires.CalibratePitch(g0, product.Template!, product.MatchThr, 1.5);
File.WriteAllText(args[0], JsonSerializer.Serialize(outp, Json.Options));
return 0;
