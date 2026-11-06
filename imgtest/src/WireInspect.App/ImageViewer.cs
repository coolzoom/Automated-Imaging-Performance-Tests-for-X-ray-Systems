using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenCvSharp;
using WireInspect.Core;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Size = Avalonia.Size;

namespace WireInspect.App;

public static class Bitmaps
{
    /// <summary>8-bit grayscale Mat -> Avalonia bitmap (BGRA).</summary>
    public static WriteableBitmap FromGray(Mat gray)
    {
        using var bgra = new Mat();
        Cv2.CvtColor(gray, bgra, ColorConversionCodes.GRAY2BGRA);
        int w = gray.Width, h = gray.Height;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        var row = new byte[w * 4];
        for (int y = 0; y < h; y++)
        {
            Marshal.Copy(bgra.Ptr(y), row, 0, row.Length);
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }
        return bmp;
    }

    public static WriteableBitmap Thumbnail(Mat gray, int side = 160)
    {
        double s = side / (double)Math.Max(gray.Width, gray.Height);
        using var small = new Mat();
        Cv2.Resize(gray, small, new OpenCvSharp.Size(Math.Max(1, (int)(gray.Width * s)), Math.Max(1, (int)(gray.Height * s))),
            interpolation: InterpolationFlags.Area);
        using var c = ImageIO.Clahe(small);
        return FromGray(c);
    }
}

public sealed record WireMark(IReadOnlyList<Point> Pts, Color Color, double Width, string Tip, int Index, string? Label = null, bool Dashed = false);
public sealed record UnitMark(Rect Box, Color Color, string Label);
public sealed record DotMark(Point Center, double Radius, Color Color);

/// <summary>Zoom / pan image view with vector overlays (units, wires, bond points, defect boxes), box
/// drawing and point picking — the Avalonia counterpart of the app-gpt QGraphicsView viewer.</summary>
public sealed class ImageViewer : Control
{
    public enum Tool { Pan, DrawBox, PickPoint }

    public Bitmap? Source { get; private set; }
    public double Scale { get; private set; } = 1;
    Vector _offset;
    bool _fit = true;
    Point? _dragStart, _panStart;
    Vector _panOffset;
    Rect? _rubber;

    public List<Defect> Defects { get; } = [];
    public string? SelectedDefect { get; set; }
    public bool ShowMarks { get; set; } = true;
    public List<WireMark> Wires { get; } = [];
    public List<UnitMark> Units { get; } = [];
    public List<DotMark> Dots { get; } = [];
    public int? SelectedWire { get; set; }
    public Rect? TemplateBox { get; set; }
    public List<Point> PendingPoints { get; } = [];
    public Tool Mode { get; set; } = Tool.Pan;

    public event Action<Rect>? BoxDrawn;
    public event Action<string>? DefectClicked;
    public event Action<int>? WireClicked;
    public event Action<Point>? PointPicked;
    public event Action<double>? ZoomChanged;

    public ImageViewer()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetImage(Bitmap? bmp, bool keepView = false)
    {
        var old = Source;
        Source = bmp;
        if (!keepView || old == null || bmp == null || old.PixelSize != bmp.PixelSize) _fit = true;
        if (_fit) Fit();
        InvalidateVisual();
    }

    public void ClearOverlays()
    {
        Defects.Clear();
        Wires.Clear();
        Units.Clear();
        Dots.Clear();
        PendingPoints.Clear();
        SelectedWire = null;
        SelectedDefect = null;
        TemplateBox = null;
    }

    public void Refresh() => InvalidateVisual();

