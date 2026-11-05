using OpenCvSharp;
using WireInspect.Core;

namespace WireInspect.Tests;

/// <summary>A 3×3 grid of units (die + lead + two wires each), with optional defects.</summary>
public static class Synthetic
{
    public const int UnitW = 120, UnitH = 90, PitchX = 150, PitchY = 120, Ox = 20, Oy = 15;
    public static readonly WireSpec[] Wires = [new(30, 30, 92, 32), new(30, 58, 92, 56)];

    public sealed class Opts
    {
        public (int unit, int wire)? Missing;
        public (int unit, int wire)? Broken;
        public (int unit, int wire)? Sweep;
        public double SweepPx = 6;
        public int ShiftX, ShiftY;
    }

    public static Mat Make(Opts? o = null)
    {
        o ??= new Opts();
        var img = new Mat(PitchY * 3 + 2 * Oy + 20, PitchX * 3 + 2 * Ox + 20, MatType.CV_32F, Scalar.All(110));
        for (int u = 0; u < 9; u++)
        {
            int x = Ox + o.ShiftX + (u % 3) * PitchX, y = Oy + o.ShiftY + (u / 3) * PitchY;
            Cv2.Rectangle(img, new Rect(x + 5, y + 5, UnitW - 10, UnitH - 10), Scalar.All(185), -1);
            Cv2.Rectangle(img, new Rect(x + 15, y + 15, 50, 60), Scalar.All(80), -1);
            Cv2.Rectangle(img, new Rect(x + 80, y + 20, 25, 50), Scalar.All(90), -1);
            for (int w = 0; w < Wires.Length; w++)
            {
                var s = Wires[w];
                var p1 = new Point2d(x + s.P1[0], y + s.P1[1]);
                var p2 = new Point2d(x + s.P2[0], y + s.P2[1]);
                if (o.Missing == (u, w)) continue;
                var layer = new Mat(img.Size(), MatType.CV_32F, Scalar.All(0));
                double bow = o.Sweep == (u, w) ? o.SweepPx : 0;
                var v = p2 - p1;
                double L = Math.Sqrt(v.X * v.X + v.Y * v.Y);
                var n = new Point2d(-v.Y / L, v.X / L);
                var pts = new List<Point>();
                for (int i = 0; i <= 200; i++)
                {
                    double t = i / 200.0;
                    if (o.Broken == (u, w) && t > 0.4 && t < 0.6) { Draw(layer, pts); pts.Clear(); continue; }
                    var p = p1 + v * t + n * (4 * t * (1 - t) * bow);
                    pts.Add(new Point((int)Math.Round(p.X * 4), (int)Math.Round(p.Y * 4)));
                }
                Draw(layer, pts);
                Cv2.GaussianBlur(layer, layer, new Size(0, 0), 0.6);
                Cv2.MinMaxLoc(layer, out _, out double mx);
                Cv2.Multiply(layer, new Scalar(35.0 / mx), layer);
                Cv2.Subtract(img, layer, img);
                layer.SetTo(Scalar.All(0));
                Cv2.Circle(layer, new Point((int)Math.Round(p1.X * 4), (int)Math.Round(p1.Y * 4)), 7, Scalar.All(1.0), -1,
                    LineTypes.AntiAlias, 2);   // ball: ~1.5× wire width, darker than the wire
                Cv2.GaussianBlur(layer, layer, new Size(0, 0), 0.6);
                Cv2.MinMaxLoc(layer, out _, out mx);
                Cv2.Multiply(layer, new Scalar(30.0 / mx), layer);
                Cv2.Subtract(img, layer, img);
                layer.Dispose();
            }
        }
        using var noise = new Mat(img.Size(), MatType.CV_32F);
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(5));
        Cv2.Add(img, noise, img);
        var g = new Mat();
        img.ConvertTo(g, MatType.CV_8U);
        img.Dispose();
        return g;

        static void Draw(Mat layer, List<Point> pts)
        {
            if (pts.Count > 1) Cv2.Polylines(layer, [pts.ToArray()], false, Scalar.All(1.0), 2, LineTypes.AntiAlias, 2);
        }
    }

    public static Product MakeProduct(Mat gray)
    {
        var tpl = new Mat(gray, new Rect(Ox, Oy, UnitW, UnitH)).Clone();
        return new Product
        {
            Name = "synthetic", PixelUm = 10, WireDiamUm = 25, Template = tpl,
            Wires = Wires.Select(w => new WireSpec(w.P1[0], w.P1[1], w.P2[0], w.P2[1])).ToList(),
        };
    }
}
