using System.Globalization;
using System.Text.Json.Nodes;
using OpenCvSharp;
using WireInspect.Core;

namespace WireInspect.Mcp;

/// <summary>What a tool call can do on a wire-inspection workspace (GUI window or headless process). All public
/// methods return structured results or throw; WireInspectTools turns them into MCP tools.</summary>
public abstract class WireInspectSession
{
    public virtual string Mode => "headless";
    public Action<string>? Echo { get; set; }

    protected Session? _session;
    protected Product? _product;
    protected string? _current;
    protected CancellationTokenSource? _cts;
    protected Task? _detectTask;
    protected int _detectTotal, _detectDone;
    protected readonly object Gate = new();

    // ------------------------------------------------------------- plumbing
    /// <summary>Run on the UI thread (GUI) or directly (headless).</summary>
    protected abstract T OnUi<T>(Func<T> f);
    protected abstract Task<T> OnUiAsync<T>(Func<Task<T>> f);
    /// <summary>Refresh the UI after session data changed (no-op headless).</summary>
    protected virtual void AfterChange(string? rel = null) { }
    protected virtual Task<JsonObject> DoOpenFolderAsync(string path)
    {
        _session?.Save();
        _session = new Session(path) { };
        _current = _session.Files.FirstOrDefault();
        AppConfig.AddRecent(path);
        return Task.FromResult(FolderInfo());
    }
    protected virtual void DoSelectImage(string rel) => _current = rel;
    protected virtual Task DoStartDetectAsync(bool all) => StartHeadlessDetect(all);
    protected virtual void DoStop() => _cts?.Cancel();
    protected virtual void DoSelectProduct(string? name)
        => _product = name == null ? null : Product.Load(ProductDir(name));
    protected virtual void AfterProductSaved(Product p) => _product = p;

    public Session? S => _session;
    public Product? P => _product;
    public string? Current => _current;
    public bool Running => _detectTask is { IsCompleted: false };
    public virtual bool Busy => Running;
    /// <summary>(done, total) of the running/last batch; the GUI reports its own progress.</summary>
    protected virtual (int Done, int Total) DetectProgress => (_detectDone, _detectTotal);

    // ------------------------------------------------------------- helpers
    protected Session RequireSession() => _session ?? throw new InvalidOperationException("没有打开的文件夹：先 wireinspect_open_folder。");
    protected Product RequireProduct() => _product ?? throw new InvalidOperationException("没有选择产品：先 wireinspect_select_product 或在界面新建。");
    protected string Rel(string? rel)
    {
        var s = RequireSession();
        var r = rel ?? _current ?? throw new InvalidOperationException("没有当前图片：先 wireinspect_select_image。");
        if (!s.Files.Contains(r)) throw new ArgumentException("图片不在列表里: " + r);
        return r;
    }
    static string ProductDir(string name)
    {
        var dir = Path.Combine(AppPaths.Products, name);
        if (!Directory.Exists(dir)) throw new ArgumentException("找不到产品: " + name);
        return dir;
    }
    static double R3(double v) => Math.Round(v, 3);

    protected JsonObject FolderInfo() => new()
    {
        ["root"] = _session!.Root,
        ["files"] = _session.Files.Count,
        ["empty_skipped"] = _session.Empty.Count,
        ["product"] = _product?.Name,
        ["current"] = _current,
    };

