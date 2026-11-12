using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace F126Overlay;

/// <summary>Animation helpers.</summary>
public static class Anim
{
    public static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static void To(IAnimatable target, DependencyProperty prop, double to, double ms, IEasingFunction? ease = null) =>
        target.BeginAnimation(prop, new DoubleAnimation(to, Theme.Ms(ms)) { EasingFunction = ease ?? Out });

    public static void ColorTo(SolidColorBrush brush, Color to, double ms) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Theme.Ms(ms)) { EasingFunction = Out });

    /// <summary>Fades and slides an element in from slightly below.</summary>
    public static void EnterUp(UIElement el, double delayMs = 0, double distance = 14)
    {
        var tt = new TranslateTransform(0, distance);
        el.RenderTransform = tt;
        el.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, Theme.Ms(260)) { BeginTime = begin, EasingFunction = Out });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(distance, 0, Theme.Ms(320)) { BeginTime = begin, EasingFunction = Out });
    }
}

/// <summary>iOS-style on/off switch with an animated knob and track colour.</summary>
public sealed class ToggleSwitch : Border
{
    private readonly TranslateTransform _knobX = new();
    private readonly SolidColorBrush _track;
    private readonly Color _accent;
    private bool _on;

    public event Action<bool>? Toggled;
    public bool IsOn => _on;

    public ToggleSwitch(bool on, Color accent)
    {
        _on = on;
        _accent = accent;
        Width = 42; Height = 24;
        CornerRadius = new CornerRadius(12);
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        _track = new SolidColorBrush(on ? accent : Theme.Track);
        Background = _track;
        _knobX.X = on ? 18 : 0;
        Child = new Ellipse
        {
            Width = 18, Height = 18, Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0),
            RenderTransform = _knobX,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.45 },
        };
        MouseLeftButtonUp += (_, _) => SetOn(!_on, raise: true);
    }

    public void SetOn(bool on, bool raise)
    {
        _on = on;
        Anim.To(_knobX, TranslateTransform.XProperty, on ? 18 : 0, 220, new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut });
        Anim.ColorTo(_track, on ? _accent : Theme.Track, 200);
        if (raise) Toggled?.Invoke(on);
    }
}

/// <summary>Flat slider with an accent fill, a thumb that grows on hover, and a value label.</summary>
public sealed class FancySlider : Grid
{
    private readonly double _min, _max, _step;
    private readonly Func<double, string> _format;
    private readonly Grid _area = new() { Height = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Border _fill;
    private readonly Ellipse _thumb;
    private readonly ScaleTransform _thumbScale = new(1, 1);
    private readonly TextBlock _label;
    private double _value;

    public event Action<double>? ValueChanged;

    public FancySlider(double min, double max, double step, double value, Func<double, string> format, Color accent)
    {
        _min = min; _max = max; _step = step; _format = format;
        _value = Snap(value);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

        _area.Children.Add(new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.B(Theme.Track),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
        });
        _fill = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.B(accent),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _area.Children.Add(_fill);
        _thumb = new Ellipse
        {
            Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _thumbScale,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.5 },
        };
        _area.Children.Add(_thumb);
        Children.Add(_area);

        _label = new TextBlock
        {
            Foreground = Theme.B(Theme.TextColor), FontFamily = Theme.Font, FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        SetColumn(_label, 1);
        Children.Add(_label);

        _area.SizeChanged += (_, _) => UpdateVisuals();
        _area.MouseEnter += (_, _) => Grow(1.25);
        _area.MouseLeave += (_, _) => { if (!_area.IsMouseCaptured) Grow(1); };
        _area.MouseLeftButtonDown += (_, e) => { _area.CaptureMouse(); SetFromMouse(e.GetPosition(_area).X); Grow(1.35); };
        _area.MouseMove += (_, e) => { if (_area.IsMouseCaptured) SetFromMouse(e.GetPosition(_area).X); };
        _area.MouseLeftButtonUp += (_, _) => { _area.ReleaseMouseCapture(); Grow(_area.IsMouseOver ? 1.25 : 1); };
        UpdateVisuals();
    }

    public double Value => _value;

    private void Grow(double s)
    {
        Anim.To(_thumbScale, ScaleTransform.ScaleXProperty, s, 150);
        Anim.To(_thumbScale, ScaleTransform.ScaleYProperty, s, 150);
    }

    private double Snap(double v)
    {
        v = Math.Clamp(v, _min, _max);
        return _step > 0 ? Math.Round((v - _min) / _step) * _step + _min : v;
    }

    private void SetFromMouse(double x)
    {
        double usable = Math.Max(1, _area.ActualWidth - 16);
        double frac = Math.Clamp((x - 8) / usable, 0, 1);
        double v = Snap(_min + frac * (_max - _min));
        if (Math.Abs(v - _value) < 1e-9) return;
        _value = v;
        UpdateVisuals();
        ValueChanged?.Invoke(v);
    }

