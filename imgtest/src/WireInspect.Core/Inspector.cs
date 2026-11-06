using System.Runtime.InteropServices;
using OpenCvSharp;

namespace WireInspect.Core;

public sealed class UnitResult
{
    public int Index { get; set; }
    public int[] Box { get; set; } = [0, 0, 0, 0];   // x, y (template origin on the image, rounded), template w, h
    public double Match { get; set; }
    public double Angle { get; set; }   // unit rotation on the image, degrees (counter-clockwise on screen)
    public bool Mirror { get; set; }    // matched by the mirrored template (part flipped over)
    /// <summary>Template -> image affine [a, b, c, d, e, f] (rotation / distortion corrected); null = Box translation.</summary>
    public double[]? Affine { get; set; }

    public double[] Map() => Affine ?? Align.Translation(Box[0], Box[1]);

    /// <summary>The unit's four corners on the image (top-left, top-right, bottom-right, bottom-left).</summary>
    public (double x, double y)[] Corners()
    {
        var m = Map();
        return [Align.Apply(m, 0, 0), Align.Apply(m, Box[2], 0), Align.Apply(m, Box[2], Box[3]), Align.Apply(m, 0, Box[3])];
    }
}

public sealed class WireResult
{
    public int Unit { get; set; }
    public int Index { get; set; }
    public List<string> Flags { get; set; } = [];
    public double ZAll { get; set; }
    public double[] ZSeg { get; set; } = [];
    public double ZBall { get; set; }
    public double[] Ball { get; set; } = [0, 0];
    public double SweepPx { get; set; }
    public double SweepPct { get; set; }
    public double ShiftPx { get; set; }
    public double Gap1 { get; set; }
    public double Gap2 { get; set; }
    public double GapMid { get; set; }   // longest interior stretch without wire response, px
    public double L { get; set; }
    public double[] P1 { get; set; } = [0, 0];
    public double[] P2 { get; set; } = [0, 0];
    public List<double[]> Curve { get; set; } = [];

    public bool Bad => Flags.Count > 0;
}

public sealed class ImageResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Product { get; set; } = "";
    public double Angle { get; set; }   // common unit direction, degrees (0 when the units point every way)
    public List<UnitResult> Units { get; set; } = [];
    public List<WireResult> Wires { get; set; } = [];
    public List<Defect> Defects { get; set; } = [];
}

/// <summary>Float image in row-major order, with bilinear sampling (edge values repeated outside).</summary>
public sealed class FImage
{
    public readonly int W, H;
    public readonly float[] Data;

    public FImage(int w, int h) { W = w; H = h; Data = new float[w * h]; }

    public static FImage FromMat(Mat m)
    {
        using var f = new Mat();
        m.ConvertTo(f, MatType.CV_32F);
        var img = new FImage(f.Width, f.Height);
        Marshal.Copy(f.Data, img.Data, 0, img.Data.Length);   // a freshly converted Mat is continuous
        return img;
    }

    public float this[int y, int x] { get => Data[y * W + x]; set => Data[y * W + x] = value; }

    public double Sample(double x, double y)
    {
        x = Math.Clamp(x, 0, W - 1);
        y = Math.Clamp(y, 0, H - 1);
        int x0 = Math.Min((int)x, Math.Max(W - 2, 0)), y0 = Math.Min((int)y, Math.Max(H - 2, 0));
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        double fx = x - x0, fy = y - y0;
        return (Data[y0 * W + x0] * (1 - fx) + Data[y0 * W + x1] * fx) * (1 - fy)
             + (Data[y1 * W + x0] * (1 - fx) + Data[y1 * W + x1] * fx) * fy;
    }
}

