using OpenCvSharp;

namespace WireInspect.Core;

/// <summary>Semi-automatic product setup: find the wires inside a unit template, calibrate the pixel size.</summary>
public static class AutoWires
{
    public static List<WireSpec> Find(Mat tpl8, Product p)
    {
        var a = p.Params();
        using var f = new Mat();
        tpl8.ConvertTo(f, MatType.CV_32F);
        if (!a.Dark) Cv2.Subtract(new Scalar(255), f, f);
        using var img = new Mat();
        Cv2.GaussianBlur(f, img, new Size(0, 0), 0.7);

        int len = Math.Max(9, a.Along * 3);
        const double zThr = 2.0;   // loose: every candidate is checked again by Inspector.InspectWire below
        using var resp = new Mat(img.Size(), MatType.CV_32F, Scalar.All(float.MinValue));
        // a wire is darker than BOTH sides and its two sides look alike; a frame edge (dark rim between
        // bright metal and dark background) is not symmetric, so |side+ - side-| is subtracted
        for (int deg = 0; deg < 180; deg += 15)
        {
            double th = deg * Math.PI / 180;
            using var kc = LineKernel(th, len, 0);
            using var kp = LineKernel(th, len, a.D);
            using var km = LineKernel(th, len, -a.D);
            using var kp3 = LineKernel(th, len, 3 * a.D);
            using var km3 = LineKernel(th, len, -3 * a.D);
            using Mat c = new(), sp = new(), sm = new(), sp3 = new(), sm3 = new(), lo = new(), diff = new(), r = new();
            Cv2.Filter2D(img, c, MatType.CV_32F, kc);
            Cv2.Filter2D(img, sp, MatType.CV_32F, kp);
            Cv2.Filter2D(img, sm, MatType.CV_32F, km);
            Cv2.Filter2D(img, sp3, MatType.CV_32F, kp3);
            Cv2.Filter2D(img, sm3, MatType.CV_32F, km3);
            Cv2.Min(sp, sm, lo);
            Cv2.Subtract(lo, c, r);
            Cv2.Absdiff(sp, sm, diff);
            Cv2.ScaleAdd(diff, -1.0, r, r);
            Cv2.Absdiff(sp3, sm3, diff);
            Cv2.ScaleAdd(diff, -0.5, r, r);
            Cv2.Max(resp, r, resp);
        }
        int margin = len / 2 + 3 * a.D + 1;   // filter responses near the template border are not reliable
        if (resp.Width > 2 * margin && resp.Height > 2 * margin)
        {
            using var inner = new Mat(resp, new Rect(margin, margin, resp.Width - 2 * margin, resp.Height - 2 * margin)).Clone();
            resp.SetTo(Scalar.All(float.MinValue));
            using var dst = new Mat(resp, new Rect(margin, margin, inner.Width, inner.Height));
            inner.CopyTo(dst);
        }
        resp.GetArray(out float[] all);
        var vals = all.Where(v => v > float.MinValue).ToArray();
        double med = Inspector.Median(vals.Select(v => (double)v));
        double mad = 1.4826 * Inspector.Median(vals.Select(v => Math.Abs(v - med))) + 1e-6;
        using var z = new Mat();
        Cv2.Subtract(resp, new Scalar(med), z);
        Cv2.Multiply(z, new Scalar(1 / mad), z);
        using var mask = new Mat();
        Cv2.Threshold(z, mask, zThr, 255, ThresholdTypes.Binary);
        mask.ConvertTo(mask, MatType.CV_8U);

        double minLen = Math.Max(12, 0.15 * Math.Min(tpl8.Width, tpl8.Height));
        var segs = Cv2.HoughLinesP(mask, 1, Math.PI / 180, (int)Math.Max(10, minLen / 2), minLen, Math.Max(3, a.Along))
            .Select(s => (x1: (double)s.P1.X, y1: (double)s.P1.Y, x2: (double)s.P2.X, y2: (double)s.P2.Y)).ToList();
        var merged = Merge(segs, Math.Max(3, 2 * p.Px(p.WireDiamUm)), Math.Max(6, 2 * a.Along))
            .Where(s => Math.Sqrt(Math.Pow(s.x2 - s.x1, 2) + Math.Pow(s.y2 - s.y1, 2)) >= minLen).ToList();

        var fimg = FImage.FromMat(img);
        var kept = new List<(double x1, double y1, double x2, double y2)>();
        double perpTol = Math.Max(3, 2 * p.Px(p.WireDiamUm));
        foreach (var s in merged.Select(s0 => Refine(fimg, s0, a)).OrderByDescending(SegLen))
        {
            if (kept.Any(k => Same(k, s, perpTol, -0.5 * SegLen(s)))) continue;   // a piece of a wire already kept
            if (Inspector.InspectWire(fimg, new Point2d(s.x1, s.y1), new Point2d(s.x2, s.y2), a).ZAll >= p.ZMissing) kept.Add(s);
        }

        var outList = new List<WireSpec>();
        double cx = tpl8.Width / 2.0, cy = tpl8.Height / 2.0;
        foreach (var s in kept)
        {
            // bond 1 = the end with the stronger ball (the score the BOND1 check uses); the found end may
            // stop short of or run past the ball, so look a few px along the wire and snap to the ball
            var e1 = BestBall(fimg, s.x1, s.y1, s.x2, s.y2);
            var e2 = BestBall(fimg, s.x2, s.y2, s.x1, s.y1);
            double d1 = Math.Pow(s.x1 - cx, 2) + Math.Pow(s.y1 - cy, 2), d2 = Math.Pow(s.x2 - cx, 2) + Math.Pow(s.y2 - cy, 2);
            var w = e2.score > e1.score + 1 || (Math.Abs(e2.score - e1.score) <= 1 && d2 < d1)
                ? new WireSpec(e2.x, e2.y, s.x1, s.y1)
                : new WireSpec(e1.x, e1.y, s.x2, s.y2);
            w.P1 = [Math.Round(w.P1[0], 1), Math.Round(w.P1[1], 1)];
            w.P2 = [Math.Round(w.P2[0], 1), Math.Round(w.P2[1], 1)];
            outList.Add(w);
        }
        return outList.OrderBy(w => Math.Round((w.P1[1] + w.P2[1]) / 2 / 10)).ThenBy(w => (w.P1[0] + w.P2[0]) / 2).Take(64).ToList();
    }

