using System.Globalization;
using System.Text;

namespace WireInspect.Core;

public sealed class ImageRecord
{
    public List<Defect> Defects { get; set; } = [];
    public string? Verdict { get; set; }
    public string Comment { get; set; } = "";
    public string Reviewer { get; set; } = "";
    public string ReviewedAt { get; set; } = "";
    public string? DetectedAt { get; set; }
    public string? Error { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Product { get; set; }
    public double PixelUm { get; set; }
    public List<UnitResult>? Units { get; set; }
    public List<WireResult>? Wires { get; set; }
}

public sealed class SessionData
{
    public string Root { get; set; } = "";
    public string Created { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Saved { get; set; } = "";
    public Dictionary<string, ImageRecord> Images { get; set; } = [];
}

/// <summary>Per-folder inspection state, stored in &lt;folder&gt;/.xray_wire/session.json.</summary>
public sealed class Session
{
    public const string StateDir = ".xray_wire";
    public const string Unchecked = "unchecked", AutoOk = "ok", Suspect = "suspect", Pass = "PASS", Fail = "FAIL";

    public static readonly Dictionary<string, string> StatusText = new()
    {
        [Unchecked] = "未检测", [AutoOk] = "未检出候选（待复核）", [Suspect] = "疑似缺陷", [Pass] = "PASS", [Fail] = "FAIL",
    };

    public static readonly Dictionary<string, string> StatusColors = new()
    {
        [Unchecked] = "#9a9a9a", [AutoOk] = "#7fc8a0", [Suspect] = "#ff9900", [Pass] = "#18a848", [Fail] = "#e02424",
    };

    public string Root { get; }
    public string FilePath { get; }
    public SessionData Data { get; }
    public List<string> Files { get; }
    public List<string> Empty { get; } = [];
    public bool Dirty { get; set; }

    public Session(string root)
    {
        Root = Path.GetFullPath(root);
        FilePath = Path.Combine(Root, StateDir, "session.json");
        Data = Json.Load<SessionData>(FilePath);
        Data.Root = Root;
        if (string.IsNullOrEmpty(Data.Created)) Data.Created = Now();
        Files = Scanner.Scan(Root, Empty);
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public ImageRecord Rec(string rel)
    {
        if (!Data.Images.TryGetValue(rel, out var r)) Data.Images[rel] = r = new ImageRecord();
        return r;
    }

    ImageRecord? Peek(string rel) => Data.Images.GetValueOrDefault(rel);

    public bool HasDetection(string rel) => Peek(rel)?.DetectedAt != null;

    /// <summary>Keeps manual marks and every reviewed box; a new box landing on a reviewed one is dropped.</summary>
    public void SetDetection(string rel, ImageResult res)
    {
        var r = Rec(rel);
        var keep = r.Defects.Where(d => d.Source == "manual" || d.Status is "accepted" or "rejected").ToList();
        var reviewed = keep.Where(d => d.Status is "accepted" or "rejected").ToList();
        r.Defects = keep.Concat(res.Defects.Where(d => !reviewed.Any(k => Defect.Overlaps(d, k)))).ToList();
        r.Width = res.Width;
        r.Height = res.Height;
        r.Product = res.Product;
        r.PixelUm = res.PixelUm;
        r.Units = res.Units;
        r.Wires = res.Wires;
        r.DetectedAt = Now();
        r.Error = null;
        Dirty = true;
    }

    public void SetError(string rel, string msg)
    {
        var r = Rec(rel);
        r.Error = msg;
        r.DetectedAt = Now();
        Dirty = true;
    }

    /// <summary>Remove every box and the detection result ("未检测" again); comments stay, verdicts only on request.</summary>
    public void ClearMarks(IEnumerable<string> rels, bool verdicts = false)
    {
        foreach (var rel in rels)
        {
            var r = Rec(rel);
            r.Defects = [];
            r.DetectedAt = r.Error = r.Product = null;
            r.Units = null;
            r.Wires = null;
            if (verdicts) (r.Verdict, r.Reviewer, r.ReviewedAt) = (null, "", "");
        }
        Dirty = true;
    }

    public void SetDefects(string rel, List<Defect> defects)
    {
        Rec(rel).Defects = defects;
        Dirty = true;
    }

    public void SetVerdict(string rel, string? verdict, string reviewer = "")
    {
        var r = Rec(rel);
        r.Verdict = verdict;
        r.Reviewer = reviewer;
        r.ReviewedAt = verdict != null ? Now() : "";
        Dirty = true;
    }

    public void SetComment(string rel, string text)
    {
        Rec(rel).Comment = text;
        Dirty = true;
    }

    public List<Defect> LiveDefects(string rel) => Peek(rel)?.Defects.Where(d => d.Status != "rejected").ToList() ?? [];

    public bool HasPending(string rel)
        => Peek(rel)?.Verdict != Pass && LiveDefects(rel).Any(d => d.Status == "pending" && d.Source != "manual");

    public string AutoState(string rel)
    {
        var r = Peek(rel);
        if (r?.DetectedAt == null) return Unchecked;
        if (r.Error != null) return Suspect;
        return LiveDefects(rel).Count > 0 ? Suspect : AutoOk;
    }

    public string Status(string rel)
    {
        var v = Peek(rel)?.Verdict;
        return v is Pass or Fail ? v : AutoState(rel);
    }

    public Dictionary<string, int> Counts()
    {
        var c = StatusText.Keys.ToDictionary(k => k, _ => 0);
        foreach (var f in Files) c[Status(f)]++;
        return c;
    }

    public void Save()
    {
        Data.Saved = Now();
        Json.Save(FilePath, Data);
        Dirty = false;
    }
}

public static class CsvReport
{
    static readonly UTF8Encoding Bom = new(true);