    private void UpdateVisuals()
    {
        double frac = (_value - _min) / (_max - _min);
        double usable = Math.Max(0, _area.ActualWidth - 16);
        _fill.Width = Math.Max(0, frac * usable);
        _thumb.Margin = new Thickness(frac * usable, 0, 0, 0);
        _label.Text = _format(_value);
    }
}

/// <summary>Row of round colour swatches; the selected one gets a white ring.</summary>
public sealed class ColorSwatches : WrapPanel
{
    private readonly List<(string Hex, Border El)> _items = [];
    public event Action<string>? ColorChanged;

    public ColorSwatches(string selected)
    {
        var all = Theme.Presets.ToList();
        if (!all.Contains(selected, StringComparer.OrdinalIgnoreCase)) all.Add(selected);

        foreach (var hex in all)
        {
            var scale = new ScaleTransform(1, 1);
            var el = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 7, 7),
                Background = Theme.B(Theme.Parse(hex, Colors.White)), Cursor = Cursors.Hand,
                BorderBrush = Brushes.White, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
                ToolTip = hex,
            };
            el.MouseEnter += (_, _) => { Anim.To(scale, ScaleTransform.ScaleXProperty, 1.15, 120); Anim.To(scale, ScaleTransform.ScaleYProperty, 1.15, 120); };
            el.MouseLeave += (_, _) => { Anim.To(scale, ScaleTransform.ScaleXProperty, 1, 120); Anim.To(scale, ScaleTransform.ScaleYProperty, 1, 120); };
            el.MouseLeftButtonUp += (_, _) => { Select(hex); ColorChanged?.Invoke(hex); };
            _items.Add((hex, el));
            Children.Add(el);
        }
        Select(selected);
    }

    public void Select(string hex)
    {
        foreach (var (h, el) in _items)
            el.BorderThickness = new Thickness(string.Equals(h, hex, StringComparison.OrdinalIgnoreCase) ? 2.5 : 0);
    }
}

/// <summary>Rounded button with hover and press animations.</summary>
public sealed class PillButton : Border
{
    private readonly SolidColorBrush _bg;
    private readonly ScaleTransform _scale = new(1, 1);
    public event Action? Click;

    public PillButton(string text, bool primary = false)
    {
        var baseColor = primary ? Theme.Accent : Theme.CardHover;
        var hover = primary ? Theme.C("#FFFF2A1F") : Theme.C("#FF2A2A35");
        _bg = new SolidColorBrush(baseColor);
        Background = _bg;
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(14, 8, 14, 8);
        Margin = new Thickness(0, 0, 8, 8);
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _scale;
        Child = new TextBlock
        {
            Text = text, Foreground = Brushes.White, FontFamily = Theme.Font, FontSize = 13, FontWeight = FontWeights.SemiBold,
        };
        MouseEnter += (_, _) => Anim.ColorTo(_bg, hover, 150);
        MouseLeave += (_, _) => { Anim.ColorTo(_bg, baseColor, 200); Press(1); };
        MouseLeftButtonDown += (_, _) => Press(0.95);
        MouseLeftButtonUp += (_, _) => { Press(1); Click?.Invoke(); };
    }

    private void Press(double s)
    {
        Anim.To(_scale, ScaleTransform.ScaleXProperty, s, 100);
        Anim.To(_scale, ScaleTransform.ScaleYProperty, s, 100);
    }
}

/// <summary>Sidebar navigation entry with an animated accent bar.</summary>
public sealed class NavItem : Border
{
    private readonly SolidColorBrush _bg = new(Colors.Transparent);
    private readonly ScaleTransform _barScale = new(1, 0);
    private readonly TextBlock _title;
    private bool _selected;
    public event Action? Click;

    public NavItem(string icon, string title)
    {
        Background = _bg;
        CornerRadius = new CornerRadius(10);
        Margin = new Thickness(0, 0, 0, 4);
        Padding = new Thickness(0, 10, 12, 10);
        Cursor = Cursors.Hand;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Background = Theme.B(Theme.Accent),
            HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _barScale,
        };
        grid.Children.Add(bar);

        var iconText = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Theme.B(Theme.Muted), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(iconText, 1);
        grid.Children.Add(iconText);

        _title = new TextBlock { Text = title, FontFamily = Theme.Font, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.Muted), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_title, 2);
        grid.Children.Add(_title);
        Child = grid;

        MouseEnter += (_, _) => { if (!_selected) Anim.ColorTo(_bg, Theme.C("#FF16161C"), 150); };
        MouseLeave += (_, _) => { if (!_selected) Anim.ColorTo(_bg, Colors.Transparent, 200); };
        MouseLeftButtonUp += (_, _) => Click?.Invoke();
    }

    public void SetSelected(bool selected)
    {
        _selected = selected;
        Anim.To(_barScale, ScaleTransform.ScaleYProperty, selected ? 1 : 0, 250, new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut });
        Anim.ColorTo(_bg, selected ? Theme.Card : Colors.Transparent, 200);
        _title.Foreground = Theme.B(selected ? Theme.TextColor : Theme.Muted);
    }
}