public static class Inspector
{
    public static List<UnitResult> FindUnits(Mat gray8, Mat tpl8, double thr)
    {
        var units = new List<UnitResult>();
        if (tpl8.Width > gray8.Width || tpl8.Height > gray8.Height) return units;
        using var res = new Mat();
        Cv2.MatchTemplate(gray8, tpl8, res, TemplateMatchModes.CCoeffNormed);
        int w = tpl8.Width, h = tpl8.Height, d = Math.Max(4, Math.Min(w, h) / 2);
        while (true)
        {
            Cv2.MinMaxLoc(res, out _, out double mx, out _, out Point loc);
            if (mx < thr || double.IsNaN(mx)) break;
            units.Add(new UnitResult { Box = [loc.X, loc.Y, w, h], Match = mx });
            int x0 = Math.Max(0, loc.X - d), y0 = Math.Max(0, loc.Y - d);
            int x1 = Math.Min(res.Width, loc.X + d + 1), y1 = Math.Min(res.Height, loc.Y + d + 1);
            res[new Rect(x0, y0, x1 - x0, y1 - y0)].SetTo(new Scalar(-1));
        }
        units = units.OrderBy(u => Math.Round(u.Box[1] / (double)h, MidpointRounding.ToEven)).ThenBy(u => u.Box[0]).ToList();
        for (int i = 0; i < units.Count; i++) units[i].Index = i;
        return units;
    }

    public static (Point2d p1, Point2d p2) WirePoints(UnitResult u, WireSpec w) => WirePoints(u.Map(), w);

    static (Point2d p1, Point2d p2) WirePoints(double[] m, WireSpec w)
    {
        var (x1, y1) = Align.Apply(m, w.P1[0], w.P1[1]);
        var (x2, y2) = Align.Apply(m, w.P2[0], w.P2[1]);
        return (new Point2d(x1, y1), new Point2d(x2, y2));
    }

    /// <summary>Gray (uint8) -> float, wire made dark, light blur: the image every wire is measured on.</summary>
    public static FImage Prepare(Mat gray8, bool dark)
    {
        using var f = new Mat();
        gray8.ConvertTo(f, MatType.CV_32F);
        if (!dark) Cv2.Subtract(new Scalar(255), f, f);
        using var b = new Mat();
        Cv2.GaussianBlur(f, b, new Size(0, 0), 0.7);
        return FImage.FromMat(b);
    }

    /// <summary>Units of one template (any angle, or upright only), each with Affine template -> image and Angle.</summary>
    static List<UnitResult> Find(Mat gray8, Mat tpl, PixelParams a)
    {
        if (a.MaxAngle > 0) return Align.FindUnitsRotated(gray8, tpl, a.MatchThr, a.MaxAngle, a.AlignSearch, a.LocalAlign);
        var units = FindUnits(gray8, tpl, a.MatchThr);
        foreach (var u in units)
        {
            var m = a.LocalAlign ? Align.AlignUnit(gray8, tpl, u.Box, a.AlignSearch) : u.Map();
            u.Affine = m;
            u.Box[0] = (int)Math.Round(m[2]);
            u.Box[1] = (int)Math.Round(m[5]);
        }
        return units;
    }

    public static ImageResult Detect(Mat gray8, Product product)
    {
        if (product.Template == null) throw new InvalidOperationException("产品没有模板图");
        var a = product.Params();
        var tpl = product.Template;
        var units = Find(gray8, tpl, a);
        if (a.AllowMirror)   // a part flipped over shows up mirrored in the X-ray picture
        {
            using var flipped = new Mat();
            Cv2.Flip(tpl, flipped, FlipMode.Y);
            units = Align.MergeMirrored(units, Find(gray8, flipped, a), tpl.Width, tpl.Height);
        }
        var res = new ImageResult
        {
            Width = gray8.Width, Height = gray8.Height, Product = product.Name, Units = units,
            Angle = Math.Round(Align.MeanAngle(units.Select(u => u.Angle).ToList(), Align.SameDirection) * 10) / 10,
        };
        var img = Prepare(gray8, a.Dark);
        foreach (var u in units)
            for (int i = 0; i < product.Wires.Count; i++)
            {
                var (p1, p2) = WirePoints(u.Map(), product.Wires[i]);
                var r = InspectWire(img, p1, p2, a);
                r.Unit = u.Index;
                r.Index = i;
                res.Wires.Add(r);
            }
        res.Defects = MakeDefects(res.Wires, a);
        return res;
    }

    static readonly string[] FlagOrder = ["MISSING", "BROKEN", "BOND1", "BOND2", "SWEEP", "SHIFT"];

