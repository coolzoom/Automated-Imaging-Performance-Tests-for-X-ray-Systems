using OpenCvSharp;

namespace WireInspect.Core;

/// <summary>Geometry correction for rotated / distorted images (same steps as py/core/align.py).
/// 1. Units at any angle: the centre square of the unit template (side min(w, h)/√2, inside the template at every
///    rotation) is matched on a half-size image at every `step`° over ±max angle (up to the full circle); the
///    per-pixel best score over all angles gives unit candidates (plus the upright template matches). Each
///    candidate is cut out upright (own bilinear warp), its angle refined (0.5°, then 0.1°) with the full
///    template, kept when the score reaches the match threshold. Every unit gets its own angle: parts may lie in
///    any direction.
/// 2. Local distortion: each unit is split into 2×2 template blocks, every block is matched within ±search px
///    (sub-pixel peak) in the upright cut-out, and an affine map is fitted to the block centres, so the wire end
///    points follow the local rotation / scale / shear of that unit (perspective, lens distortion).
/// Result per unit: the affine template -> original image; wires are then measured on the original image.</summary>
public static class Align
{
    const double MinAngle = 0.2, MinGain = 0.01;        // a unit counts as upright unless rotated by more and matching clearly better
    const double CandFactor = 0.75;                     // coarse candidates: square-crop score >= CandFactor × match threshold
    const int MaxCandidates = 256;
    const double EarlyFactor = 0.7;                     // a candidate scoring below EarlyFactor × threshold at its coarse angle is dropped
    public const double SameDirection = 0.9;            // units count as one direction when |mean unit vector| reaches this
    const double BlockMinScore = 0.5, MaxLinearDev = 0.08, MaxResidual = 1.5;

    /// <summary>2×3 affine [a, b, c, d, e, f]: x' = a·x + b·y + c, y' = d·x + e·y + f.</summary>
    public static double[] Rotation(double cx, double cy, double deg)
    {
        double t = deg * (Math.PI / 180), a = Math.Cos(t), b = Math.Sin(t);
        return [a, b, (1 - a) * cx - b * cy, -b, a, b * cx + (1 - a) * cy];
    }

    public static double[] Translation(double x, double y) => [1, 0, x, 0, 1, y];

    public static double[] Invert(double[] m)
    {
        double det = m[0] * m[4] - m[1] * m[3];
        double ia = m[4] / det, ib = -m[1] / det, ic = -m[3] / det, id = m[0] / det;
        return [ia, ib, -(ia * m[2] + ib * m[5]), ic, id, -(ic * m[2] + id * m[5])];
    }

    /// <summary>m2 ∘ m1 (apply m1 first).</summary>
    public static double[] Compose(double[] m2, double[] m1) =>
    [
        m2[0] * m1[0] + m2[1] * m1[3], m2[0] * m1[1] + m2[1] * m1[4], m2[0] * m1[2] + m2[1] * m1[5] + m2[2],
        m2[3] * m1[0] + m2[4] * m1[3], m2[3] * m1[1] + m2[4] * m1[4], m2[3] * m1[2] + m2[4] * m1[5] + m2[5],
    ];

    public static (double x, double y) Apply(double[] m, double x, double y) => (m[0] * x + m[1] * y + m[2], m[3] * x + m[4] * y + m[5]);

    /// <summary>Affine warp (m = source -> destination) with bilinear sampling and edge replication; done here
    /// instead of Cv2.WarpAffine so the Python version gives the very same pixels (OpenCV versions differ).</summary>
    public static Mat Warp(Mat gray8, double[] m, int w, int h) => WarpBack(FImage.FromMat(gray8), Invert(m), w, h);

    /// <summary>Like Warp, with mi = destination -> source.</summary>
    public static Mat WarpBack(FImage src, double[] mi, int w, int h)
    {
        var buf = new byte[w * h];
        for (int v = 0; v < h; v++)
            for (int u = 0; u < w; u++)
            {
                double s = src.Sample(mi[0] * u + mi[1] * v + mi[2], mi[3] * u + mi[4] * v + mi[5]);
                buf[v * w + u] = (byte)Math.Clamp(Math.Floor(s + 0.5), 0, 255);
            }
        var dst = new Mat(h, w, MatType.CV_8U);
        dst.SetArray(buf);
        return dst;
    }