    static string Q(object? v)
    {
        var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    static string Row(params object?[] cells) => string.Join(",", cells.Select(Q));

    public static string FlagText(IEnumerable<string> flags)
        => string.Join("、", flags.Select(f => DefectTypes.FromFlag.GetValueOrDefault(f, f)));

    /// <summary>Folder report: one row per image (summary) and one row per wire (details file).</summary>
    public static (string summary, string details) Write(Session s, string summaryPath)
    {
        var sum = new List<string> { Row("文件", "状态", "判定", "判定人", "判定时间", "产品", "像素µm", "单元数", "导线数", "NG导线",
            "有效缺陷数", "缺陷类型", "备注", "检测时间", "错误") };
        var det = new List<string> { Row("文件", "单元", "线", "结果", "z", "z_ball", "长度µm", "sweep%", "sweep µm", "端点偏移µm",
            "起点缺口µm", "终点缺口µm", "焊点1x", "焊点1y", "焊点2x", "焊点2y") };
        foreach (var rel in s.Files)
        {
            var r = s.Rec(rel);
            var live = s.LiveDefects(rel);
            var wires = r.Wires ?? [];
            double um = r.PixelUm > 0 ? r.PixelUm : 1;
            sum.Add(Row(rel, Session.StatusText[s.Status(rel)], r.Verdict ?? "", r.Reviewer, r.ReviewedAt, r.Product ?? "",
                r.PixelUm > 0 ? r.PixelUm : "", r.Units?.Count ?? 0, wires.Count, wires.Count(w => w.Bad), live.Count,
                string.Join("、", live.GroupBy(d => d.Type).Select(g => $"{g.Key}×{g.Count()}")), r.Comment,
                r.DetectedAt ?? "", r.Error ?? ""));
            foreach (var w in wires)
                det.Add(Row(rel, w.Unit + 1, w.Index + 1, w.Bad ? FlagText(w.Flags) : "正常", w.ZAll.ToString("F1"),
                    w.ZBall.ToString("F1"), (w.L * um).ToString("F0"), w.SweepPct.ToString("F1"), (w.SweepPx * um).ToString("F1"),
                    (w.ShiftPx * um).ToString("F1"), (w.Gap1 * um).ToString("F0"), (w.Gap2 * um).ToString("F0"),
                    w.Ball[0].ToString("F1"), w.Ball[1].ToString("F1"), w.P2[0].ToString("F1"), w.P2[1].ToString("F1")));
        }
        var detailsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(summaryPath))!,
            Path.GetFileNameWithoutExtension(summaryPath) + "_导线明细.csv");
        File.WriteAllLines(summaryPath, sum, Bom);
        File.WriteAllLines(detailsPath, det, Bom);
        return (summaryPath, detailsPath);
    }

    /// <summary>Wires of one image (the 「导线统计」 export).</summary>
    public static void WriteWires(string path, ImageRecord r)
    {
        double um = r.PixelUm > 0 ? r.PixelUm : 1;
        var lines = new List<string> { Row("单元", "线", "起点x", "起点y", "终点x", "终点y", "长度µm", "sweep%", "端点偏移µm", "z", "判定") };
        foreach (var w in r.Wires ?? [])
            lines.Add(Row(w.Unit + 1, w.Index + 1, w.P1[0].ToString("F1"), w.P1[1].ToString("F1"), w.P2[0].ToString("F1"),
                w.P2[1].ToString("F1"), (w.L * um).ToString("F0"), w.SweepPct.ToString("F1"), (w.ShiftPx * um).ToString("F1"),
                w.ZAll.ToString("F1"), w.Bad ? FlagText(w.Flags) : "正常"));
        File.WriteAllLines(path, lines, Bom);
    }
}