/// <summary>Segmented control with a sliding highlight, used for sub-tabs.</summary>
public sealed class SegmentedTabs : Border
{
    private readonly Grid _grid = new();
    private readonly Border _highlight;
    private readonly TranslateTransform _move = new();
    private readonly List<TextBlock> _labels = [];
    private int _index;

    public event Action<int>? Selected;

    public SegmentedTabs(string[] items, int selected = 0)
    {
        _index = selected;
        Background = Theme.B(Theme.Sidebar);
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(4);
        Height = 42;
        Child = _grid;

        foreach (var _ in items) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _highlight = new Border
        {
            CornerRadius = new CornerRadius(8), Background = Theme.B(Theme.CardHover),
            BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _move,
        };
        Grid.SetColumnSpan(_highlight, items.Length);
        _grid.Children.Add(_highlight);

        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            var label = new TextBlock
            {
                Text = items[i], FontFamily = Theme.Font, FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.B(i == selected ? Theme.TextColor : Theme.Muted),
            };
            var hit = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = label };
            hit.MouseLeftButtonUp += (_, _) => Select(index, raise: true);
            Grid.SetColumn(hit, i);
            _grid.Children.Add(hit);
            _labels.Add(label);
        }

        _grid.SizeChanged += (_, _) =>
        {
            double segment = _grid.ActualWidth / _labels.Count;
            _highlight.Width = segment;
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _move.X = segment * _index;
        };
    }

    public void Select(int index, bool raise)
    {
        _index = index;
        double segment = _grid.ActualWidth / Math.Max(1, _labels.Count);
        Anim.To(_move, TranslateTransform.XProperty, segment * index, 260, new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut });
        for (int i = 0; i < _labels.Count; i++) _labels[i].Foreground = Theme.B(i == index ? Theme.TextColor : Theme.Muted);
        if (raise) Selected?.Invoke(index);
    }
}

/// <summary>A row of selectable pills (single choice).</summary>
public sealed class ChoiceChips : WrapPanel
{
    private readonly List<(string Value, SolidColorBrush Bg)> _chips = [];
    public event Action<string>? Changed;

    public ChoiceChips(IEnumerable<(string Value, string Label)> options, string selected, bool previewFont = false)
    {
        foreach (var (value, label) in options)
        {
            var bg = new SolidColorBrush(value == selected ? Theme.Accent : Theme.CardHover);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(16), Background = bg, Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    FontFamily = previewFont ? new FontFamily(value + ", Segoe UI") : Theme.Font,
                },
            };
            chip.MouseLeftButtonUp += (_, _) => { Select(value); Changed?.Invoke(value); };
            _chips.Add((value, bg));
            Children.Add(chip);
        }
    }

    private void Select(string value)
    {
        foreach (var (v, bg) in _chips) Anim.ColorTo(bg, v == value ? Theme.Accent : Theme.CardHover, 180);
    }
}

/// <summary>Swatches plus a hex box for any custom colour.</summary>
public sealed class ColorField : StackPanel
{
    public event Action<string>? ColorChanged;

    public ColorField(string selected)
    {
        var swatches = new ColorSwatches(selected);
        Children.Add(swatches);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var preview = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1),
            Background = Theme.B(Theme.Parse(selected, Colors.White)),
        };
        var box = Ui.TextField(selected.ToUpperInvariant(), 100);
        box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        box.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(preview);
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "custom hex", Foreground = Theme.B(Theme.Muted), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        Children.Add(row);

        bool fromSwatch = false;
        swatches.ColorChanged += hex =>
        {
            fromSwatch = true;
            box.Text = hex.ToUpperInvariant();
            fromSwatch = false;
            preview.Background = Theme.B(Theme.Parse(hex, Colors.White));
            ColorChanged?.Invoke(hex);
        };
        box.TextChanged += (_, _) =>
        {
            if (fromSwatch) return;
            var text = box.Text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length != 7 && text.Length != 9) return;
            try
            {
                var c = Theme.C(text);
                preview.Background = Theme.B(c);
                swatches.Select(text);
                ColorChanged?.Invoke(text.ToUpperInvariant());
            }
            catch { /* not a colour yet */ }
        };
    }
}

/// <summary>Small factory helpers for consistently styled inputs.</summary>
public static class Ui
{
    public static TextBox TextField(string text, double width)
    {
        return new TextBox
        {
            Text = text, Width = width, Height = 30, Padding = new Thickness(8, 4, 8, 4),
            Background = Theme.B(Theme.Sidebar), Foreground = Theme.B(Theme.TextColor),
            BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White, SelectionBrush = Theme.B(Theme.Accent),
            FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center,
        };
    }
}