    /// <summary>Rotate the whole image by deg on an enlarged canvas (nothing cut off); m = original -> new.</summary>
    public static Mat RotateImage(Mat gray8, double deg, out double[] m)
    {
        int w = gray8.Width, h = gray8.Height;
        double t = deg * (Math.PI / 180);
        int nw = (int)Math.Ceiling(w * Math.Abs(Math.Cos(t)) + h * Math.Abs(Math.Sin(t)) - 1e-9);
        int nh = (int)Math.Ceiling(w * Math.Abs(Math.Sin(t)) + h * Math.Abs(Math.Cos(t)) - 1e-9);
        m = Rotation(w / 2.0, h / 2.0, deg);
        m[2] += nw / 2.0 - w / 2.0;
        m[5] += nh / 2.0 - h / 2.0;
        return Warp(gray8, m, nw, nh);
    }

    /// <summary>Test aid: the image rotated by deg and cut to the largest centred upright rectangle with no
    /// border fill, like a photo of a rotated part.</summary>
    public static Mat RotateInscribed(Mat gray8, double deg)
    {
        using var r = RotateImage(gray8, deg, out _);
        int w = gray8.Width, h = gray8.Height;
        double c = Math.Abs(Math.Cos(deg * (Math.PI / 180))), s = Math.Abs(Math.Sin(deg * (Math.PI / 180)));
        int cw = c >= s ? (int)(w * c - h * s) : (int)(h * s - w * c), ch = c >= s ? (int)(h * c - w * s) : (int)(w * s - h * c);
        if (cw < 16 || ch < 16) throw new ArgumentException("旋转角度太大，裁剪后没有图像");
        return new Mat(r, new Rect((r.Width - cw) / 2, (r.Height - ch) / 2, cw, ch)).Clone();
    }