    // ---- zoom
    public void Fit()
    {
        _fit = true;
        if (Source == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        double w = Source.PixelSize.Width, h = Source.PixelSize.Height;
        Scale = Math.Min(Bounds.Width / w, Bounds.Height / h);
        _offset = new Vector((Bounds.Width - w * Scale) / 2, (Bounds.Height - h * Scale) / 2);
        ZoomChanged?.Invoke(Scale);
        InvalidateVisual();
    }

    public void ActualSize() => ZoomAt(1.0 / Scale, new Point(Bounds.Width / 2, Bounds.Height / 2));

    public void Zoom(double f) => ZoomAt(f, new Point(Bounds.Width / 2, Bounds.Height / 2));

    void ZoomAt(double f, Point screen)
    {
        double s = Scale * f;
        if (Source == null || s < 0.02 || s > 40) return;
        var img = ToImage(screen);
        Scale = s;
        _offset = new Vector(screen.X - img.X * s, screen.Y - img.Y * s);
        _fit = false;
        ZoomChanged?.Invoke(Scale);
        InvalidateVisual();
    }

    /// <summary>Show an image rectangle (with margin) in the middle of the view.</summary>
    public void CenterOn(Rect r, double margin = 2)
    {
        if (Source == null || Bounds.Width <= 0) return;
        var big = r.Inflate(new Thickness(r.Width * margin, r.Height * margin));
        if (big.Width >= Source.PixelSize.Width * 0.9) return;
        Scale = Math.Clamp(Math.Min(Bounds.Width / big.Width, Bounds.Height / big.Height), 0.02, 40);
        _offset = new Vector(Bounds.Width / 2 - r.Center.X * Scale, Bounds.Height / 2 - r.Center.Y * Scale);
        _fit = false;
        ZoomChanged?.Invoke(Scale);
        InvalidateVisual();
    }

    Point ToScreen(Point p) => new(p.X * Scale + _offset.X, p.Y * Scale + _offset.Y);
    Point ToImage(Point p) => new((p.X - _offset.X) / Scale, (p.Y - _offset.Y) / Scale);
    Rect ToScreen(Rect r) => new(ToScreen(r.TopLeft), ToScreen(r.BottomRight));

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fit) Fit();
    }

    // ---- painting
    static void Text(DrawingContext ctx, string text, Point at, Color color, double size, bool bold = false, bool badge = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, new SolidColorBrush(color));
        if (badge) ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(220, 25, 28, 30)), new Rect(at.X - 3, at.Y - 1, ft.Width + 6, ft.Height + 2));
        ctx.DrawText(ft, at);
    }

    static Pen MakePen(Color c, double w, bool dashed = false) => new(new SolidColorBrush(c), w,
        dashed ? new DashStyle([4, 3], 0) : null, PenLineCap.Round, PenLineJoin.Round);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), new Rect(Bounds.Size));
        if (Source == null) return;
        var dest = ToScreen(new Rect(0, 0, Source.PixelSize.Width, Source.PixelSize.Height));
        RenderOptions.SetBitmapInterpolationMode(this, Scale >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        ctx.DrawImage(Source, new Rect(0, 0, Source.PixelSize.Width, Source.PixelSize.Height), dest);

        if (TemplateBox is { } tb)
            ctx.DrawRectangle(null, MakePen(Color.Parse("#1f9fff"), 2, true), ToScreen(tb));
        foreach (var u in Units)
        {
            ctx.DrawRectangle(null, MakePen(u.Color, 1.5, true), ToScreen(u.Box));
            var tl = ToScreen(u.Box.TopLeft);
            Text(ctx, u.Label, new Point(tl.X + 5, tl.Y + 3), u.Color, 11, badge: true);
        }
        for (int i = 0; i < Wires.Count; i++)
        {
            var w = Wires[i];
            if (w.Pts.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(ToScreen(w.Pts[0]), false);
                foreach (var p in w.Pts.Skip(1)) g.LineTo(ToScreen(p));
                g.EndFigure(false);
            }
            bool sel = SelectedWire == w.Index;
            var glow = w.Color;
            ctx.DrawGeometry(null, MakePen(Color.FromArgb(50, glow.R, glow.G, glow.B), (sel ? 3.6 : w.Width) * 3.2), geo);
            ctx.DrawGeometry(null, MakePen(w.Color, sel ? 3.6 : w.Width, w.Dashed), geo);
            if (w.Label != null && !(ShowMarks && Defects.Count > 0))   // defect boxes already carry the type
            {
                var p0 = ToScreen(w.Pts[0]);
                Text(ctx, w.Label, new Point(p0.X + 6, p0.Y - 18), w.Color, 11, badge: true);
            }
        }
        foreach (var d in Dots)
            ctx.DrawEllipse(null, MakePen(d.Color, 1.4), ToScreen(d.Center), d.Radius * Scale + 2, d.Radius * Scale + 2);
        foreach (var p in PendingPoints)
            ctx.DrawEllipse(Brushes.Red, null, ToScreen(p), 4, 4);

        if (ShowMarks)
            foreach (var d in Defects)
            {
                var c = d.Status == "rejected" ? Color.FromRgb(150, 150, 150) : Color.Parse(DefectTypes.Color(d.Type));
                bool sel = d.Id == SelectedDefect;
                var r = ToScreen(new Rect(d.X0, d.Y0, d.X1 - d.X0, d.Y1 - d.Y0));
                if (sel) ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 0)), r);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(c), sel ? 4 : 2, d.Status == "rejected" ? DashStyle.Dot : null), r);
                var src = d.Source == "manual" ? "手动" : d.By;
                Text(ctx, $"{d.Type} {src}", new Point(r.X, r.Y - 17), c, 12, bold: true);
            }

        if (_rubber is { } rb)
            ctx.DrawRectangle(null, MakePen(Color.Parse("#30ff60"), 2, true), ToScreen(rb));
    }

    // ---- mouse
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomAt(e.Delta.Y > 0 ? 1.25 : 0.8, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Source == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var sp = e.GetPosition(this);
        var ip = ToImage(sp);
        e.Pointer.Capture(this);
        switch (Mode)
        {
            case Tool.DrawBox:
                _dragStart = ip;
                _rubber = new Rect(ip, ip);
                return;
            case Tool.PickPoint:
                PointPicked?.Invoke(ip);
                return;
        }
        var wire = WireAt(sp);
        if (wire != null)
        {
            SelectedWire = wire.Index;
            WireClicked?.Invoke(wire.Index);
            InvalidateVisual();
        }
        else if (ShowMarks && Defects.LastOrDefault(d => ip.X >= d.X0 && ip.X <= d.X1 && ip.Y >= d.Y0 && ip.Y <= d.Y1) is { } hit)
            DefectClicked?.Invoke(hit.Id);
        _panStart = sp;
        _panOffset = _offset;
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    WireMark? WireAt(Point screen)
    {
        foreach (var w in Wires)
            for (int i = 1; i < w.Pts.Count; i++)
                if (SegDist(screen, ToScreen(w.Pts[i - 1]), ToScreen(w.Pts[i])) <= 5) return w;
        return null;
    }

    static double SegDist(Point p, Point a, Point b)
    {
        var ab = b - a;
        double l2 = ab.X * ab.X + ab.Y * ab.Y;
        double t = l2 > 0 ? Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / l2, 0, 1) : 0;
        var q = a + ab * t;
        return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var sp = e.GetPosition(this);
        if (_dragStart is { } ds)
        {
            var ip = ToImage(sp);
            _rubber = new Rect(new Point(Math.Min(ds.X, ip.X), Math.Min(ds.Y, ip.Y)), new Point(Math.Max(ds.X, ip.X), Math.Max(ds.Y, ip.Y)));
            InvalidateVisual();
        }
        else if (_panStart is { } ps)
        {
            _offset = _panOffset + (sp - ps);
            _fit = false;
            InvalidateVisual();
        }
        else if (Wires.Count > 0)
            ToolTip.SetTip(this, WireAt(sp)?.Tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        Cursor = Mode == Tool.Pan ? Cursor.Default : new Cursor(StandardCursorType.Cross);
        _panStart = null;
        if (_rubber is { } r && _dragStart != null)
        {
            _rubber = null;
            _dragStart = null;
            InvalidateVisual();
            if (Source != null)
            {
                r = r.Intersect(new Rect(0, 0, Source.PixelSize.Width, Source.PixelSize.Height));
                if (r.Width >= 4 && r.Height >= 4) BoxDrawn?.Invoke(r);
            }
        }
    }

    public void SetTool(Tool t)
    {
        Mode = t;
        _rubber = null;
        _dragStart = null;
        Cursor = t == Tool.Pan ? Cursor.Default : new Cursor(StandardCursorType.Cross);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);
}
