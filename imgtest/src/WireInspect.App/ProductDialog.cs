using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using OpenCvSharp;
using WireInspect.Core;
using Point = Avalonia.Point;
using Rect = Avalonia.Rect;
using Window = Avalonia.Controls.Window;

namespace WireInspect.App;

/// <summary>New / edit product: frame one unit (-> tpl.png), find its wires automatically, fix them by hand,
/// set the pixel size (or calibrate it from the unit pitch) and the µm thresholds, preview, save.</summary>
public sealed class ProductDialog : Window
{
    public string? Saved { get; private set; }

    readonly Product _work;
    readonly bool _isNew;
    readonly string _originalName;
    readonly Mat? _image;          // the main window's current image (null when not available)
    Mat _source;                   // what the viewer shows: the image, or the template alone
    Rect? _box;                    // template box in source coordinates
    readonly List<WireSpec> _wires;
    Point? _firstPick;
    readonly ImageViewer _viewer = new();
    readonly TextBlock _hint = new() { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap };
    readonly Border _hintBar = new() { Background = Brush.Parse("#1f6fd1"), Padding = new Thickness(8, 6) };
    readonly TextBox _name = new();
    readonly TextBlock _tplInfo = new() { Foreground = Brush.Parse("#555") };
    readonly ToggleButton _btnBox = new() { Content = "① 框选单元模板" };
    readonly Button _btnAuto = new() { Content = "② 自动找线" };
    readonly ToggleButton _btnAdd = new() { Content = "手动添加(点两点)" };
    readonly Button _btnDel = new() { Content = "删除" }, _btnRev = new() { Content = "反向" };
    readonly ListBox _list = new() { Height = 150 };
    readonly NumericUpDown _pixelUm = Num(10, 0.1, 500, 0.1, "0.###");
    readonly NumericUpDown _pitchMm = Num(1.5, 0.01, 100, 0.01, "0.###");
    readonly Button _btnCal = new() { Content = "按单元间距标定" };
    readonly NumericUpDown _wireDiam = Num(25, 1, 500, 1, "0.#"), _corridor = Num(70, 5, 2000, 5, "0.#"), _endTol = Num(100, 5, 2000, 5, "0.#"),
        _gapMax = Num(80, 5, 2000, 5, "0.#"), _shiftMax = Num(35, 1, 2000, 1, "0.#"), _sweepMax = Num(6, 0.5, 100, 0.5, "0.#"),
        _zMissing = Num(6, 1, 50, 0.5, "0.#"), _zSeg = Num(2.5, 0.5, 50, 0.5, "0.#"), _zBall = Num(2.5, 0, 50, 0.5, "0.#"),
        _matchThr = Num(0.6, 0.2, 0.99, 0.05, "0.##");
    readonly ComboBox _polarity = new() { ItemsSource = new[] { "导线比背景暗", "导线比背景亮" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ToggleButton _btnPreview = new() { Content = "预览检测（整张图）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly TextBlock _previewInfo = new() { TextWrapping = TextWrapping.Wrap };

    static NumericUpDown Num(double v, double min, double max, double inc, string fmt) => new()
    {
        Value = (decimal)v, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)inc, FormatString = fmt,
        HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 110,
    };

    public ProductDialog(Product? existing, Mat? image)
    {
        Title = existing == null ? "新建产品" : $"编辑产品 - {existing.Name}";
        Width = 1400;
        Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _isNew = existing == null;
        _work = existing ?? new Product();
        _originalName = _work.Name;
        _image = image;
        _wires = _work.Wires.Select(w => new WireSpec(w.P1[0], w.P1[1], w.P2[0], w.P2[1])).ToList();
        _source = image ?? _work.Template!;
        if (existing?.Template is { } tpl)
        {
            var u = image != null ? Inspector.FindUnits(image, tpl, Math.Min(existing.MatchThr, 0.5)).FirstOrDefault() : null;
            if (u != null) _box = new Rect(u.Box[0], u.Box[1], u.Box[2], u.Box[3]);
            else
            {
                _source = tpl;
                _box = new Rect(0, 0, tpl.Width, tpl.Height);
            }
        }
        LoadFields();
        Content = Build();
        WireEvents();
        _viewer.SetImage(Bitmaps.FromGray(_source));
        Redraw();
        Hint(_box == null ? "第 ① 步：点“框选单元模板”，在图上拖框框住一个完整的芯片单元（选清晰的良品单元）"
                          : "可“自动找线”或手动增删导线；焊点1（芯片侧）在前，用“反向”调换；改好后“预览检测”再“保存”");
        Closed += (_, _) => _image?.Dispose();
    }

    Control Build()
    {
        Control Section(string title) => new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
        Grid Form(params (string label, Control field)[] rows)
        {
            var g = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("150,*"), RowSpacing = 4 };
            for (int i = 0; i < rows.Length; i++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                g.Children.Add(new TextBlock { Text = rows[i].label, VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = i });
                rows[i].field[Grid.RowProperty] = i;
                rows[i].field[Grid.ColumnProperty] = 1;
                g.Children.Add(rows[i].field);
            }
            return g;
        }
        foreach (var b in new Control[] { _btnBox, _btnAuto, _btnAdd, _btnDel, _btnRev, _btnCal })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (b is ContentControl cc) cc.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        var wireButtons = new UniformGrid { Columns = 3, Children = { _btnAdd, _btnDel, _btnRev } };
        foreach (var c in wireButtons.Children) c.Margin = new Thickness(1);
        var panel = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 4,
            Children =
            {
                Form(("产品名称", _name)),
                Section("单元模板"), _btnBox, _tplInfo,
                Section("导线（模板坐标，焊点1 → 焊点2）"), _btnAuto, _list, wireButtons,
                Section("像素尺寸"),
                Form(("µm / 像素", _pixelUm), ("单元横向间距 mm", _pitchMm)), _btnCal,
                Section("阈值（长度单位 µm）"),
                Form(("线径", _wireDiam), ("走廊半宽", _corridor), ("端点缺口容差", _endTol), ("中段断线缺口", _gapMax),
                     ("端点偏移上限", _shiftMax), ("sweep 上限 %", _sweepMax), ("缺线 z", _zMissing), ("分段 z（断线）", _zSeg),
                     ("焊点 z", _zBall), ("模板匹配阈值", _matchThr), ("极性", _polarity)),
                Section("预览"), _btnPreview, _previewInfo,
            },
        };
        var save = new Button { Content = "保存", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "取消", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Click += async (_, _) => await Save();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), Children = { save, cancel } };
        var right = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(buttons);
        right.Children.Add(new ScrollViewer { Content = panel });

