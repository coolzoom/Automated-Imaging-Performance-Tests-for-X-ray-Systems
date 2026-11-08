using System.Globalization;
using System.Net;
using System.Text;
using OpenCvSharp;

namespace WireInspect.Core;

/// <summary>Self-contained interactive HTML report (same layout as the Python version / app-gpt).
/// Each image is a card: embedded (or linked) JPEG with an SVG overlay of unit boxes,
/// wire curves and defect boxes; header checkboxes toggle marks/labels/defect types.</summary>
public static class HtmlReport
{
    static readonly Dictionary<string, string> VerdictClass = new()
    {
        [Session.Pass] = "pass", [Session.Fail] = "fail", [Session.Suspect] = "suspect",
        [Session.AutoOk] = "ok", [Session.Unchecked] = "none",
    };
    static readonly Dictionary<string, string> Src = new() { ["auto"] = "算法", ["manual"] = "手动", ["import"] = "导入" };
    static readonly Dictionary<string, string> State = new() { ["pending"] = "待确认", ["accepted"] = "已确认", ["rejected"] = "误报" };

    static string E(object? s) => WebUtility.HtmlEncode(s?.ToString() ?? "");
    static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

    const string CSS = """
    *{box-sizing:border-box}body{font-family:-apple-system,"PingFang SC","Microsoft YaHei",sans-serif;margin:0;background:#f2f3f5;color:#222}
    header{background:#1f2933;color:#fff;padding:14px 22px}h1{margin:0 0 6px;font-size:22px}
    .meta{font-size:13px;color:#cbd2d9}.stats{display:flex;flex-wrap:wrap;gap:10px;margin-top:10px}
    .stat{background:#323f4b;border-radius:6px;padding:6px 12px;font-size:13px}.stat b{font-size:18px;margin-left:4px}
    .controls{position:sticky;top:0;z-index:5;background:#fff;border-bottom:1px solid #ddd;padding:10px 22px;display:flex;flex-wrap:wrap;gap:18px;align-items:center;font-size:14px}
    .controls fieldset{border:1px solid #ddd;border-radius:6px;padding:4px 10px;margin:0}.controls legend{font-size:12px;color:#666}
    .controls label{margin-right:10px;white-space:nowrap;cursor:pointer}.sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:3px}
    .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(460px,1fr));gap:16px;padding:18px 22px}
    .card{background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.12);overflow:hidden;border-top:5px solid #9aa5b1}
    .card.pass{border-top-color:#18a848}.card.fail{border-top-color:#e02424}.card.suspect{border-top-color:#ff9900}.card.ok{border-top-color:#7fc8a0}
    .ch{display:flex;justify-content:space-between;gap:8px;padding:8px 12px;font-size:13px;align-items:center}.ch .path{word-break:break-all}
    .badge{font-weight:bold;color:#fff;border-radius:4px;padding:2px 8px;white-space:nowrap}.badge.pass{background:#18a848}.badge.fail{background:#e02424}
    .badge.suspect{background:#ff9900}.badge.ok{background:#7fc8a0}.badge.none{background:#9aa5b1}
    .fig{position:relative;background:#111;cursor:zoom-in}.fig img{display:block;width:100%}.fig svg{position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none}
    .mk rect{fill:none;stroke-width:3;vector-effect:non-scaling-stroke}.mk.pending.auto rect{stroke-dasharray:6 4}
    .mk text{font-size:var(--fs,22px);font-weight:bold;paint-order:stroke;stroke:#000;stroke-width:3px}
    .wk polyline{fill:none;stroke-width:2.5;vector-effect:non-scaling-stroke}
    .wk circle{fill:none;stroke-width:2;vector-effect:non-scaling-stroke}
    .wk text{font-size:var(--fs,20px);font-weight:bold;paint-order:stroke;stroke:#000;stroke-width:3px}
    .ub rect{fill:none;stroke-width:1.5;stroke-dasharray:8 4;vector-effect:non-scaling-stroke}
    .ub text{font-size:var(--fs,18px);paint-order:stroke;stroke:#000;stroke-width:3px}
    .cb{padding:8px 12px;font-size:13px}.cb table{border-collapse:collapse;width:100%}.cb td,.cb th{border-bottom:1px solid #eee;padding:3px 4px;text-align:left}
    .cb .cm{margin-top:6px;color:#444;white-space:pre-wrap}.muted{color:#888}
    body.hide-marks .mk,body.hide-marks .wk,body.hide-marks .ub{display:none}body.hide-labels .mk text,body.hide-labels .wk text,body.hide-labels .ub text{display:none}
    #lb{display:none;position:fixed;inset:0;background:rgba(0,0,0,.9);z-index:10;align-items:center;justify-content:center;cursor:zoom-out}
    #lb.on{display:flex}#lb .fig{max-width:96vw;max-height:94vh;cursor:zoom-out}#lb .fig img{max-width:96vw;max-height:94vh;width:auto}
    @media print{.controls{display:none}.card{break-inside:avoid}}
    """;

