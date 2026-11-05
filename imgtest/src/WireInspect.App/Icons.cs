using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace WireInspect.App;

/// <summary>Toolbar icons (the app-gpt line icons, drawn as vector shapes; nothing to ship).</summary>
public static class Icons
{
    const string Line = "#3a4458";

    abstract record E(string? Fill, string? Stroke);
    sealed record P(string D, string? Fill = null, string? Stroke = Line) : E(Fill, Stroke);
    sealed record R(double X, double Y, double W, double H, double Rx, string? Fill = null, string? Stroke = Line) : E(Fill, Stroke);
    sealed record C(double Cx, double Cy, double Rad, string? Fill = null, string? Stroke = Line) : E(Fill, Stroke);

    static readonly Dictionary<string, E[]> Tool = new()
    {
        ["open"] = [new P("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z", "#f4c542", "#b8860b")],
        ["start"] = [new P("M7 4.5v15l12-7.5z", "#18a848", "#128a3a")],
        ["stop"] = [new R(5.5, 5.5, 13, 13, 2, "#e02424", "#b81c1c")],
        ["rerun"] = [new P("M20 11a8 8 0 0 0-14.3-4.9M4 13a8 8 0 0 0 14.3 4.9"), new P("M5 2.5v4h4M19 21.5v-4h-4")],
        ["report"] = [new P("M6 2.5h8l4.5 4.5v13.5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1v-17a1 1 0 0 1 1-1z", "#ffffff"),
                      new P("M14 2.5V7h4.5M8 12h8M8 15.5h8M8 19h5")],
        ["clear"] = [new P("M14.5 4.5 20 10l-8.5 8.5H6L3.5 16z", "#ffd6d6", "#e02424"), new P("M9 10l5.5 5.5M11.5 18.5H20")],
        ["recent"] = [new C(12, 12, 8.5), new P("M12 7.5V12l3 2", null, "#2b7bd6")],
        ["product"] = [new R(6.5, 6.5, 11, 11, 1.5, "#e9eef6"), new R(9.5, 9.5, 5, 5, 0.5, "#8fa3c0", "#8fa3c0"),
                       new P("M9 6.5V3.5M12 6.5V3.5M15 6.5V3.5M9 20.5v-3M12 20.5v-3M15 20.5v-3M3.5 9h3M3.5 12h3M3.5 15h3M17.5 9h3M17.5 12h3M17.5 15h3")],
        ["add"] = [new C(12, 12, 9, "#e8f6ee", "#18a848"), new P("M12 7.5v9M7.5 12h9", null, "#18a848")],
        ["edit"] = [new P("M4 20h4L19.5 8.5l-4-4L4 16z", "#fff4cc", "#b8860b"), new P("M13.5 6.5l4 4")],
    };

    public static Control Get(string name, double size = 18)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var e in Tool[name])
        {
            Shape s = e switch
            {
                P p => new Path { Data = Geometry.Parse(p.D) },
                R r => new Rectangle { Width = r.W, Height = r.H, RadiusX = r.Rx, RadiusY = r.Rx, [Canvas.LeftProperty] = r.X, [Canvas.TopProperty] = r.Y },
                C c => new Ellipse { Width = 2 * c.Rad, Height = 2 * c.Rad, [Canvas.LeftProperty] = c.Cx - c.Rad, [Canvas.TopProperty] = c.Cy - c.Rad },
                _ => throw new InvalidOperationException(),
            };
            if (e.Fill != null) s.Fill = Brush.Parse(e.Fill);
            if (e.Stroke != null) s.Stroke = Brush.Parse(e.Stroke);
            s.StrokeThickness = 1.9;
            s.StrokeLineCap = PenLineCap.Round;
            s.StrokeJoin = PenLineJoin.Round;
            canvas.Children.Add(s);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    /// <summary>Toolbar button: icon + text beside it (like the app-gpt QToolBar, ToolButtonTextBesideIcon).</summary>
    public static Button ToolButton(string icon, string text, string? tip = null)
    {
        var b = new Button
        {
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5,
                Children = { Get(icon), new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
            },
            Padding = new Thickness(7, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }
}