        _hintBar.Child = _hint;
        var left = new DockPanel();
        DockPanel.SetDock(_hintBar, Dock.Top);
        left.Children.Add(_hintBar);
        left.Children.Add(_viewer);
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,4,400") };
        grid.Children.Add(left);
        grid.Children.Add(new GridSplitter { [Grid.ColumnProperty] = 1, Background = Brush.Parse("#e3e6ea") });
        right[Grid.ColumnProperty] = 2;
        grid.Children.Add(right);
        return grid;
    }

    void WireEvents()
    {
        _btnBox.IsCheckedChanged += (_, _) =>
        {
            if (_btnBox.IsChecked == true)
            {
                if (_image == null)
                {
                    _btnBox.IsChecked = false;
                    Hint("没有可用的整图：请在主窗口选中一张图片后再编辑产品");
                    return;
                }
                if (_source != _image) SwitchSource(_image, null);
                _btnAdd.IsChecked = false;
                ExitPreview();
                _viewer.SetTool(ImageViewer.Tool.DrawBox);
                Hint("拖框框住一个完整的芯片单元（框尽量贴合单元外形，不要包含相邻单元）");
            }
            else if (_btnAdd.IsChecked != true) _viewer.SetTool(ImageViewer.Tool.Pan);
        };
        _viewer.BoxDrawn += r =>
        {
            if (_btnBox.IsChecked != true) return;
            var nb = new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
            if (nb.Width < 16 || nb.Height < 16) { Hint("框太小，请重新框住一个完整的芯片单元"); return; }
            if (_box is { } old)   // keep the wires where they are on the image
                foreach (var w in _wires)
                {
                    w.P1 = [w.P1[0] + old.X - nb.X, w.P1[1] + old.Y - nb.Y];
                    w.P2 = [w.P2[0] + old.X - nb.X, w.P2[1] + old.Y - nb.Y];
                }
            _box = nb;
            _btnBox.IsChecked = false;
            Redraw();
            Hint(_wires.Count == 0 ? "第 ② 步：点“自动找线”，或“手动添加”" : "模板已更新；可“自动找线”重新找线");
        };
        _btnAuto.Click += (_, _) => AutoFind();
        _btnAdd.IsCheckedChanged += (_, _) =>
        {
            _firstPick = null;
            _viewer.PendingPoints.Clear();
            if (_btnAdd.IsChecked == true)
            {
                if (_box == null) { _btnAdd.IsChecked = false; Hint("请先框选单元模板"); return; }
                _btnBox.IsChecked = false;
                ExitPreview();
                _viewer.SetTool(ImageViewer.Tool.PickPoint);
                Hint("点第 1 点：焊点1（芯片侧的球焊点）");
            }
            else _viewer.SetTool(ImageViewer.Tool.Pan);
            _viewer.Refresh();
        };
        _viewer.PointPicked += p =>
        {
            if (_btnAdd.IsChecked != true || _box is not { } b) return;
            if (_firstPick is not { } a)
            {
                _firstPick = p;
                _viewer.PendingPoints.Add(p);
                _viewer.Refresh();
                Hint("点第 2 点：焊点2（引脚侧）");
                return;
            }
            _wires.Add(new WireSpec(Math.Round(a.X - b.X, 1), Math.Round(a.Y - b.Y, 1), Math.Round(p.X - b.X, 1), Math.Round(p.Y - b.Y, 1)));
            _btnAdd.IsChecked = false;
            Redraw(_wires.Count - 1);
            Hint($"已添加线 {_wires.Count}");
        };
        _viewer.WireClicked += i => _list.SelectedIndex = i;
        _list.SelectionChanged += (_, _) => { _viewer.SelectedWire = _list.SelectedIndex >= 0 ? _list.SelectedIndex : null; _viewer.Refresh(); };
        _btnDel.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            _wires.RemoveAt(_list.SelectedIndex);
            Redraw();
        };
        _btnRev.Click += (_, _) =>
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _wires[i] = _wires[i].Reversed();
            Redraw(i);
        };
        _btnCal.Click += (_, _) => Calibrate();
        _btnPreview.IsCheckedChanged += (_, _) =>
        {
            if (_btnPreview.IsChecked == true) Preview();
            else Redraw();
        };
    }

    void Hint(string text) => _hint.Text = text;

    void SwitchSource(Mat src, Rect? box)
    {
        _source = src;
        _box = box;
        _viewer.SetImage(Bitmaps.FromGray(src));
    }

    void ExitPreview()
    {
        if (_btnPreview.IsChecked == true) _btnPreview.IsChecked = false;
    }

    Mat? CropTemplate() => _box is { } b
        ? new Mat(_source, new OpenCvSharp.Rect((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height)).Clone()
        : null;

    void Redraw(int? select = null)
    {
        _viewer.ClearOverlays();
        _viewer.TemplateBox = _box;
        if (_box is { } b)
        {
            _tplInfo.Text = $"模板 {(int)b.Width}×{(int)b.Height} px，位置 ({(int)b.X}, {(int)b.Y})";
            for (int i = 0; i < _wires.Count; i++)
            {
                var w = _wires[i];
                var p1 = new Point(w.P1[0] + b.X, w.P1[1] + b.Y);
                var p2 = new Point(w.P2[0] + b.X, w.P2[1] + b.Y);
                _viewer.Wires.Add(new WireMark([p1, p2], Color.Parse("#2ee6ff"), 2, $"线 {i + 1}", i, $"线{i + 1}"));
                _viewer.Dots.Add(new DotMark(p1, 2.5, Color.Parse("#ffb000")));
                _viewer.Dots.Add(new DotMark(p2, 2.5, Color.Parse("#d070ff")));
            }
        }
        else _tplInfo.Text = "未框选";
        double um = (double)(_pixelUm.Value ?? 10);
        _list.ItemsSource = _wires.Select((w, i) =>
            $"线{i + 1}  ({w.P1[0]:F1}, {w.P1[1]:F1}) → ({w.P2[0]:F1}, {w.P2[1]:F1})  {w.Length * um:F0} µm").ToList();
        if (select is { } s && s < _wires.Count) _list.SelectedIndex = s;
        _viewer.SelectedWire = _list.SelectedIndex >= 0 ? _list.SelectedIndex : null;
        _viewer.Refresh();
    }

    void LoadFields()
    {
        var p = _work;
        _name.Text = p.Name;
        _pixelUm.Value = (decimal)p.PixelUm;
        _wireDiam.Value = (decimal)p.WireDiamUm;
        _corridor.Value = (decimal)p.CorridorUm;
        _endTol.Value = (decimal)p.EndTolUm;
        _gapMax.Value = (decimal)p.GapMaxUm;
        _shiftMax.Value = (decimal)p.ShiftMaxUm;
        _sweepMax.Value = (decimal)p.SweepMaxPct;
        _zMissing.Value = (decimal)p.ZMissing;
        _zSeg.Value = (decimal)p.ZSeg;
        _zBall.Value = (decimal)p.ZBall;
        _matchThr.Value = (decimal)p.MatchThr;
        _polarity.SelectedIndex = p.Polarity == "bright" ? 1 : 0;
    }

    void ApplyFields(Product p)
    {
        static double V(NumericUpDown n) => (double)(n.Value ?? n.Minimum);
        p.Name = (_name.Text ?? "").Trim();
        p.PixelUm = V(_pixelUm);
        p.WireDiamUm = V(_wireDiam);
        p.CorridorUm = V(_corridor);
        p.EndTolUm = V(_endTol);
        p.GapMaxUm = V(_gapMax);
        p.ShiftMaxUm = V(_shiftMax);
        p.SweepMaxPct = V(_sweepMax);
        p.ZMissing = V(_zMissing);
        p.ZSeg = V(_zSeg);
        p.ZBall = V(_zBall);
        p.MatchThr = V(_matchThr);
        p.Polarity = _polarity.SelectedIndex == 1 ? "bright" : "dark";
    }

    Product? Candidate()
    {
        using var tpl = CropTemplate();
        if (tpl == null) return null;
        var p = new Product();
        ApplyFields(p);
        p.Template = tpl.Clone();
        p.Wires = _wires.Select(w => new WireSpec(w.P1[0], w.P1[1], w.P2[0], w.P2[1])).ToList();
        return p;
    }

    void AutoFind()
    {
        ExitPreview();
        var p = Candidate();
        if (p == null) { Hint("请先框选单元模板"); return; }
        var found = AutoWires.Find(p.Template!, p);
        p.Template!.Dispose();
        _wires.Clear();
        _wires.AddRange(found);
        Redraw();
        Hint(found.Count == 0
            ? "没有自动找到导线：请检查线径/像素尺寸，或用“手动添加”"
            : $"找到 {found.Count} 根线。请逐根核对：多余的删除，漏掉的手动添加，焊点1（橙圈）应在芯片侧，否则点“反向”");
    }

    void Calibrate()
    {
        if (_image == null || _source != _image) { Hint("需要整张图：请在主窗口选中图片后打开本对话框"); return; }
        using var tpl = CropTemplate();
        if (tpl == null) { Hint("请先框选单元模板"); return; }
        var um = AutoWires.CalibratePitch(_image, tpl, (double)(_matchThr.Value ?? 0.6m), (double)(_pitchMm.Value ?? 0));
        if (um == null) { Hint("图中没有找到左右相邻的单元，无法标定；请直接输入 µm/像素"); return; }
        _pixelUm.Value = (decimal)Math.Round(um.Value, 3);
        Redraw();
        Hint($"已按单元间距 {_pitchMm.Value} mm 标定：{um.Value:F3} µm/像素");
    }

    void Preview()
    {
        if (_image == null || _source != _image)
        {
            _btnPreview.IsChecked = false;
            Hint("需要整张图：请在主窗口选中图片后打开本对话框");
            return;
        }
        var p = Candidate();
        if (p == null || p.Wires.Count == 0)
        {
            _btnPreview.IsChecked = false;
            Hint("请先框选模板并设置至少一根导线");
            return;
        }
        _btnAdd.IsChecked = _btnBox.IsChecked = false;
        var res = Inspector.Detect(_image, p);
        p.Template!.Dispose();
        _viewer.ClearOverlays();
        foreach (var u in res.Units)
        {
            bool unitBad = res.Wires.Any(w => w.Unit == u.Index && w.Bad);
            _viewer.Units.Add(new UnitMark(new Rect(u.Box[0], u.Box[1], u.Box[2], u.Box[3]), Color.Parse(unitBad ? "#ff4d4f" : "#67d69c"),
                $"单元 {u.Index + 1} {u.Match:F2}"));
        }
        foreach (var w in res.Wires)
        {
            var c = Color.Parse(w.Bad ? "#ff4d4f" : "#2ee6ff");
            var pts = w.Flags.Contains("MISSING") || w.Curve.Count < 2
                ? new List<Point> { new(w.P1[0], w.P1[1]), new(w.P2[0], w.P2[1]) }
                : w.Curve.Select(q => new Point(q[0], q[1])).ToList();
            _viewer.Wires.Add(new WireMark(pts, c, 2, $"z {w.ZAll:F1}  sweep {w.SweepPct:F1}%", -1,
                w.Bad ? CsvReport.FlagText(w.Flags) : null, w.Flags.Contains("MISSING")));
            _viewer.Dots.Add(new DotMark(new Point(w.Ball[0], w.Ball[1]), 2.5, Color.Parse("#ffb000")));
        }
        _viewer.Refresh();
        int bad = res.Wires.Count(w => w.Bad);
        _previewInfo.Text = $"找到 {res.Units.Count} 个单元、{res.Wires.Count} 根线，其中 {bad} 根判为异常。"
                            + (res.Units.Count == 0 ? "\n没有匹配到单元：降低模板匹配阈值或重新框选。" : "")
                            + (bad > 0 ? "\n良品图上出现异常时，请核对导线位置或放宽对应阈值。" : "");
        Hint("预览：青色 = 正常，红色 = 异常；再点一次“预览检测”回到编辑");
    }

    async Task Save()
    {
        var p = Candidate();
        if (p == null) { await Dialogs.Info(this, "保存", "请先框选单元模板。"); return; }
        if (p.Name.Length == 0) { await Dialogs.Info(this, "保存", "请填写产品名称。"); return; }
        if (p.Wires.Count == 0) { await Dialogs.Info(this, "保存", "请至少设置一根导线。"); return; }
        var dir = Path.Combine(AppPaths.Products, Product.SafeName(p.Name));
        bool renamed = !_isNew && Product.SafeName(_originalName) != Product.SafeName(p.Name);
        if ((_isNew || renamed) && Directory.Exists(dir)
            && !await Dialogs.Confirm(this, "保存", $"产品「{p.Name}」已存在，覆盖？"))
            return;
        p.Note = _work.Note;
        p.NSeg = _work.NSeg;
        p.Save();
        Saved = Product.SafeName(p.Name);
        Close();
    }
}