    const string JS = """
    const B=document.body;
    function tog(cls,on){B.classList.toggle(cls,!on)}
    document.querySelectorAll('[data-type]').forEach(c=>c.onchange=()=>tog('hide-t'+c.dataset.type,c.checked));
    function applyFilter(){
      const v=document.querySelector('input[name=vf]:checked').value, q=document.getElementById('q').value.trim().toLowerCase();
      let n=0;document.querySelectorAll('.card').forEach(c=>{
        const ok=(v==='all'||c.dataset.status===v||(v==='review'&&c.dataset.status!=='pass'&&c.dataset.status!=='fail'))&&(!q||c.dataset.path.toLowerCase().includes(q));
        c.style.display=ok?'':'none';if(ok)n++;});
      document.getElementById('shown').textContent=n;
    }
    document.querySelectorAll('input[name=vf]').forEach(r=>r.onchange=applyFilter);document.getElementById('q').oninput=applyFilter;
    const lb=document.getElementById('lb');
    document.querySelectorAll('.card .fig').forEach(f=>f.onclick=()=>{lb.innerHTML='';lb.appendChild(f.cloneNode(true));lb.classList.add('on')});
    lb.onclick=()=>lb.classList.remove('on');document.onkeydown=e=>{if(e.key==='Escape')lb.classList.remove('on')};
    applyFilter();
    """;

    static int TypeIndex(string t) => Array.IndexOf(DefectTypes.All, t) is var i && i >= 0 ? i : DefectTypes.All.Length;