    /// <summary>Strongest ball within 12 px inward/outward of an end, searched along the wire.</summary>
    static (double score, double x, double y) BestBall(FImage img, double x, double y, double ox, double oy)
    {
        double l = Math.Max(1e-6, Math.Sqrt((ox - x) * (ox - x) + (oy - y) * (oy - y))), ux = (ox - x) / l, uy = (oy - y) / l;
        var best = (score: double.NegativeInfinity, x, y);
        for (int k = -12; k <= 12; k += 3)
        {
            var b = Inspector.BallScore(img, x + ux * k, y + uy * k);
            if (b.score > best.score) best = b;
        }
        return best;
    }

    /// <summary>Grow a candidate along its direction (inside the template) and trim it to where the wire
    /// response really is: the filter only sees the strong middle part of a faint wire.</summary>
    static (double x1, double y1, double x2, double y2) Refine(FImage img, (double x1, double y1, double x2, double y2) s,
        PixelParams a)
    {
        for (int it = 0; it < 3; it++)
        {
            double l = SegLen(s), ux = (s.x2 - s.x1) / l, uy = (s.y2 - s.y1) / l, grow = Math.Max(10, 0.4 * l);
            double Lim(double v, int n) => Math.Clamp(v, a.D + 1, n - a.D - 2);
            var e = (x1: Lim(s.x1 - ux * grow, img.W), y1: Lim(s.y1 - uy * grow, img.H),
                     x2: Lim(s.x2 + ux * grow, img.W), y2: Lim(s.y2 + uy * grow, img.H));
            var r = Inspector.InspectWire(img, new Point2d(e.x1, e.y1), new Point2d(e.x2, e.y2), a);
            if (r.Gap1 + r.Gap2 >= r.L - 4) return s;
            double el = SegLen(e), ex = (e.x2 - e.x1) / el, ey = (e.y2 - e.y1) / el;
            var n = (x1: e.x1 + ex * r.Gap1, y1: e.y1 + ey * r.Gap1, x2: e.x2 - ex * r.Gap2, y2: e.y2 - ey * r.Gap2);
            // keep an extension only where it is a wire on its own (not just faint texture)
            bool Keep(double ax, double ay, double bx, double by)
            {
                double pl = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                return pl < 3 || Inspector.InspectWire(img, new Point2d(ax, ay), new Point2d(bx, by), a).ZAll >= a.ZSeg;
            }
            bool grow1 = (n.x1 - s.x1) * ux + (n.y1 - s.y1) * uy < 0, grow2 = (n.x2 - s.x2) * ux + (n.y2 - s.y2) * uy > 0;
            if (grow1 && !Keep(n.x1, n.y1, s.x1, s.y1)) (n.x1, n.y1) = (s.x1, s.y1);
            if (grow2 && !Keep(s.x2, s.y2, n.x2, n.y2)) (n.x2, n.y2) = (s.x2, s.y2);
            if (Math.Abs(SegLen(n) - l) < 1) return n;
            s = n;
        }
        return s;
    }

