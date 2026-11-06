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
        for (int u = 0; u < 9; u++) DrawUnit(img, Ox + o.ShiftX + (u % 3) * PitchX, Oy + o.ShiftY + (u / 3) * PitchY, u, o);
        return Noisy(img);
    }

    public const int Cell = 200;

    /// <summary>3×3 units, unit u turned by angles[u] degrees (parts lying in different directions), each in a Cell² cell.</summary>
    public static Mat MakeMixed(double[] angles, Opts? o = null)
    {
        o ??= new Opts();
        var img = new Mat(3 * Cell, 3 * Cell, MatType.CV_32F, Scalar.All(110));
        for (int u = 0; u < 9; u++)
        {
            using var cell = new Mat(Cell, Cell, MatType.CV_32F, Scalar.All(110));
            DrawUnit(cell, (Cell - UnitW) / 2, (Cell - UnitH) / 2, u, o);
            using var c8 = new Mat();
            cell.ConvertTo(c8, MatType.CV_8U);
            using var rot = Align.Warp(c8, Align.Rotation(Cell / 2.0, Cell / 2.0, angles[u]), Cell, Cell);
            using var rf = new Mat();
            rot.ConvertTo(rf, MatType.CV_32F);
            rf.CopyTo(new Mat(img, new Rect((u % 3) * Cell, (u / 3) * Cell, Cell, Cell)));
        }
        return Noisy(img);
    }

    static Mat Noisy(Mat img)
    {
        using var noise = new Mat(img.Size(), MatType.CV_32F);
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(5));
        Cv2.Add(img, noise, img);
        var g = new Mat();
        img.ConvertTo(g, MatType.CV_8U);
        img.Dispose();
        return g;
    }

    static void DrawUnit(Mat img, int x, int y, int u, Opts o)
    {
        Cv2.Rectangle(img, new Rect(x + 5, y + 5, UnitW - 10, UnitH - 10), Scalar.All(185), -1);
        Cv2.Rectangle(img, new Rect(x + 15, y + 15, 50, 60), Scalar.All(80), -1);
        Cv2.Rectangle(img, new Rect(x + 80, y + 20, 25, 50), Scalar.All(90), -1);
        Cv2.Rectangle(img, new Rect(x + 68, y + 8, 8, 6), Scalar.All(70), -1);   // marker: unit not symmetric, mirror != rotation
        for (int w = 0; w < Wires.Length; w++)
        {
            var s = Wires[w];
            var p1 = new Point2d(x + s.P1[0], y + s.P1[1]);
            var p2 = new Point2d(x + s.P2[0], y + s.P2[1]);
            if (o.Missing == (u, w)) continue;
            using var layer = new Mat(img.Size(), MatType.CV_32F, Scalar.All(0));
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
        }

        static void Draw(Mat layer, List<Point> pts)
        {
            if (pts.Count > 1) Cv2.Polylines(layer, [pts.ToArray()], false, Scalar.All(1.0), 2, LineTypes.AntiAlias, 2);
        }
    }

    /// <summary>The whole picture rotated by deg (counter-clockwise), canvas enlarged.</summary>
    public static Mat Rotate(Mat gray, double deg) => Align.RotateImage(gray, deg, out _);

    /// <summary>Radial lens distortion: k &gt; 0 shrinks towards the corners (pincushion), k &lt; 0 barrel.</summary>
    public static Mat Barrel(Mat gray, double k)
    {
        int w = gray.Width, h = gray.Height;
        double cx = w / 2.0, cy = h / 2.0, r2 = Math.Max(cx, cy) * Math.Max(cx, cy);
        using var mx = new Mat(h, w, MatType.CV_32F);
        using var my = new Mat(h, w, MatType.CV_32F);
        var ax = new float[h * w];
        var ay = new float[h * w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double dx = x - cx, dy = y - cy, f = 1 + k * (dx * dx + dy * dy) / r2;
                ax[y * w + x] = (float)(cx + dx * f);
                ay[y * w + x] = (float)(cy + dy * f);
            }
        mx.SetArray(ax);
        my.SetArray(ay);
        var dst = new Mat();
        Cv2.Remap(gray, dst, mx, my, InterpolationFlags.Linear, BorderTypes.Replicate);
        return dst;
    }

    public static Product MakeProduct(Mat gray)
    {
        var tpl = new Mat(gray, new Rect(Ox, Oy, UnitW, UnitH)).Clone();
        return new Product
        {
            Name = "synthetic", WireWidthPx = 2.5, Template = tpl,
            Wires = Wires.Select(w => new WireSpec(w.P1[0], w.P1[1], w.P2[0], w.P2[1])).ToList(),
        };
    }
}