    static byte[] EncodeJpeg(Mat gray, int side = 1600, int quality = 80)
    {
        using var g = gray;
        Mat small = g;
        if (Math.Max(g.Width, g.Height) > side)
        {
            double f = side / (double)Math.Max(g.Width, g.Height);
            small = g.Resize(new Size(Math.Max(1, (int)(g.Width * f)), Math.Max(1, (int)(g.Height * f))), interpolation: InterpolationFlags.Area);
        }
        try
        {
            Cv2.ImEncode(".jpg", small, out var buf, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
            return buf;
        }
        finally
        {
            if (!ReferenceEquals(small, g)) small.Dispose();
        }
    }

    static (string src, int w, int h) ImageSrc(Session s, string rel, string outDir, bool embed)
    {
        var path = Path.Combine(s.Root, rel);
        var r = s.Rec(rel);
        if (embed)
        {
            var g = ImageIO.ReadGray(path);
            int w = g.Width, h = g.Height;
            var jpg = EncodeJpeg(g);
            return ("data:image/jpeg;base64," + Convert.ToBase64String(jpg), w, h);
        }
        if (r.Width <= 0)
        {
            using var g = ImageIO.ReadGray(path);
            return (RelLink(path, outDir), g.Width, g.Height);
        }
        return (RelLink(path, outDir), r.Width, r.Height);
    }

    static string RelLink(string path, string outDir)
        => Path.GetRelativePath(outDir, path).Replace(Path.DirectorySeparatorChar, '/');

    static string Card(Session s, string rel, string outDir, bool embed, int idx)
    {
        var r = s.Rec(rel);
        var st = s.Status(rel);
        var cls = VerdictClass[st];
        var (src, W, H) = ImageSrc(s, rel, outDir, embed);
        var live = s.LiveDefects(rel);
        var wires = r.Wires ?? [];
        var units = r.Units ?? [];
        var fs = Math.Max(14, Math.Max(W, H) / 60);
        var mk = new StringBuilder();
        foreach (var u in units)
            mk.Append(CultureInfo.InvariantCulture,
                $"""<g class="ub"><rect x="{u.Box[0]}" y="{u.Box[1]}" width="{u.Box[2]}" height="{u.Box[3]}" stroke="#31b8ff"/>""" +
                $"""<text x="{u.Box[0] + 4}" y="{u.Box[1] + Math.Max(18, fs)}" fill="#31b8ff">{u.Index + 1}</text></g>""");
        foreach (var w in wires)
        {
            var c = w.Bad ? "#ff4d4d" : "#3fd070";
            var curve = w.Curve.Count > 0 ? w.Curve : [w.P1, w.P2];
            var pts = string.Join(' ', curve.Select(p => FormattableString.Invariant($"{p[0]:F1},{p[1]:F1}")));
            var lbl = $"单元{w.Unit + 1} 线{w.Index + 1} {(w.Bad ? CsvReport.FlagText(w.Flags) : "OK")}";
            mk.Append(CultureInfo.InvariantCulture,
                $"""<g class="wk{(w.Bad ? " bad" : "")}"><polyline points="{pts}" stroke="{c}"/>""" +
                $"""<circle cx="{w.Ball[0]:F1}" cy="{w.Ball[1]:F1}" r="4" stroke="{c}"/>""" +
                $"""<text x="{w.P1[0] + 4:F1}" y="{w.P1[1] - 6:F1}" fill="{c}">{E(lbl)}</text></g>""");
        }
        foreach (var d in live)
        {
            var c = DefectTypes.Color(d.Type);
            var lbl = d.Type + (d.Source == "manual" ? "" : $" {d.Score:F2}");
            mk.Append(CultureInfo.InvariantCulture,
                $"""<g class="mk t-{TypeIndex(d.Type)} {d.Status} {d.Source}"><rect x="{d.X0}" y="{d.Y0}" width="{d.X1 - d.X0}" height="{d.Y1 - d.Y0}" stroke="{c}"/>""" +
                $"""<text x="{d.X0 + 2}" y="{Math.Max(fs, d.Y0 - 4)}" fill="{c}">{E(lbl)}</text></g>""");
        }
        var bad = wires.Where(w => w.Bad).ToList();
        var table = new StringBuilder();
        if (bad.Count > 0)
        {
            table.Append("""<table><tr><th>单元</th><th>线</th><th>缺陷</th><th>z</th><th>sweep</th><th>偏移px</th></tr>""");
            foreach (var w in bad)
                table.Append(CultureInfo.InvariantCulture,
                    $"""<tr><td>{w.Unit + 1}</td><td>{w.Index + 1}</td><td>{E(CsvReport.FlagText(w.Flags))}</td><td>{w.ZAll:F1}</td><td>{w.SweepPct:F1}%</td><td>{w.ShiftPx:F1}</td></tr>""");
            table.Append("</table>");
        }
        if (live.Count > 0)
        {
            table.Append("""<table><tr><th>类型</th><th>分数</th><th>来源</th><th>状态</th><th>位置(像素)</th></tr>""");
            foreach (var d in live)
                table.Append(CultureInfo.InvariantCulture,
                    $"""<tr><td><span class="sw" style="background:{DefectTypes.Color(d.Type)}"></span>{E(d.Type)}</td><td>{d.Score:F2}</td><td>{E(Src.GetValueOrDefault(d.Source, d.Source))}</td>""" +
                    $"""<td>{E(State.GetValueOrDefault(d.Status, d.Status))}</td><td class="muted">{d.X0},{d.Y0}-{d.X1},{d.Y1}</td></tr>""");
            table.Append("</table>");
        }
        if (table.Length == 0) table.Append("""<div class="muted">无缺陷标记</div>""");
        var meta = $"单元 {units.Count} · 导线 {wires.Count} · NG <b style=\"color:#c00\">{bad.Count}</b>";
        if (r.Product is { } prod) meta += $" · {E(prod)}";
        if (r.Error is { } err) meta += $" · <b style=\"color:#c00\">错误: {E(err)}</b>";
        var who = r.Verdict != null
            ? $"""<div class="muted">判定: {E(r.Reviewer.Length > 0 ? r.Reviewer : "-")} {E(r.ReviewedAt)}</div>""" : "";
        var badge = st is Session.Pass or Session.Fail ? Session.StatusText[st] : $"未判定({Session.StatusText[st]})";
        return $"""
            <div class="card {cls}" data-status="{cls}" data-path="{E(rel)}"><div class="ch"><span class="path">{idx}. {E(rel)}</span>
            <span class="badge {cls}">{badge}</span></div><div class="fig"><img loading="lazy" src="{src}" alt="">
            <svg viewBox="0 0 {W} {H}" preserveAspectRatio="none" style="--fs:{fs}px">{mk}</svg></div>
            <div class="cb"><div class="muted">{meta} · 检测 {E(r.DetectedAt ?? "-")}</div>{table}
            {(r.Comment.Length > 0 ? $"""<div class="cm">备注: {E(r.Comment)}</div>""" : "")}{who}</div></div>
            """;
    }

    /// <summary>Write the interactive HTML report for `rels` to `path`. Returns path.</summary>
    public static string WriteHtml(Session s, IReadOnlyList<string> rels, string path, bool embed = true)
    {
        var outDir = Path.GetFullPath(Path.GetDirectoryName(path) ?? ".");
        var c = Session.StatusText.Keys.ToDictionary(k => k, _ => 0);
        var typeCount = new Dictionary<string, int>();
        var ngWires = 0;
        foreach (var rel in rels)
        {
            c[s.Status(rel)]++;
            ngWires += s.Rec(rel).Wires?.Count(w => w.Bad) ?? 0;
            foreach (var d in s.LiveDefects(rel)) typeCount[d.Type] = typeCount.GetValueOrDefault(d.Type) + 1;
        }
        var reviewed = c[Session.Pass] + c[Session.Fail];
        var stats = new List<(string k, string v)>
        {
            ("图片总数", rels.Count.ToString()), ("PASS", c[Session.Pass].ToString()), ("FAIL", c[Session.Fail].ToString()),
            ("未判定", (rels.Count - reviewed).ToString()), ("其中疑似", c[Session.Suspect].ToString()),
            ("NG导线", ngWires.ToString()),
            ("不良率", reviewed > 0 ? (100.0 * c[Session.Fail] / reviewed).ToString("F1", CultureInfo.InvariantCulture) + "%" : "-"),
        };
        stats.AddRange(typeCount.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value.ToString())));
        var present = DefectTypes.All.Where(typeCount.ContainsKey)
            .Concat(typeCount.Keys.Where(t => !DefectTypes.All.Contains(t)).OrderBy(t => t)).ToList();
        var typeBoxes = present.Count > 0
            ? string.Concat(present.Select(t => $"""<label><input type="checkbox" checked data-type="{TypeIndex(t)}"><span class="sw" style="background:{DefectTypes.Color(t)}"></span>{E(t)} ({typeCount[t]})</label>"""))
            : """<span class="muted">无</span>""";
        var typeCss = string.Concat(Enumerable.Range(0, DefectTypes.All.Length + 1).Select(i => $"body.hide-t{i} .t-{i}{{display:none}}"));
        var filters = new[] { ("all", "全部"), ("fail", "FAIL"), ("pass", "PASS"), ("suspect", "疑似未判定"), ("review", "未判定") };
        var vf = string.Concat(filters.Select(f => $"""<label><input type="radio" name="vf" value="{f.Item1}"{(f.Item1 == "all" ? " checked" : "")}>{f.Item2}</label>"""));
        var cards = string.Concat(rels.Select((rel, i) => Card(s, rel, outDir, embed, i + 1)));
        var products = rels.Select(rel => s.Rec(rel).Product).Where(p => p != null).Distinct().OrderBy(p => p);
        var statsHtml = string.Concat(stats.Select(kv => $"""<div class="stat">{E(kv.k)}<b>{kv.v}</b></div>"""));
        var doc = $$"""
            <!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
            <title>X-Ray 焊线检测报告 - {{E(Path.GetFileName(s.Root.TrimEnd('/', '\\')))}}</title><style>{{CSS}}{{typeCss}}</style></head><body>
            <header><h1>X-Ray 焊线检测报告</h1><div class="meta">文件夹: {{E(s.Root)}}<br>操作员: {{E(s.Data.Operator.Length > 0 ? s.Data.Operator : "-")}} · 生成时间: {{DateTime.Now:yyyy-MM-dd HH:mm:ss}} · 产品: {{E(string.Join("、", products) is { Length: > 0 } p ? p : "-")}}</div>
            <div class="stats">{{statsHtml}}</div></header>
            <div class="controls">
            <fieldset><legend>标记显示</legend><label><input type="checkbox" checked onchange="tog('hide-marks',this.checked)">显示标记</label>
            <label><input type="checkbox" checked onchange="tog('hide-labels',this.checked)">显示标签文字</label></fieldset>
            <fieldset><legend>缺陷类型</legend>{{typeBoxes}}</fieldset>
            <fieldset><legend>筛选</legend>{{vf}}</fieldset>
            <label>搜索文件 <input id="q" type="search" placeholder="文件名"></label><span>显示 <b id="shown"></b> / {{rels.Count}}</span>
            </div><div class="grid">{{cards}}</div><div id="lb"></div><script>{{JS}}</script></body></html>
            """;
        File.WriteAllText(path, doc, new UTF8Encoding(false));
        return path;
    }
}