    static double[]? FitAffine(List<(double sx, double sy, double dx, double dy, double w)> pts)
    {
        var A = new double[3, 3];
        var bx = new double[3];
        var by = new double[3];
        foreach (var (sx, sy, dx, dy, w) in pts)
        {
            double[] row = [sx, sy, 1.0];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) A[i, j] += w * (row[i] * row[j]);
                bx[i] += w * dx * row[i];
                by[i] += w * dy * row[i];
            }
        }
        var ux = Inspector.Solve3(A, bx);
        var uy = Inspector.Solve3(A, by);
        return ux == null || uy == null ? null : [ux[0], ux[1], ux[2], uy[0], uy[1], uy[2]];
    }

    /// <summary>Affine template -> image for one unit found at box (x, y, w, h); the pure translation when the
    /// blocks do not give a trustworthy fit.</summary>
    public static double[] AlignUnit(Mat gray8, Mat tpl8, int[] box, int search)
    {
        int x = box[0], y = box[1], tw = box[2], th = box[3];
        var baseM = Translation(x, y);
        int bw = tw / 2, bh = th / 2;
        if (search <= 0 || bw < 8 || bh < 8) return baseM;
        int W = gray8.Width, H = gray8.Height;
        var pts = new List<(double sx, double sy, double dx, double dy, double w)>();
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                int bx = j * bw, by = i * bh, x0 = x + bx - search, y0 = y + by - search;
                if (x0 < 0 || y0 < 0 || x0 + bw + 2 * search > W || y0 + bh + 2 * search > H) continue;
                using var region = new Mat(gray8, new Rect(x0, y0, bw + 2 * search, bh + 2 * search));
                using var block = new Mat(tpl8, new Rect(bx, by, bw, bh));
                using var res = new Mat();
                Cv2.MatchTemplate(region, block, res, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(res, out _, out double mx, out _, out Point loc);
                if (double.IsNaN(mx) || mx < BlockMinScore) continue;
                static double Peak(double a, double b, double c)
                {
                    double den = a - 2 * b + c;
                    return Math.Abs(den) > 1e-9 ? Math.Clamp(0.5 * (a - c) / den, -0.5, 0.5) : 0.0;
                }
                int n = 2 * search, lx = loc.X, ly = loc.Y;
                double dx = lx > 0 && lx < n ? Peak(res.At<float>(ly, lx - 1), res.At<float>(ly, lx), res.At<float>(ly, lx + 1)) : 0.0;
                double dy = ly > 0 && ly < n ? Peak(res.At<float>(ly - 1, lx), res.At<float>(ly, lx), res.At<float>(ly + 1, lx)) : 0.0;
                double sx = bx + (bw - 1) / 2.0, sy = by + (bh - 1) / 2.0;
                pts.Add((sx, sy, x + sx + lx + dx - search, y + sy + ly + dy - search, mx));
            }
        if (pts.Count < 3) return baseM;
        var m = FitAffine(pts);
        if (m != null && pts.Count >= 4)
        {
            var res = pts.Select(p =>
            {
                var (qx, qy) = Apply(m, p.sx, p.sy);
                double ex = qx - p.dx, ey = qy - p.dy;
                return Math.Sqrt(ex * ex + ey * ey);
            }).ToList();
            int k = res.IndexOf(res.Max());
            if (res[k] > MaxResidual) m = FitAffine(pts.Where((_, i) => i != k).ToList());
        }
        if (m == null || Math.Max(Math.Max(Math.Abs(m[0] - 1), Math.Abs(m[1])), Math.Max(Math.Abs(m[3]), Math.Abs(m[4] - 1))) > MaxLinearDev)
            return baseM;
        var (cx, cy) = Apply(m, tw / 2.0, th / 2.0);
        if (Math.Abs(cx - (x + tw / 2.0)) > search + 1 || Math.Abs(cy - (y + th / 2.0)) > search + 1) return baseM;
        return m;
    }

    static Mat Resize(Mat img, double f)
    {
        var d = new Mat();
        Cv2.Resize(img, d, new Size(Math.Max(1, (int)Math.Round(img.Width * f)), Math.Max(1, (int)Math.Round(img.Height * f))),
            interpolation: InterpolationFlags.Area);
        return d;
    }

    /// <summary>Angle in tenths of a degree -> (-1800, 1800].</summary>
    static int Norm10(int k)
    {
        while (k <= -1800) k += 3600;
        while (k > 1800) k -= 3600;
        return k;
    }

    /// <summary>Patch (pw × ph, upright unit) -> image: the patch centre lands on (cx, cy), content rotated by deg.</summary>
    public static double[] PatchMap(double cx, double cy, double deg, int pw, int ph)
    {
        double t = deg * (Math.PI / 180), a = Math.Cos(t), b = Math.Sin(t), pcx = pw / 2.0, pcy = ph / 2.0;
        return [a, b, cx - a * pcx - b * pcy, -b, a, cy + b * pcx - a * pcy];
    }

    /// <summary>Coarse angle step (whole degrees) for a centre square of side s: its edge turns by about 2 px per step.</summary>
    public static int CoarseStep(int s) => Math.Max(1, Math.Min(6, (int)(Math.Atan(2.0 / Math.Max(s / 2.0, 1.0)) * (180.0 / Math.PI))));

    /// <summary>Circular mean (deg) of unit angles; 0 for none, or when the angles scatter (|mean vector| &lt; minResultant).</summary>
    public static double MeanAngle(IReadOnlyList<double> angles, double minResultant = 0)
    {
        if (angles.Count == 0) return 0;
        double sn = 0, cs = 0;
        foreach (var v in angles) sn += Math.Sin(v * (Math.PI / 180));
        foreach (var v in angles) cs += Math.Cos(v * (Math.PI / 180));
        if (Math.Sqrt(sn * sn + cs * cs) < minResultant * angles.Count) return 0;
        return Math.Atan2(sn, cs) * (180.0 / Math.PI);
    }

    /// <summary>Row by row, in the frame of the common unit direction (image frame when the units point every way).</summary>
    public static List<UnitResult> SortUnits(List<UnitResult> units, int tw, int th)
    {
        double phi = MeanAngle(units.Select(u => u.Angle).ToList(), SameDirection) * (Math.PI / 180), ca = Math.Cos(phi), sb = Math.Sin(phi);
        units = units.OrderBy(u => { var (qx, qy) = Apply(u.Map(), tw / 2.0, th / 2.0); return Math.Round((sb * qx + ca * qy) / th); })
            .ThenBy(u => { var (qx, qy) = Apply(u.Map(), tw / 2.0, th / 2.0); return ca * qx - sb * qy; }).ToList();
        for (int i = 0; i < units.Count; i++) units[i].Index = i;
        return units;
    }

    /// <summary>Units found with the template and with its mirror image (strip flipped over): one side per picture,
    /// the mirrored one only when it matches clearly better where both sides found a unit (no common place: more
    /// units). Deciding per unit is not safe: a unit cut off by the image border, or a nearly symmetric frame,
    /// often fits the mirrored template too. Mirrored affines are made to take original template coordinates.</summary>
    public static List<UnitResult> MergeMirrored(List<UnitResult> units, List<UnitResult> mirrored, int tw, int th)
    {
        double[] flip = [-1, 0, tw - 1.0, 0, 1, 0];
        foreach (var u in mirrored)
        {
            var m = Compose(u.Map(), flip);
            u.Affine = m;
            u.Box[0] = (int)Math.Round(m[2]);
            u.Box[1] = (int)Math.Round(m[5]);
            u.Mirror = true;
        }
        double dmin2 = Math.Pow(0.5 * Math.Min(tw, th), 2);
        var pairs = new List<(double n, double m)>();   // places both sides found: (normal score, mirrored score)
        foreach (var u in units)
        {
            var (ux, uy) = Apply(u.Map(), tw / 2.0, th / 2.0);
            foreach (var v in mirrored)
            {
                var (vx, vy) = Apply(v.Map(), tw / 2.0, th / 2.0);
                if ((ux - vx) * (ux - vx) + (uy - vy) * (uy - vy) < dmin2) { pairs.Add((u.Match, v.Match)); break; }
            }
        }
        bool useMirror = pairs.Count > 0
            ? pairs.Sum(q => q.m) / pairs.Count > pairs.Sum(q => q.n) / pairs.Count + MinGain
            : mirrored.Count > units.Count;
        if (useMirror) return SortUnits(mirrored, tw, th);
        return units;
    }

    sealed record Pose(double Score, Point Loc, double[] Map, Mat Patch);

    /// <summary>Units at any angle within ±maxAngle; Affine = template -> image, Angle per unit.</summary>
    public static List<UnitResult> FindUnitsRotated(Mat gray8, Mat tpl8, double matchThr, double maxAngle, int search, bool localAlign)
    {
        int tw = tpl8.Width, th = tpl8.Height, W = gray8.Width, H = gray8.Height;
        if (tw > W || th > H) return [];
        // ---- 1. coarse: centre square at every step°, best score per pixel
        double f = Math.Min(tw, th) >= 80 ? 0.5 : 1.0;
        using var small = f != 1.0 ? Resize(gray8, f) : gray8.Clone();
        using var tsm = f != 1.0 ? Resize(tpl8, f) : tpl8.Clone();
        int stw = tsm.Width, sth = tsm.Height;
        int s = Math.Max(8, (int)Math.Floor(Math.Min(stw, sth) / Math.Sqrt(2)));
        s = Math.Min(s, Math.Min(stw, sth));
        int cx = stw / 2, cy = sth / 2, h0 = s / 2, x0 = Math.Max(0, cx - h0), y0 = Math.Max(0, cy - h0);
        int step = CoarseStep(s);
        int n = (int)Math.Floor(Math.Min(maxAngle, 180.0) / step + 1e-9);
        var angles = Enumerable.Range(-n, 2 * n + 1).Select(k => Norm10(k * step * 10)).Distinct().Order().ToList();
        float[]? best = null;
        int[]? ang = null;
        int rw = 0, rh = 0;
        foreach (var k10 in angles)
        {
            using var rot = Warp(tsm, Rotation(cx, cy, k10 / 10.0), stw, sth);
            using var crop = new Mat(rot, new Rect(x0, y0, s, s));
            using var res = new Mat();
            Cv2.MatchTemplate(small, crop, res, TemplateMatchModes.CCoeffNormed);
            res.GetArray(out float[] r);
            for (int i = 0; i < r.Length; i++) if (float.IsNaN(r[i])) r[i] = -1;
            if (best == null)
            {
                (best, rw, rh) = (r, res.Width, res.Height);
                ang = Enumerable.Repeat(k10, r.Length).ToArray();
            }
            else
                for (int i = 0; i < r.Length; i++)
                    if (r[i] > best[i]) (best[i], ang![i]) = (r[i], k10);
        }
        var cands = new List<(double x, double y, int k0)>();
        if (best != null)
        {
            using var bm = new Mat(rh, rw, MatType.CV_32F);
            bm.SetArray(best);
            int d = Math.Max(2, (int)(0.5 * Math.Min(stw, sth)));
            while (cands.Count < MaxCandidates)
            {
                Cv2.MinMaxLoc(bm, out _, out double mx, out _, out Point p);
                if (mx < CandFactor * matchThr) break;
                // the crop was cut from the rotated template: the template centre sits at (cx - x0, cy - y0) in it
                cands.Add(((p.X + cx - x0) / f, (p.Y + cy - y0) / f, ang![p.Y * rw + p.X]));
                int ax0 = Math.Max(0, p.X - d), ay0 = Math.Max(0, p.Y - d);
                bm[new Rect(ax0, ay0, Math.Min(rw, p.X + d + 1) - ax0, Math.Min(rh, p.Y + d + 1) - ay0)].SetTo(new Scalar(-1));
            }
        }
        // upright matches, after the peaks: they only rescue units the coarse search missed
        foreach (var u in Inspector.FindUnits(gray8, tpl8, matchThr))
        {
            double ux = u.Box[0] + tw / 2.0, uy = u.Box[1] + th / 2.0;
            int k0 = 0;
            if (ang != null)
            {
                int px = (int)Math.Clamp(Math.Round(ux * f - cx + x0), 0, rw - 1), py = (int)Math.Clamp(Math.Round(uy * f - cy + y0), 0, rh - 1);
                k0 = ang[py * rw + px];
            }
            cands.Add((ux, uy, k0));
        }
        // ---- 2. refine each candidate upright, full template
        int R = Math.Max(8, (int)Math.Round(0.1 * Math.Min(tw, th)));
        int pw = tw + 2 * R, ph = th + 2 * R, half = (int)Math.Ceiling(step * 10 / 2.0 / 5);
        var fimg = FImage.FromMat(gray8);
        double dmin2 = Math.Pow(0.5 * Math.Min(tw, th), 2);
        var found = new List<(double sc, int k, Point loc, double[] mp, Mat patch, double ccx, double ccy)>();
        var allPatches = new List<Mat>();
        try
        {
            foreach (var (ux, uy, k0start) in cands)
            {
                if (found.Any(q => (ux - q.ccx) * (ux - q.ccx) + (uy - q.ccy) * (uy - q.ccy) < dmin2)) continue;   // a unit already found here
                var cache = new Dictionary<int, Pose>();
                Pose P(int k10)
                {
                    if (cache.TryGetValue(k10, out var v)) return v;
                    var mp = PatchMap(ux, uy, k10 / 10.0, pw, ph);
                    var patch = WarpBack(fimg, mp, pw, ph);
                    allPatches.Add(patch);
                    using var res = new Mat();
                    Cv2.MatchTemplate(patch, tpl8, res, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(res, out _, out double mx, out _, out Point loc);
                    return cache[k10] = new Pose(double.IsNaN(mx) ? -1.0 : mx, loc, mp, patch);
                }
                int Pick(IEnumerable<int> ks)
                {
                    int? b = null;
                    foreach (var k in ks)
                        if (b == null || P(k).Score > P(b.Value).Score || (P(k).Score == P(b.Value).Score && Math.Abs(k) < Math.Abs(b.Value))) b = k;
                    return b!.Value;
                }
                int k0 = k0start;
                if (P(Norm10(k0 + 1800)).Score > P(k0).Score) k0 = Norm10(k0 + 1800);   // nearly symmetric units: the other way round fits better
                if (P(k0).Score < EarlyFactor * matchThr) continue;
                int kk = Pick(Enumerable.Range(-half, 2 * half + 1).Select(j => k0 + j * 5));
                kk = Pick(Enumerable.Range(-3, 7).Select(j => kk + j));
                if (Math.Abs(kk) <= 20 && P(0).Score + MinGain >= P(kk).Score) kk = 0;
                var pose = P(kk);
                if (pose.Score < matchThr) continue;
                var (ccx, ccy) = Apply(pose.Map, pose.Loc.X + tw / 2.0, pose.Loc.Y + th / 2.0);
                found.Add((pose.Score, Norm10(kk), pose.Loc, pose.Map, pose.Patch, ccx, ccy));
            }
            // ---- 3. one unit per place, local distortion, keep units fully inside the image
            var units = new List<UnitResult>();
            var kept = new List<(double x, double y)>();
            foreach (var q in found.OrderByDescending(q => q.sc))
            {
                if (kept.Any(c => (q.ccx - c.x) * (q.ccx - c.x) + (q.ccy - c.y) * (q.ccy - c.y) < dmin2)) continue;
                kept.Add((q.ccx, q.ccy));
                var lm = localAlign ? AlignUnit(q.patch, tpl8, [q.loc.X, q.loc.Y, tw, th], search) : Translation(q.loc.X, q.loc.Y);
                var m = Compose(q.mp, lm);
                var corners = new (double, double)[] { (0, 0), (tw, 0), (0, th), (tw, th) };
                if (!corners.All(c => { var (qx, qy) = Apply(m, c.Item1, c.Item2); return qx >= -1 && qx <= W && qy >= -1 && qy <= H; })) continue;
                units.Add(new UnitResult { Box = [(int)Math.Round(m[2]), (int)Math.Round(m[5]), tw, th], Match = q.sc, Angle = q.k / 10.0, Affine = m });
            }
            return SortUnits(units, tw, th);
        }
        finally
        {
            foreach (var p in allPatches) p.Dispose();
        }
    }
}