    // ------------------------------------------------------------- folder / images
    public Task<JsonObject> OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("文件夹不存在: " + path);
        return OnUiAsync(() => DoOpenFolderAsync(Path.GetFullPath(path)));
    }

    public JsonArray RecentFolders() => new(AppConfig.Load().Recent()
        .Select(p => (JsonNode?)new JsonObject { ["path"] = p, ["exists"] = Directory.Exists(p) }).ToArray());

    public JsonObject SelectImage(string rel) => OnUi(() =>
    {
        var s = RequireSession();
        if (!s.Files.Contains(rel)) throw new ArgumentException("图片不在列表里: " + rel);
        DoSelectImage(rel);
        return new JsonObject { ["current"] = rel };
    });

    public JsonArray ListImages(string? status)
    {
        var s = RequireSession();
        var rows = s.Files.Select(f => new JsonObject
        {
            ["file"] = f,
            ["status"] = s.Status(f),
            ["status_text"] = Session.StatusText[s.Status(f)],
            ["defects"] = s.LiveDefects(f).Count,
            ["detected"] = s.HasDetection(f),
            ["verdict"] = s.Rec(f).Verdict,
        }).Where(o => status == null || (string?)o["status"] == status).ToList();
        return new JsonArray(rows.Select(o => (JsonNode?)o).ToArray());
    }

    // ------------------------------------------------------------- detection
    public async Task<JsonObject> DetectAsync(bool all, double waitSeconds)
    {
        var t = await OnUiAsync(async () =>
        {
            var s = RequireSession();
            RequireProduct();
            if (Busy) throw new InvalidOperationException("检测已在运行：用 wireinspect_get_status 看进度，wireinspect_stop 停止。");
            _detectTotal = all ? s.Files.Count : s.Files.Count(r => !s.HasDetection(r));
            _detectDone = 0;
            var task = DoStartDetectAsync(all);
            _detectTask = task;
            return task;
        });
        if (waitSeconds > 0 && !t.IsCompleted)
            await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(waitSeconds)));
        var st = Status();
        st["started"] = true;
        return st;
    }

    protected Task StartHeadlessDetect(bool all)
    {
        var s = RequireSession();
        var p = RequireProduct();
        var todo = all ? s.Files.ToList() : s.Files.Where(r => !s.HasDetection(r)).ToList();
        var cts = _cts = new CancellationTokenSource();
        int workers = AppConfig.Load().Workers;
        if (workers <= 0) workers = Math.Max(1, Environment.ProcessorCount - 1);
        return Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cts.Token }, rel =>
                {
                    Echo?.Invoke("detect " + rel);
                    try
                    {
                        using var g = ImageIO.ReadGray(Path.Combine(s.Root, rel));
                        var res = Inspector.Detect(g, p);
                        lock (Gate) s.SetDetection(rel, res);
                    }
                    catch (Exception e) { lock (Gate) s.SetError(rel, $"{e.GetType().Name}: {e.Message}"); }
                    Interlocked.Increment(ref _detectDone);
                });
            }
            catch (OperationCanceledException) { }
            lock (Gate) s.Save();
        }, cts.Token);
    }

    public JsonObject Stop() => OnUi(() =>
    {
        if (!Busy) return new JsonObject { ["stopped"] = false, ["message"] = "没有运行中的检测" };
        DoStop();
        return new JsonObject { ["stopped"] = true };
    });

    public JsonObject Status() => OnUi(() =>
    {
        var (done, total) = DetectProgress;
        var st = new JsonObject
        {
            ["mode"] = Mode,
            ["running"] = Busy,
            ["detect_done"] = done,
            ["detect_total"] = total,
            ["product"] = _product?.Name,
            ["current"] = _current,
        };
        if (_session != null)
        {
            st["root"] = _session.Root;
            st["files"] = _session.Files.Count;
            var counts = new JsonObject();
            foreach (var (k, v) in _session.Counts()) counts[k] = v;
            st["counts"] = counts;
        }
        return st;
    });

    // ------------------------------------------------------------- results
    static JsonObject DefectJson(Defect d) => new()
    {
        ["id"] = d.Id, ["type"] = d.Type, ["score"] = d.Score, ["source"] = d.Source, ["status"] = d.Status,
        ["by"] = d.By, ["x0"] = d.X0, ["y0"] = d.Y0, ["x1"] = d.X1, ["y1"] = d.Y1,
    };

    static JsonObject WireJson(WireResult w, ImageRecord r) => new()
    {
        ["unit"] = w.Unit + 1, ["wire"] = w.Index + 1, ["flags"] = new JsonArray(w.Flags.Select(f => (JsonNode?)f).ToArray()),
        ["flags_text"] = w.Bad ? CsvReport.FlagText(w.Flags) : "正常",
        ["z_all"] = R3(w.ZAll), ["z_seg"] = new JsonArray(w.ZSeg.Select(z => (JsonNode?)R3(z)).ToArray()), ["z_ball"] = R3(w.ZBall),
        ["l_px"] = R3(w.L), ["sweep_px"] = R3(w.SweepPx), ["sweep_pct"] = R3(w.SweepPct), ["shift_px"] = R3(w.ShiftPx),
        ["gap1_px"] = R3(w.Gap1), ["gap2_px"] = R3(w.Gap2), ["gap_mid_px"] = R3(w.GapMid),
        ["p1"] = new JsonArray(w.P1[0], w.P1[1]), ["p2"] = new JsonArray(w.P2[0], w.P2[1]), ["ball"] = new JsonArray(w.Ball[0], w.Ball[1]),
        ["unit_angle"] = r.Units != null && w.Unit < r.Units.Count ? R3(r.Units[w.Unit].Angle) : 0,
    };

    public JsonObject GetResult(string? rel) => OnUi(() =>
    {
        var s = RequireSession();
        var r = s.Rec(Rel(rel));
        var o = new JsonObject
        {
            ["file"] = Rel(rel),
            ["status"] = s.Status(Rel(rel)),
            ["detected_at"] = r.DetectedAt,
            ["error"] = r.Error,
            ["product"] = r.Product,
            ["angle"] = r.Angle,
            ["verdict"] = r.Verdict,
            ["reviewer"] = r.Reviewer,
            ["comment"] = r.Comment,
            ["units"] = new JsonArray((r.Units ?? []).Select(u => (JsonNode?)new JsonObject
            {
                ["index"] = u.Index + 1, ["box"] = new JsonArray(u.Box[0], u.Box[1], u.Box[2], u.Box[3]),
                ["match"] = R3(u.Match), ["angle"] = R3(u.Angle), ["mirror"] = u.Mirror,
            }).ToArray()),
            ["wires"] = new JsonArray((r.Wires ?? []).Select(w => (JsonNode?)WireJson(w, r)).ToArray()),
            ["defects"] = new JsonArray(r.Defects.Select(d => (JsonNode?)DefectJson(d)).ToArray()),
        };
        return o;
    });

    public JsonArray ListDefects(string? rel, string? status)
    {
        var s = RequireSession();
        var files = rel != null ? [Rel(rel)] : s.Files.ToList();
        var rows = files.SelectMany(f => s.Rec(f).Defects
            .Where(d => status == null || d.Status == status)
            .Select(d => { var o = DefectJson(d); o["file"] = f; return (JsonNode?)o; }));
        return new JsonArray(rows.ToArray());
    }

    public byte[] RenderPng(string? rel) => OnUi(() =>
    {
        var s = RequireSession();
        var r = s.Rec(Rel(rel));
        using var g = ImageIO.ReadGray(Path.Combine(s.Root, Rel(rel)));
        var res = new ImageResult { Width = r.Width, Height = r.Height, Units = r.Units ?? [], Wires = r.Wires ?? [], Defects = r.Defects };
        using var marked = Annotator.Draw(g, res, 2);
        Cv2.ImEncode(".png", marked, out byte[] buf);
        return buf;
    });

    public JsonObject SaveReport(string? path) => OnUi(() =>
    {
        var s = RequireSession();
        lock (Gate) s.Save();
        var file = path ?? Path.Combine(s.Root, $"{Path.GetFileName(s.Root.TrimEnd('/', '\\'))}_报告_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        var (sum, det) = CsvReport.Write(s, Path.GetFullPath(file));
        return new JsonObject { ["summary_csv"] = sum, ["wires_csv"] = det };
    });

    public JsonObject ClearMarks(string scope, bool verdicts) => OnUi(() =>
    {
        var s = RequireSession();
        var rels = scope switch
        {
            "image" => [Rel(null)],
            "subfolder" => _current?.Contains('/') == true
                ? s.Files.Where(f => f.StartsWith(_current[..(_current.LastIndexOf('/') + 1)], StringComparison.Ordinal)).ToList()
                : throw new ArgumentException("当前图片不在子文件夹里"),
            "folder" => s.Files.ToList(),
            _ => throw new ArgumentException("scope 只能是 image / subfolder / folder"),
        };
        lock (Gate) { s.ClearMarks(rels, verdicts); s.Save(); }
        AfterChange();
        return new JsonObject { ["cleared"] = rels.Count, ["verdicts_cleared"] = verdicts };
    });

    // ------------------------------------------------------------- defects / review
    (Session s, string rel, Defect d) FindDefect(string id)
    {
        var s = RequireSession();
        foreach (var (f, r) in s.Data.Images)
            if (r.Defects.FirstOrDefault(d => d.Id == id) is { } d)
                return (s, f, d);
        throw new ArgumentException("找不到缺陷: " + id);
    }

    public JsonObject AddDefect(string? rel, int x0, int y0, int x1, int y1, string type) => OnUi(() =>
    {
        var s = RequireSession();
        var f = Rel(rel);
        if (!DefectTypes.All.Contains(type)) throw new ArgumentException("未知缺陷类型: " + type);
        var d = new Defect { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Type = type, Score = 1, Source = "manual", Status = "accepted" };
        lock (Gate) { s.SetDefects(f, [.. s.Rec(f).Defects, d]); s.Save(); }
        AfterChange(f);
        return DefectJson(d);
    });

    public JsonObject SetDefectStatus(string id, string status) => OnUi(() =>
    {
        if (status is not ("pending" or "accepted" or "rejected")) throw new ArgumentException("status 只能是 pending / accepted / rejected");
        var (s, f, d) = FindDefect(id);
        lock (Gate) { d.Status = status; s.SetDefects(f, s.Rec(f).Defects); s.Save(); }
        AfterChange(f);
        return DefectJson(d);
    });

    public JsonObject ChangeDefectType(string id, string type) => OnUi(() =>
    {
        if (!DefectTypes.All.Contains(type)) throw new ArgumentException("未知缺陷类型: " + type);
        var (s, f, d) = FindDefect(id);
        lock (Gate) { d.Type = type; d.Status = "accepted"; s.SetDefects(f, s.Rec(f).Defects); s.Save(); }
        AfterChange(f);
        return DefectJson(d);
    });

    public JsonObject DeleteDefect(string id) => OnUi(() =>
    {
        var (s, f, d) = FindDefect(id);
        lock (Gate) { s.SetDefects(f, s.Rec(f).Defects.Where(x => x.Id != id).ToList()); s.Save(); }
        AfterChange(f);
        return new JsonObject { ["deleted"] = id };
    });

    public JsonObject SetVerdict(string? rel, string? verdict, string? comment, bool changeVerdict = true) => OnUi(() =>
    {
        var s = RequireSession();
        var f = Rel(rel);
        if (verdict is not (null or "PASS" or "FAIL")) throw new ArgumentException("verdict 只能是 PASS / FAIL / null（清除）");
        lock (Gate)
        {
            if (changeVerdict) s.SetVerdict(f, verdict, AppConfig.Load().Operator);
            if (comment != null) s.SetComment(f, comment);
            s.Save();
        }
        AfterChange(f);
        var r = s.Rec(f);
        return new JsonObject { ["file"] = f, ["verdict"] = r.Verdict, ["comment"] = r.Comment, ["reviewed_at"] = r.ReviewedAt };
    });

    public JsonObject SetComment(string? rel, string text) => OnUi(() =>
    {
        var s = RequireSession();
        var f = Rel(rel);
        lock (Gate) { s.SetComment(f, text); s.Save(); }
        AfterChange(f);
        return new JsonObject { ["file"] = f, ["comment"] = text };
    });

    // ------------------------------------------------------------- product / config
    public JsonArray ListProducts() => new(Product.List()
        .Select(n => (JsonNode?)new JsonObject { ["name"] = n, ["selected"] = n == _product?.Name }).ToArray());

    public JsonObject SelectProduct(string? name) => OnUi(() =>
    {
        if (name != null && !Product.List().Contains(name)) throw new ArgumentException("找不到产品: " + name);
        DoSelectProduct(name);
        AfterChange();
        return new JsonObject { ["product"] = _product?.Name };
    });

    public JsonObject GetProduct() => OnUi(() =>
    {
        var p = RequireProduct();
        return (JsonObject)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(p, Core.Json.Options))!;
    });

    public JsonObject SetProductParams(JsonObject patch) => OnUi(() =>
    {
        var p = RequireProduct();
        double D(string k, double cur) => patch[k] is { } v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : cur;
        bool B(string k, bool cur) => patch[k] is { } v && bool.TryParse(v.ToString(), out var b) ? b : cur;
        string S(string k, string cur) => patch[k]?.GetValue<string>() is { Length: > 0 } s ? s : cur;
        p.MatchThr = D("match_thr", p.MatchThr);
        p.Polarity = S("polarity", p.Polarity);
        p.WireWidthPx = D("wire_width_px", p.WireWidthPx);
        p.CorridorPx = D("corridor_px", p.CorridorPx);
        p.EndTolPx = D("end_tol_px", p.EndTolPx);
        p.GapMaxPx = D("gap_max_px", p.GapMaxPx);
        p.ShiftMaxPx = D("shift_max_px", p.ShiftMaxPx);
        p.SweepMaxPct = D("sweep_max_pct", p.SweepMaxPct);
        p.ZMissing = D("z_missing", p.ZMissing);
        p.ZSeg = D("z_seg", p.ZSeg);
        p.ZBall = D("z_ball", p.ZBall);
        p.NSeg = (int)D("n_seg", p.NSeg);
        p.MaxAngleDeg = D("max_angle_deg", p.MaxAngleDeg);
        p.AlignSearchPx = D("align_search_px", p.AlignSearchPx);
        p.LocalAlign = B("local_align", p.LocalAlign);
        p.AllowMirror = B("allow_mirror", p.AllowMirror);
        p.Note = patch["note"] != null ? patch["note"]!.GetValue<string>() : p.Note;
        if (patch["wires"] is JsonArray ws)
        {
            p.Wires = ws.Select((w, i) =>
            {
                var o = w as JsonObject ?? throw new ArgumentException($"wires[{i}] 不是对象");
                var p1 = o["p1"] as JsonArray; var p2 = o["p2"] as JsonArray;
                if (p1?.Count != 2 || p2?.Count != 2) throw new ArgumentException($"wires[{i}] 需要 p1:[x,y] 和 p2:[x,y]（模板坐标）");
                return new WireSpec(p1[0]!.GetValue<double>(), p1[1]!.GetValue<double>(), p2[0]!.GetValue<double>(), p2[1]!.GetValue<double>());
            }).ToList();
        }
        p.Save();
        AfterProductSaved(p);
        AfterChange();
        return GetProduct();
    });

    public JsonObject AutoWires(double x, double y, double w, double h, string? rel, string? product) => OnUi(() =>
    {
        var s = RequireSession();
        var p = product != null ? Product.Load(ProductDir(product)) : RequireProduct();
        using var g = ImageIO.ReadGray(Path.Combine(s.Root, Rel(rel)));
        var rect = new Rect((int)x, (int)y, (int)w, (int)h);
        if (rect.Right > g.Width || rect.Bottom > g.Height || w < 4 || h < 4)
            throw new ArgumentException($"区域 [{x},{y},{w},{h}] 超出图片 {g.Width}x{g.Height} 或太小");
        using var tpl = new Mat(g, rect);
        var found = WireInspect.Core.AutoWires.Find(tpl, p);
        return new JsonObject
        {
            ["count"] = found.Count,
            ["wires"] = new JsonArray(found.Select(ws => (JsonNode?)new JsonObject
            {
                ["p1"] = new JsonArray(ws.P1[0], ws.P1[1]), ["p2"] = new JsonArray(ws.P2[0], ws.P2[1]),
                ["length_px"] = R3(ws.Length),
            }).ToArray()),
            ["tpl_size"] = new JsonArray((int)w, (int)h),
        };
    });

    public JsonObject CreateProduct(JsonObject a) => OnUi(() =>
    {
        var s = RequireSession();
        var name = a["name"]?.GetValue<string>() ?? throw new ArgumentException("缺少参数 name");
        var f = Rel(a["image"]?.GetValue<string>());
        using var g = ImageIO.ReadGray(Path.Combine(s.Root, f));
        var r = a["rect"] as JsonArray ?? throw new ArgumentException("缺少参数 rect: [x,y,w,h]（图片坐标，裁作模板）");
        var rect = new Rect((int)r[0]!.GetValue<double>(), (int)r[1]!.GetValue<double>(), (int)r[2]!.GetValue<double>(), (int)r[3]!.GetValue<double>());
        if (rect.Right > g.Width || rect.Bottom > g.Height || rect.Width < 4 || rect.Height < 4)
            throw new ArgumentException("rect 超出图片或太小");
        var p = new Product { Name = name, Template = new Mat(g, rect).Clone() };
        var patch = a.DeepClone() as JsonObject ?? new JsonObject();
        if (a["wires"] is JsonArray ws && ws.Count == 0) patch.Remove("wires");
        // reuse the param patcher against a fresh product
        var tmp = _product;
        try { _product = p; SetProductParams(patch); }
        finally { _product = tmp; }
        AfterChange();
        return new JsonObject { ["product"] = p.Name, ["dir"] = p.Dir };
    });

    public JsonObject GetConfig() => OnUi(() =>
    {
        var c = AppConfig.Load();
        return new JsonObject
        {
            ["operator"] = c.Operator, ["product"] = c.Product, ["workers"] = c.Workers,
            ["recent_folders"] = new JsonArray(c.Recent().Select(p => (JsonNode?)p).ToArray()),
        };
    });

    public JsonObject SetConfig(JsonObject a) => OnUi(() =>
    {
        var c = AppConfig.Update(cfg =>
        {
            if (a["operator"] != null) cfg.Operator = a["operator"]!.GetValue<string>();
            if (a["workers"] is { } w) cfg.Workers = (int)w.GetValue<double>();
            if (a["product"]?.GetValue<string>() is { Length: > 0 } name)
            {
                if (!Product.List().Contains(name)) throw new ArgumentException("找不到产品: " + name);
                cfg.Product = name;
                DoSelectProduct(name);
            }
        });
        AfterChange();
        return new JsonObject { ["operator"] = c.Operator, ["product"] = c.Product, ["workers"] = c.Workers };
    });
}

/// <summary>No-UI session: everything runs on the calling thread; used by `WireInspect --mcp-stdio`.</summary>
public sealed class HeadlessSession : WireInspectSession
{
    protected override T OnUi<T>(Func<T> f) => f();
    protected override Task<T> OnUiAsync<T>(Func<Task<T>> f) => f();
}
