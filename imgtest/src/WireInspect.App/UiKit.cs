using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace WireInspect.App;

public sealed record TableRow(string[] Cells, IBrush?[] Colors, object Tag, string? Tip = null);

/// <summary>Read-only table: a header row plus a ListBox of rows sharing the same column layout
/// (stands in for the app-gpt QTableWidget).</summary>
public sealed class SimpleTable : DockPanel
{
    readonly string _columns;
    public ListBox List { get; } = new() { SelectionMode = SelectionMode.Single };

    public SimpleTable(string[] headers, string columns)
    {
        _columns = columns;
        var head = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(columns), Background = Brush.Parse("#eef1f5") };
        for (int i = 0; i < headers.Length; i++)
            head.Children.Add(new TextBlock { Text = headers[i], FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 3), [Grid.ColumnProperty] = i });
        SetDock(head, Dock.Top);
        Children.Add(head);
        List.ItemTemplate = new FuncDataTemplate<TableRow>((row, _) =>
        {
            var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(_columns) };
            if (row == null) return g;
            for (int i = 0; i < row.Cells.Length; i++)
                g.Children.Add(new TextBlock
                {
                    Text = row.Cells[i], Foreground = row.Colors.ElementAtOrDefault(i) ?? Brushes.Black,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 4, 0), [Grid.ColumnProperty] = i,
                });
            if (row.Tip != null) ToolTip.SetTip(g, row.Tip);
            return g;
        });
        List.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 2)), new Setter(MinHeightProperty, 22.0) },
        });
        Children.Add(List);
    }

    public void SetRows(IEnumerable<TableRow> rows) => List.ItemsSource = rows.ToList();

    public object? SelectedTag => (List.SelectedItem as TableRow)?.Tag;

    public void SelectTag(object? tag)
    {
        var rows = List.ItemsSource as List<TableRow>;
        var row = rows?.FirstOrDefault(r => Equals(r.Tag, tag));
        List.SelectedItem = row;
        if (row != null) List.ScrollIntoView(row);
    }
}

/// <summary>Status dot for the file tree; while busy a blue arc spins on its left (app-gpt dot_icon).</summary>
public sealed class StatusDot : Control
{
    public static int Frame;
    Color _color = Colors.Gray;
    bool _busy;

    public StatusDot() { Width = 32; Height = 14; }

    public void Set(string color, bool busy)
    {
        _color = Color.Parse(color);
        _busy = busy;
        InvalidateVisual();
    }

    public bool Busy => _busy;

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawEllipse(new SolidColorBrush(_color), null, new Point(Width - 7, 7), 6, 6);
        if (!_busy) return;
        var geo = new StreamGeometry();
        double a0 = -Frame * Math.PI * 2 / 12;
        using (var g = geo.Open())
        {
            var c = new Point(7, 7);
            g.BeginFigure(new Point(c.X + 5 * Math.Cos(a0), c.Y + 5 * Math.Sin(a0)), false);
            g.ArcTo(new Point(c.X + 5 * Math.Cos(a0 + 4.7), c.Y + 5 * Math.Sin(a0 + 4.7)), new Size(5, 5), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Brush.Parse("#1f6fd1"), 2.2, lineCap: PenLineCap.Round), geo);
    }
}

public static class Dialogs
{
    /// <summary>Modal message box; returns the clicked button text (null if closed) and the checkbox state.</summary>
    public static async Task<(string? button, bool check)> Show(Window owner, string title, string text, string[] buttons,
        string? checkbox = null, string? defaultButton = null)
    {
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 620, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        string? result = null;
        var chk = checkbox != null ? new CheckBox { Content = checkbox } : null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            var btn = new Button { Content = b, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = b == (defaultButton ?? buttons[0]) };
            btn.Click += (_, _) => { result = b; w.Close(); };
            row.Children.Add(btn);
        }
        var body = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        if (chk != null) body.Children.Add(chk);
        body.Children.Add(row);
        w.Content = body;
        await w.ShowDialog(owner);
        return (result, chk?.IsChecked == true);
    }

    public static Task Info(Window owner, string title, string text) => Show(owner, title, text, ["确定"]);

    public static async Task<bool> Confirm(Window owner, string title, string text)
        => (await Show(owner, title, text, ["确定", "取消"], defaultButton: "取消")).button == "确定";
}
