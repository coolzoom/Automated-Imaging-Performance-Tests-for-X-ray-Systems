using OpenCvSharp;

namespace WireInspect.Core;

/// <summary>Annotated result image for the command line tool (the GUI draws its own vector overlay).</summary>
public static class Annotator
{
    public static Mat Draw(Mat gray8, ImageResult res, int scale = 2)
    {
        using var bgr = new Mat();
        Cv2.CvtColor(gray8, bgr, ColorConversionCodes.GRAY2BGR);
        var vis = new Mat();
        Cv2.Resize(bgr, vis, new Size(0, 0), scale, scale, InterpolationFlags.Linear);
        Point S(double x, double y) => new((int)Math.Round(x * scale), (int)Math.Round(y * scale));
        var ok = new Scalar(0, 200, 0);
        var ng = new Scalar(0, 0, 255);
        foreach (var u in res.Units)
        {
            var wires = res.Wires.Where(w => w.Unit == u.Index).ToList();
            var bad = wires.Where(w => w.Bad).ToList();
            var col = bad.Count == 0 ? ok : ng;
            int bh = u.Box[3];
            var m = u.Map();
            Cv2.Polylines(vis, [u.Corners().Select(c => S(c.x, c.y)).ToArray()], true, col, 2);
            var (x, y) = Align.Apply(m, 0, 0);
            foreach (var w in wires)
            {
                if (w.Flags.Contains("MISSING"))
                    Cv2.Line(vis, S(w.P1[0], w.P1[1]), S(w.P2[0], w.P2[1]), ng, 1);
                else if (w.Curve.Count > 1)
                    Cv2.Polylines(vis, [w.Curve.Select(p => S(p[0], p[1])).ToArray()], false,
                        w.Bad ? ng : new Scalar(0, 255, 255), 1, LineTypes.AntiAlias);
                Cv2.Circle(vis, S(w.Ball[0], w.Ball[1]), 4 * scale, new Scalar(255, 120, 0), 1);
                Cv2.Circle(vis, S(w.P2[0], w.P2[1]), 4 * scale, new Scalar(255, 0, 255), 1);
            }
            var label = $"#{u.Index} " + (bad.Count == 0 ? "OK" : string.Join("/", bad.SelectMany(w => w.Flags).Distinct()));
            Cv2.PutText(vis, label, S(x + 4, y + 16), HersheyFonts.HersheySimplex, 0.8, Scalar.Black, 4);
            Cv2.PutText(vis, label, S(x + 4, y + 16), HersheyFonts.HersheySimplex, 0.8, col, 2);
            if (wires.Count > 0)
            {
                var w0 = wires.OrderBy(w => w.ZAll).First();
                var info = $"z={w0.ZAll:F1} ball={w0.ZBall:F1} sw={w0.SweepPx:F1}px";
                Cv2.PutText(vis, info, S(x + 4, y + bh - 6), HersheyFonts.HersheySimplex, 0.55, Scalar.Black, 3);
                Cv2.PutText(vis, info, S(x + 4, y + bh - 6), HersheyFonts.HersheySimplex, 0.55, col, 1);
            }
        }
        return vis;
    }
}