    public static List<Defect> MakeDefects(List<WireResult> wires, PixelParams a)
    {
        var list = new List<Defect>();
        foreach (var w in wires.Where(w => w.Bad))
        {
            var pts = new List<double[]>(w.Curve) { w.P1, w.P2 };
            double pad = a.HalfW + 4;
            var main = FlagOrder.First(f => w.Flags.Contains(f));
            list.Add(new Defect
            {
                X0 = (int)Math.Floor(pts.Min(p => p[0]) - pad), Y0 = (int)Math.Floor(pts.Min(p => p[1]) - pad),
                X1 = (int)Math.Ceiling(pts.Max(p => p[0]) + pad), Y1 = (int)Math.Ceiling(pts.Max(p => p[1]) + pad),
                Type = DefectTypes.FromFlag[main], Score = Math.Round(Severity(w, main, a), 3),
                By = $"单元{w.Unit + 1}·线{w.Index + 1}",
            });
        }
        return list;
    }

    static double Severity(WireResult w, string flag, PixelParams a)
    {
        double ratio = flag switch
        {
            "MISSING" or "BROKEN" => 2,
            "SWEEP" => w.SweepPct / Math.Max(a.SweepMaxPct, 1e-6),
            "SHIFT" => w.ShiftPx / Math.Max(a.ShiftMax, 1e-6),
            "BOND1" => Math.Max(a.ZBall / Math.Max(w.ZBall, 0.1), w.Gap1 / Math.Max(a.EndTol, 1e-6)),
            _ => Math.Max(a.ZSeg / Math.Max(w.ZSeg.LastOrDefault(), 0.1), w.Gap2 / Math.Max(a.EndTol, 1e-6)),
        };
        return Math.Clamp(0.5 + 0.5 * (ratio - 1), 0.5, 1.0);
    }

