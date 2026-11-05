using OpenCvSharp;

namespace WireInspect.Core;

/// <summary>Paints synthetic defects into a good image (test aid): a missing wire, a broken wire and a swept
/// wire, on the given units (-1 = none).</summary>
public static class Simulator
{
    public static Mat Apply(Mat gray8, Product product, List<UnitResult> units, int missUnit, int brokenUnit,
        int sweepUnit, double sweepPx = 5.0, int seed = 0)
    {
        using var mask = new Mat(gray8.Size(), MatType.CV_8U, Scalar.All(0));
        var jobs = new (int unit, double f0, double f1)[] { (missUnit, 0, 1), (brokenUnit, 0.35, 0.65), (sweepUnit, 0, 1) };
        foreach (var (unit, f0, f1) in jobs)
        {
            if (unit < 0 || unit >= units.Count) continue;
            foreach (var w in product.Wires)
            {
                var (p1, p2) = Inspector.WirePoints(units[unit], w);
                var v = p2 - p1;
                var a = p1 + v * f0 + (f0 > 0 ? v * 0.06 : new Point2d());
                var b = p1 + v * f1;
                Cv2.Line(mask, Round(a), Round(b), Scalar.All(255), 9);
                if (unit == missUnit || unit == sweepUnit) Cv2.Circle(mask, Round(p1), 3, Scalar.All(255), -1);
            }
        }

        using var inpainted = new Mat();
        Cv2.Inpaint(gray8, mask, inpainted, 4, InpaintTypes.Telea);
        using var g = new Mat();
        gray8.ConvertTo(g, MatType.CV_32F);
        using var gb = new Mat();
        Cv2.GaussianBlur(g, gb, new Size(0, 0), 2);
        using var hp = new Mat();
        Cv2.Subtract(g, gb, hp);
        using var notMask = new Mat();
        Cv2.BitwiseNot(mask, notMask);
        Cv2.MeanStdDev(hp, out _, out var std, notMask);

        var outF = new Mat();
        inpainted.ConvertTo(outF, MatType.CV_32F);
        using var smooth = new Mat();
        Cv2.GaussianBlur(outF, smooth, new Size(0, 0), 2);
        using var noise = new Mat(gray8.Size(), MatType.CV_32F);
        var rng = new Random(seed);
        var nd = new float[gray8.Rows * gray8.Cols];
        for (int i = 0; i < nd.Length; i++)
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            nd[i] = (float)(std.Val0 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        noise.SetArray(nd);
        using var filled = new Mat();
        Cv2.Add(smooth, noise, filled);
        filled.CopyTo(outF, mask);

        if (sweepUnit >= 0 && sweepUnit < units.Count)
            foreach (var w in product.Wires)
            {
                var (p1, p2) = Inspector.WirePoints(units[sweepUnit], w);
                var v = p2 - p1;
                double L = Math.Sqrt(v.X * v.X + v.Y * v.Y);
                var n = new Point2d(-v.Y / L, v.X / L);
                int m = (int)(L * 4);
                var pts = Enumerable.Range(0, m).Select(i =>
                {
                    double t = m > 1 ? i / (double)(m - 1) : 0;
                    var p = p1 + v * t + n * (4 * t * (1 - t) * sweepPx);
                    return new Point((int)Math.Round(p.X * 4), (int)Math.Round(p.Y * 4));
                }).ToArray();
                using var layer = new Mat(gray8.Size(), MatType.CV_32F, Scalar.All(0));
                Cv2.Polylines(layer, [pts], false, Scalar.All(1.0), 2, LineTypes.AntiAlias, 2);
                using var lb = new Mat();
                Cv2.GaussianBlur(layer, lb, new Size(0, 0), 0.6);
                Cv2.MinMaxLoc(lb, out _, out double mx);
                using var scaled = new Mat();
                Cv2.Multiply(lb, new Scalar(35.0 / (mx + 1e-6)), scaled);
                Cv2.Subtract(outF, scaled, outF);
                var c = Round(p1);
                var roi = new Rect(c.X - 3, c.Y - 3, 7, 7).Intersect(new Rect(0, 0, outF.Width, outF.Height));
                using var patch = new Mat(outF, roi).Clone();
                patch.GetArray(out float[] vals);
                Cv2.Circle(outF, c, 1, Scalar.All(Inspector.Median(vals.Select(f => (double)f)) - 40), -1);
            }

        var result = new Mat();
        outF.ConvertTo(result, MatType.CV_8U);
        outF.Dispose();
        return result;
    }

    static Point Round(Point2d p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));
}