    /// <summary>Averaging kernel along a line of length len at angle theta, offset off px along its normal.</summary>
    static Mat LineKernel(double theta, int len, int off)
    {
        int size = len + 2 * Math.Abs(off) + 3;
        if (size % 2 == 0) size++;
        double c = size / 2, dx = Math.Cos(theta), dy = Math.Sin(theta), nx = -dy, ny = dx;
        var idx = new float[size, size];
        int steps = len * 4;
        for (int i = 0; i <= steps; i++)
        {
            double s = -len / 2.0 + len * i / (double)steps;
            double x = c + s * dx + off * nx, y = c + s * dy + off * ny;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0, w = 1.0 / (steps + 1);
            idx[y0, x0] += (float)(w * (1 - fx) * (1 - fy));
            idx[y0, x0 + 1] += (float)(w * fx * (1 - fy));
            idx[y0 + 1, x0] += (float)(w * (1 - fx) * fy);
            idx[y0 + 1, x0 + 1] += (float)(w * fx * fy);
        }
        var k = new Mat(size, size, MatType.CV_32F);
        k.SetArray(idx.Cast<float>().ToArray());
        return k;
    }

    /// <summary>Union collinear, overlapping or nearly touching segments into one per wire.</summary>
    static List<(double x1, double y1, double x2, double y2)> Merge(List<(double x1, double y1, double x2, double y2)> segs,
        double perpTol, double gapTol)
    {
        int n = segs.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Same(segs[i], segs[j], perpTol, gapTol)) parent[Find(i)] = Find(j);
        var result = new List<(double, double, double, double)>();
        foreach (var g in Enumerable.Range(0, n).GroupBy(Find))
        {
            var members = g.Select(i => segs[i]).ToList();
            var longest = members.OrderByDescending(SegLen).First();
            double l = SegLen(longest), ux = (longest.x2 - longest.x1) / l, uy = (longest.y2 - longest.y1) / l;
            var pts = members.SelectMany(s => new[] { (s.x1, s.y1), (s.x2, s.y2) }).ToList();
            double mx = pts.Average(q => q.Item1), my = pts.Average(q => q.Item2);
            var proj = pts.Select(q => (q.Item1 - mx) * ux + (q.Item2 - my) * uy).ToList();
            double lo = proj.Min(), hi = proj.Max();
            result.Add((mx + lo * ux, my + lo * uy, mx + hi * ux, my + hi * uy));
        }
        return result;
    }

    static double SegLen((double x1, double y1, double x2, double y2) s)
        => Math.Max(1e-6, Math.Sqrt(Math.Pow(s.x2 - s.x1, 2) + Math.Pow(s.y2 - s.y1, 2)));

    static bool Same((double x1, double y1, double x2, double y2) a, (double x1, double y1, double x2, double y2) b,
        double perpTol, double gapTol)
    {
        double la = SegLen(a), ux = (a.x2 - a.x1) / la, uy = (a.y2 - a.y1) / la;
        double lb = SegLen(b), vx = (b.x2 - b.x1) / lb, vy = (b.y2 - b.y1) / lb;
        if (Math.Abs(ux * vx + uy * vy) < Math.Cos(12 * Math.PI / 180)) return false;
        double Perp(double x, double y) => Math.Abs((x - a.x1) * -uy + (y - a.y1) * ux);
        if (Perp(b.x1, b.y1) > perpTol || Perp(b.x2, b.y2) > perpTol) return false;
        double t1 = (b.x1 - a.x1) * ux + (b.y1 - a.y1) * uy, t2 = (b.x2 - a.x1) * ux + (b.y2 - a.y1) * uy;
        double lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
        return lo <= la + gapTol && hi >= -gapTol;
    }

    /// <summary>µm per pixel from the known horizontal unit pitch (mm): median spacing of side-by-side units.</summary>
    public static double? CalibratePitch(Mat gray8, Mat tpl8, double matchThr, double pitchMm)
    {
        var units = Inspector.FindUnits(gray8, tpl8, matchThr);
        var dx = new List<double>();
        foreach (var u in units)
        {
            var right = units.Where(v => v.Box[0] > u.Box[0] && Math.Abs(v.Box[1] - u.Box[1]) < tpl8.Height / 2.0)
                .Select(v => (double)(v.Box[0] - u.Box[0])).DefaultIfEmpty(double.NaN).Min();
            if (!double.IsNaN(right)) dx.Add(right);
        }
        return dx.Count == 0 || pitchMm <= 0 ? null : pitchMm * 1000 / Inspector.Median(dx);
    }
}