    public static WireResult InspectWire(FImage img, Point2d p1, Point2d p2, PixelParams a)
    {
        double vx = p2.X - p1.X, vy = p2.Y - p1.Y;
        double L = Math.Max(1.0, Math.Sqrt(vx * vx + vy * vy));
        double ux = vx / L, uy = vy / L, nx = -uy, ny = ux;
        int W = a.HalfW + a.BgOff + a.BgW + a.D;
        int nt = (int)Math.Ceiling(L + 2 * a.Ext + 1 - 1e-9);
        var t = new double[nt];
        for (int j = 0; j < nt; j++) t[j] = -a.Ext + j;
        int ns = 2 * W + 1;

        var patch = new double[ns, nt];
        for (int i = 0; i < ns; i++)
            for (int j = 0; j < nt; j++)
            {
                double s = i - W;
                patch[i, j] = img.Sample(p1.X + t[j] * ux + s * nx, p1.Y + t[j] * uy + s * ny);
            }
        var ridge = new double[ns, nt];
        for (int i = a.D; i < ns - a.D; i++)
            for (int j = 0; j < nt; j++)
                ridge[i, j] = Math.Min(patch[i - a.D, j], patch[i + a.D, j]) - patch[i, j];
        var R = BlurRows(ridge, a.Along);

        var inside = t.Select(v => v >= 0 && v <= L).ToArray();
        int c0 = W;
        var bg = new List<double>();
        for (int j = 0; j < nt; j++)
        {
            if (!inside[j]) continue;
            for (int i = c0 - a.HalfW - a.BgOff - a.BgW; i < c0 - a.HalfW - a.BgOff; i++) bg.Add(R[i, j]);
            for (int i = c0 + a.HalfW + a.BgOff + 1; i < c0 + a.HalfW + a.BgOff + a.BgW + 1; i++) bg.Add(R[i, j]);
        }
        double mu = Median(bg), sd = 1.4826 * Median(bg.Select(v => Math.Abs(v - mu))) + 1e-6;

        int nc = 2 * a.HalfW + 1;
        var yc = new double[nt];
        var y1v = new double[nt];
        for (int j = 0; j < nt; j++)
        {
            int iy = 0;
            for (int k = 1; k < nc; k++) if (R[c0 - a.HalfW + k, j] > R[c0 - a.HalfW + iy, j]) iy = k;
            double y0 = R[c0 - a.HalfW + Math.Max(iy - 1, 0), j];
            double y1 = R[c0 - a.HalfW + iy, j];
            double y2 = R[c0 - a.HalfW + Math.Min(iy + 1, nc - 1), j];
            double den = y0 - 2 * y1 + y2;
            double dy = Math.Abs(den) > 1e-6 ? 0.5 * (y0 - y2) / den : 0;
            yc[j] = iy + Math.Clamp(dy, -0.5, 0.5) - a.HalfW;
            y1v[j] = y1;
        }

        var coef = new double[3];
        var sel = Enumerable.Range(0, nt).Where(j => inside[j] && (y1v[j] - mu) / sd > a.SelZ).ToArray();
        if (sel.Length >= 10)
        {
            var tt = sel.Select(j => t[j]).ToArray();
            var yy = sel.Select(j => yc[j]).ToArray();
            var w = Enumerable.Repeat(1.0, sel.Length).ToArray();
            for (int it = 0; it < 6; it++)
            {
                coef = PolyFit2(tt, yy, w);
                var r = tt.Select((x, k) => yy[k] - PolyVal(coef, x)).ToArray();
                double sc = 2 * (1.4826 * Median(r.Select(Math.Abs)) + 0.3);
                w = r.Select(v => 1 / (1 + Math.Pow(v / sc, 2))).ToArray();
            }
        }

        var zc = new double[nt];
        for (int j = 0; j < nt; j++)
        {
            double row = Math.Clamp(PolyVal(coef, t[j]) + c0, 0, ns - 1);
            int r0 = Math.Min((int)row, ns - 2);
            double f = row - r0;
            zc[j] = (R[r0, j] * (1 - f) + R[r0 + 1, j] * f - mu) / sd;
        }
        double Eff(int m) => Math.Sqrt(Math.Max(m, 1) / (double)a.Along);
        var zi = Enumerable.Range(0, nt).Where(j => inside[j]).Select(j => zc[j]).ToArray();
        double zAll = zi.Length > 0 ? zi.Average() * Eff(zi.Length) : 0;
        var zSeg = ArraySplit(zi, a.NSeg).Select(sg => sg.Length > 0 ? sg.Average() * Eff(sg.Length) : 0).ToArray();

        var ti = Enumerable.Range(0, nt).Where(j => inside[j]).Select(j => t[j]).ToArray();
        double e0 = PolyVal(coef, 0), eL = PolyVal(coef, L);
        double sweep = ti.Length > 0 ? ti.Max(x => Math.Abs(PolyVal(coef, x) - (e0 + (eL - e0) * x / L))) : 0;
        double shift = Math.Max(Math.Abs(e0), Math.Abs(eL));

        int half = a.Along / 2;
        var onIdx = new List<int>();
        for (int j = 0; j < nt; j++)
        {
            double s = 0;
            for (int k = j - half; k <= j + half; k++) if (k >= 0 && k < nt && zc[k] > a.OnZ) s += 1;
            if (s / a.Along > 0.6 && inside[j]) onIdx.Add(j);
        }
        double gap1 = onIdx.Count > 0 ? t[onIdx[0]] : L, gap2 = onIdx.Count > 0 ? L - t[onIdx[^1]] : L;
        // longest stretch without response between the two ends; zc is averaged over `along` px, which
        // shrinks a real gap by along-1, so that is added back
        double gapMid = 0;
        if (onIdx.Count > 0)
            for (int j = onIdx[0], run = 0; j <= onIdx[^1]; j++)
            {
                run = zc[j] <= a.OnZ ? run + 1 : 0;
                if (run > 0) gapMid = Math.Max(gapMid, run + a.Along - 1);
            }

        var (zb, bx, by) = BallScore(img, p1.X + e0 * nx, p1.Y + e0 * ny);

        var flags = new List<string>();
        if (zAll < a.ZMissing) flags.Add("MISSING");
        else
        {
            var mid = zSeg.Length > 2 ? zSeg[1..^1] : zSeg;
            if (mid.Min() < a.ZSeg || gapMid > a.GapMax) flags.Add("BROKEN");
            if (zb < a.ZBall || gap1 > a.EndTol) flags.Add("BOND1");
            if (zSeg[^1] < a.ZSeg || gap2 > a.EndTol) flags.Add("BOND2");
            if (sweep / L * 100 > a.SweepMaxPct) flags.Add("SWEEP");
            if (shift > a.ShiftMax) flags.Add("SHIFT");
        }

        return new WireResult
        {
            Flags = flags, ZAll = zAll, ZSeg = zSeg, ZBall = zb, Ball = [bx, by], SweepPx = sweep,
            SweepPct = sweep / L * 100, ShiftPx = shift, Gap1 = gap1, Gap2 = gap2, GapMid = gapMid, L = L,
            P1 = [p1.X, p1.Y], P2 = [p2.X, p2.Y],
            Curve = ti.Select(x => new[] { Math.Round(p1.X + x * ux + PolyVal(coef, x) * nx, 2),
                                           Math.Round(p1.Y + x * uy + PolyVal(coef, x) * ny, 2) }).ToList(),
        };
    }

    /// <summary>Dark-blob score near a bond point: (ring median - core mean) / ring MAD, best within ±search px.</summary>
    public static (double score, double x, double y) BallScore(FImage img, double px, double py,
        double rin = 1.5, double rout0 = 3, double rout1 = 5, int search = 3)
    {
        double best = double.NegativeInfinity, bx = px, by = py;
        int R = (int)rout1;
        var ring = new List<double>();
        for (int dy = -search; dy <= search; dy++)
            for (int dx = -search; dx <= search; dx++)
            {
                double cx = px + dx, cy = py + dy, core = 0;
                int nCore = 0;
                ring.Clear();
                for (int yy = -R; yy <= R; yy++)
                    for (int xx = -R; xx <= R; xx++)
                    {
                        double rr = Math.Sqrt(xx * xx + yy * yy);
                        double v = img.Sample(cx + xx, cy + yy);
                        if (rr <= rin) { core += v; nCore++; }
                        if (rr >= rout0 && rr <= rout1) ring.Add(v);
                    }
                double med = Median(ring);
                double sc = (med - core / nCore) / (1.4826 * Median(ring.Select(v => Math.Abs(v - med))) + 1e-6);
                if (sc > best) (best, bx, by) = (sc, cx, cy);
            }
        return (best, bx, by);
    }

    // ---- small numeric helpers (numpy equivalents)
    static double[,] BlurRows(double[,] src, int k)
    {
        int rows = src.GetLength(0), cols = src.GetLength(1), h = k / 2;
        var dst = new double[rows, cols];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
            {
                double s = 0;
                for (int m = j - h; m <= j + h; m++) s += src[i, Reflect101(m, cols)];
                dst[i, j] = s / k;
            }
        return dst;
    }

    static int Reflect101(int i, int n)
    {
        if (n == 1) return 0;
        while (i < 0 || i >= n) i = i < 0 ? -i : 2 * n - 2 - i;
        return i;
    }

    public static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length == 0) return 0;
        Array.Sort(a);
        int m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : 0.5 * (a[m - 1] + a[m]);
    }

    static double[][] ArraySplit(double[] a, int n)
    {
        var parts = new double[n][];
        int q = a.Length / n, r = a.Length % n, pos = 0;
        for (int i = 0; i < n; i++)
        {
            int len = q + (i < r ? 1 : 0);
            parts[i] = a[pos..(pos + len)];
            pos += len;
        }
        return parts;
    }

    public static double PolyVal(double[] c, double x) => (c[0] * x + c[1]) * x + c[2];

    /// <summary>Weighted least-squares quadratic (coefficients highest power first, like numpy.polyfit).</summary>
    public static double[] PolyFit2(double[] x, double[] y, double[] w)
    {
        var A = new double[3, 3];
        var b = new double[3];
        for (int k = 0; k < x.Length; k++)
        {
            double[] row = [x[k] * x[k], x[k], 1];
            for (int i = 0; i < 3; i++)
            {
                b[i] += w[k] * row[i] * y[k];
                for (int j = 0; j < 3; j++) A[i, j] += w[k] * row[i] * row[j];
            }
        }
        return Solve3(A, b) ?? [0, 0, 0];
    }

    /// <summary>3×3 linear system (Gauss-Jordan, partial pivoting); null when singular.</summary>
    internal static double[]? Solve3(double[,] A, double[] b)
    {
        int n = 3;
        var M = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) M[i, j] = A[i, j];
            M[i, n] = b[i];
        }
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[p, c])) p = r;
            if (Math.Abs(M[p, c]) < 1e-12) return null;
            for (int j = 0; j <= n; j++) (M[c, j], M[p, j]) = (M[p, j], M[c, j]);
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = M[r, c] / M[c, c];
                for (int j = c; j <= n; j++) M[r, j] -= f * M[c, j];
            }
        }
        return [M[0, n] / M[0, 0], M[1, n] / M[1, 1], M[2, n] / M[2, 2]];
    }
}
