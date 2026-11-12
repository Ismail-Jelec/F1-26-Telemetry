using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using F126Telemetry;

namespace F126Overlay;

/// <summary>The customisation panel: sidebar navigation, live-applied settings, animated controls.</summary>
public sealed class ControlWindow : Window
{
    private readonly TelemetryHub _hub;
    private readonly Settings _settings;
    private readonly int _port;
    private readonly CancellationToken _ct;
    private readonly string? _startupError;

    private readonly OverlayWindow _lapLog;
    private readonly OverlayWindow _inputs;

    private readonly Border _root;
    private readonly ScaleTransform _rootScale = new(0.96, 0.96);
    private readonly Grid _pageHost = new();
    private readonly TextBlock _pageTitle = new();
    private readonly TextBlock _pageSubtitle = new();
    private readonly List<(NavItem Item, Func<UIElement> Build, string Title, string Subtitle)> _pages = [];

    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8 };
    private readonly SolidColorBrush _liveDotBrush = new(Theme.Muted);
    private readonly TextBlock _liveText = new();
    private readonly TextBlock _packetsText = new();
    private bool? _wasLive;

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _demoRunning;
    private bool _closing;

    public ControlWindow(TelemetryHub hub, Settings settings, string? startupError, CancellationToken ct)
    {
        _hub = hub;
        _settings = settings;
        _port = settings.Port;
        _ct = ct;
        _startupError = startupError;

        Title = "F1 26 Overlays";
        Width = 820; Height = 700;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Theme.Font;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };

        // Overlays
        var area = SystemParameters.WorkArea;
        _lapLog = new OverlayWindow("Lap time log", new LapLogView(hub, settings.LapLog), settings.LapLog,
            new Point(area.Right - 300, area.Top + 120), RequestSave);
        _inputs = new OverlayWindow("Inputs", new InputsView(hub, settings.Inputs), settings.Inputs,
            new Point(area.Left + area.Width / 2 - 140, area.Bottom - 130), RequestSave);

        // Window shell
        _root = new Border
        {
            Margin = new Thickness(20),
            CornerRadius = new CornerRadius(16),
            Background = Theme.B(Theme.WindowBg),
            BorderBrush = Theme.B(Theme.Line),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.6, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rootScale,
            Opacity = 0,
        };
        Content = _root;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.Child = layout;

        layout.Children.Add(BuildSidebar());
        var main = BuildMain();
        Grid.SetColumn(main, 1);
        layout.Children.Add(main);

        _pages.Add((null!, BuildLapLogPage, "Lap time log", "Personal best, session best and your latest laps"));
        _pages.Add((null!, BuildInputsPage, "Inputs", "Gear, speed and a live throttle / brake trace"));
        _pages.Add((null!, BuildGeneralPage, "General", "Locking, lap logging, demo data and reset"));
        BuildNav();

        Loaded += (_, _) =>
        {
            Anim.To(_root, OpacityProperty, 1, 280);
            Anim.To(_rootScale, ScaleTransform.ScaleXProperty, 1, 380, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });
            Anim.To(_rootScale, ScaleTransform.ScaleYProperty, 1, 380, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });

            if (_settings.LapLog.Enabled) _lapLog.FadeIn();
            if (_settings.Inputs.Enabled) _inputs.FadeIn();
            _lapLog.SetLocked(_settings.Locked);
            _inputs.SetLocked(_settings.Locked);
            SelectPage(0);
        };
        Closing += OnClosing;

        CompositionTarget.Rendering += OnFrame;
        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        statusTimer.Tick += (_, _) => UpdateStatus();
        statusTimer.Start();
        UpdateStatus();
    }

    // ───────────────────────────── shell ─────────────────────────────

    private UIElement BuildSidebar()
    {
        var side = new Border
        {
            Background = Theme.B(Theme.Sidebar),
            CornerRadius = new CornerRadius(16, 0, 0, 16),
            Padding = new Thickness(16, 20, 16, 16),
        };
        var dock = new DockPanel();
        side.Child = dock;

        // Brand (also a drag handle)
        var brand = new StackPanel { Margin = new Thickness(6, 0, 0, 28), Background = Brushes.Transparent };
        brand.Children.Add(new Border { Width = 34, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.B(Theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
        brand.Children.Add(new TextBlock { Text = "F1 26", FontSize = 26, FontWeight = FontWeights.Black, Foreground = Theme.B(Theme.TextColor) });
        brand.Children.Add(new TextBlock { Text = "OVERLAY STUDIO", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(1, -2, 0, 0) });
        brand.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(brand, Dock.Top);
        dock.Children.Add(brand);

        // Status pill at the bottom
        var status = new Border
        {
            CornerRadius = new CornerRadius(12), Background = Theme.B(Theme.Card), Padding = new Thickness(12, 10, 12, 10),
        };
        var sp = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _liveDot.Fill = _liveDotBrush;
        _liveDot.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_liveDot);
        _liveText.Margin = new Thickness(8, 0, 0, 0);
        _liveText.FontWeight = FontWeights.SemiBold;
        _liveText.FontSize = 13;
        _liveText.Foreground = Theme.B(Theme.TextColor);
        row.Children.Add(_liveText);
        sp.Children.Add(row);
        _packetsText.FontSize = 11;
        _packetsText.Foreground = Theme.B(Theme.Muted);
        _packetsText.Margin = new Thickness(16, 3, 0, 0);
        sp.Children.Add(_packetsText);
        status.Child = sp;
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);

        _navPanel = new StackPanel();
        dock.Children.Add(_navPanel);
        return side;
    }

    private StackPanel _navPanel = null!;

    private void BuildNav()
    {
        string[] icons = ["", "", ""];
        for (int i = 0; i < _pages.Count; i++)
        {
            int index = i;
            var item = new NavItem(icons[i], _pages[i].Title);
            item.Click += () => SelectPage(index);
            _navPanel.Children.Add(item);
            _pages[i] = (item, _pages[i].Build, _pages[i].Title, _pages[i].Subtitle);
        }
    }

    private UIElement BuildMain()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: page title + window buttons (drag handle)
        var top = new Grid { Background = Brushes.Transparent, Margin = new Thickness(28, 0, 14, 0) };
        top.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        _pageTitle.FontSize = 24; _pageTitle.FontWeight = FontWeights.Bold; _pageTitle.Foreground = Theme.B(Theme.TextColor);
        _pageSubtitle.FontSize = 13; _pageSubtitle.Foreground = Theme.B(Theme.Muted);
        titles.Children.Add(_pageTitle);
        titles.Children.Add(_pageSubtitle);
        top.Children.Add(titles);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(WindowButton("", Theme.C("#FF2A2A35"), () => WindowState = WindowState.Minimized));
        buttons.Children.Add(WindowButton("", Theme.Accent, Close));
        top.Children.Add(buttons);
        grid.Children.Add(top);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Padding = new Thickness(28, 4, 28, 24),
            Content = _pageHost,
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private static Border WindowButton(string glyph, Color hover, Action click)
    {
        var bg = new SolidColorBrush(Colors.Transparent);
        var b = new Border
        {
            Width = 34, Height = 30, CornerRadius = new CornerRadius(8), Background = bg, Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
                Foreground = Theme.B(Theme.TextColor), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        b.MouseEnter += (_, _) => Anim.ColorTo(bg, hover, 120);
        b.MouseLeave += (_, _) => Anim.ColorTo(bg, Colors.Transparent, 180);
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private void SelectPage(int index)
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Item.SetSelected(i == index);
        var page = _pages[index];
        _pageTitle.Text = page.Title;
        _pageSubtitle.Text = page.Subtitle;
        Anim.EnterUp(_pageTitle, 0, 8);
        Anim.EnterUp(_pageSubtitle, 40, 8);

        _pageHost.Children.Clear();
        var content = page.Build();
        _pageHost.Children.Add(content);

        // Stagger the cards in.
        if (content is Panel panel)
        {
            int n = 0;
            foreach (UIElement child in panel.Children) Anim.EnterUp(child, 60 + n++ * 55);
        }
    }

    // ───────────────────────────── pages ─────────────────────────────

    private UIElement BuildLapLogPage()
    {
        var s = _settings.LapLog;
        var w = _lapLog;
        var page = new StackPanel();

        page.Children.Add(Card("Overlay", null,
            RowInline("Show lap time log", "Fades the overlay in or out on your screen",
                Switch(s.Enabled, on => { s.Enabled = on; if (on) w.FadeIn(); else w.FadeOut(); RequestSave(); }))));

        page.Children.Add(AppearanceCard(s, w));

        page.Children.Add(Card("Content", "What the log shows",
            RowStacked("Lap rows", MakeSlider(3, 20, 1, s.Rows, v => $"{v:0}", v => { s.Rows = (int)v; w.ApplyAppearance(); RequestSave(); })),
            RowInline("Session best row", "Purple row with the fastest lap of any car", Switch(s.ShowSessionBest, on => { s.ShowSessionBest = on; w.ApplyAppearance(); RequestSave(); })),
            RowInline("Gap to personal best", "PS. BEST column", Switch(s.ShowDelta, on => { s.ShowDelta = on; w.ApplyAppearance(); RequestSave(); })),
            RowInline("Fuel column", "Fuel left at the end of each lap", Switch(s.ShowFuel, on => { s.ShowFuel = on; w.ApplyAppearance(); RequestSave(); }))));

        page.Children.Add(Card("Colours", null,
            RowStacked("Accent stripe", Swatches(s.AccentColor, c => { s.AccentColor = c; RequestSave(); })),
            RowStacked("Session best", Swatches(s.SessionBestColor, c => { s.SessionBestColor = c; RequestSave(); })),
            RowStacked("Column headers", Swatches(s.HeaderColor, c => { s.HeaderColor = c; RequestSave(); }))));

        page.Children.Add(PositionCard(w));
        return page;
    }

    private UIElement BuildInputsPage()
    {
        var s = _settings.Inputs;
        var w = _inputs;
        var page = new StackPanel();

        page.Children.Add(Card("Overlay", null,
            RowInline("Show inputs overlay", "Gear, speed, trace and pedal bars",
                Switch(s.Enabled, on => { s.Enabled = on; if (on) w.FadeIn(); else w.FadeOut(); RequestSave(); }))));

        page.Children.Add(AppearanceCard(s, w));

        page.Children.Add(Card("Content", null,
            RowStacked("Trace length", MakeSlider(2, 10, 0.5, s.TraceSeconds, v => $"{v:0.#} s", v => { s.TraceSeconds = v; RequestSave(); })),
            RowInline("Speed", "Shown under the gear", Switch(s.ShowSpeed, on => { s.ShowSpeed = on; w.ApplyAppearance(); RequestSave(); })),
            RowInline("Pedal bars", "Throttle and brake bars on the right", Switch(s.ShowBars, on => { s.ShowBars = on; w.ApplyAppearance(); RequestSave(); })),
            RowInline("Fill under trace", "Soft colour under the throttle and brake lines", Switch(s.FillTrace, on => { s.FillTrace = on; RequestSave(); }))));

        page.Children.Add(Card("Colours", null,
            RowStacked("Gear", Swatches(s.GearColor, c => { s.GearColor = c; RequestSave(); })),
            RowStacked("Throttle", Swatches(s.ThrottleColor, c => { s.ThrottleColor = c; RequestSave(); })),
            RowStacked("Brake", Swatches(s.BrakeColor, c => { s.BrakeColor = c; RequestSave(); }))));

        page.Children.Add(PositionCard(w));
        return page;
    }

    private UIElement BuildGeneralPage()
    {
        var page = new StackPanel();

        if (_startupError != null)
            page.Children.Add(Card("Connection problem", _startupError));

        page.Children.Add(Card("Overlays", null,
            RowInline("Lock overlays", "Clicks go through to the game. Unlock to drag overlays (they get a dashed outline).",
                Switch(_settings.Locked, on => { _settings.Locked = on; _lapLog.SetLocked(on); _inputs.SetLocked(on); RequestSave(); }))));

        var dataButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var folder = new PillButton("Open lap folder");
        folder.Click += () =>
        {
            Directory.CreateDirectory(Settings.LapLogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", Settings.LapLogDir) { UseShellExecute = true });
        };
        var demo = new PillButton(_demoRunning ? "Demo running" : "Run demo data", primary: !_demoRunning);
        demo.Click += () =>
        {
            if (_demoRunning) return;
            _demoRunning = true;
            Task.Run(() => DemoSender.Run(_port, _ct));
            ((TextBlock)demo.Child).Text = "Demo running";
        };
        dataButtons.Children.Add(demo);
        dataButtons.Children.Add(folder);

        page.Children.Add(Card("Data", null,
            RowInline("Save laps to CSV", "Applies after restarting the app", Switch(_settings.LogLaps, on => { _settings.LogLaps = on; RequestSave(); })),
            dataButtons));

        page.Children.Add(Card("Connection",
            $"Listening on UDP port {_port}. With SimHub, forward its F1 data to 127.0.0.1:{_port}. " +
            "Play in Borderless or Windowed mode: Windows can't draw overlays over exclusive fullscreen."));

        var reset = new PillButton("Reset all settings");
        reset.Click += () =>
        {
            bool lapOn = _settings.LapLog.Enabled, inOn = _settings.Inputs.Enabled;
            _settings.LapLog.ResetToDefaults();
            _settings.Inputs.ResetToDefaults();
            _lapLog.ApplyAppearance(); _inputs.ApplyAppearance();
            _lapLog.ResetPosition(); _inputs.ResetPosition();
            if (!lapOn) _lapLog.FadeIn();
            if (!inOn) _inputs.FadeIn();
            RequestSave();
            SelectPage(2);
        };
        page.Children.Add(Card("Reset", "Puts colours, sizes and positions of both overlays back to default.", reset));
        return page;
    }

    // ───────────────────────────── building blocks ─────────────────────────────

    private Border AppearanceCard(OverlaySettings s, OverlayWindow w) =>
        Card("Appearance", "Changes apply live",
            RowStacked("Size", MakeSlider(0.5, 2.0, 0.05, s.Scale, v => $"{v * 100:0}%", v => { s.Scale = v; w.ApplyAppearance(); RequestSave(); })),
            RowStacked("Opacity", MakeSlider(0.2, 1.0, 0.05, s.Opacity, v => $"{v * 100:0}%", v => { s.Opacity = v; w.ApplyAppearance(); RequestSave(); })),
            RowStacked("Background", MakeSlider(0, 1.0, 0.05, s.BackgroundOpacity, v => $"{v * 100:0}%", v => { s.BackgroundOpacity = v; RequestSave(); })));

    private Border PositionCard(OverlayWindow w)
    {
        var reset = new PillButton("Reset position");
        reset.Click += w.ResetPosition;
        var unlock = new PillButton("Unlock to move");
        unlock.Click += () =>
        {
            _settings.Locked = false; _lapLog.SetLocked(false); _inputs.SetLocked(false); RequestSave();
        };
        var row = new WrapPanel();
        row.Children.Add(unlock);
        row.Children.Add(reset);
        return Card("Position", "Unlock, then drag the overlay with your mouse. Lock it again in General.", row);
    }

    private static Border Card(string title, string? subtitle, params UIElement[] rows)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Theme.B(Theme.TextColor) });
        if (subtitle != null)
            sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) sp.Children.Add(new Border { Height = 1, Background = Theme.B(Theme.Line), Margin = new Thickness(0, 12, 0, 12) });
            else sp.Children.Add(new Border { Height = 14 });
            sp.Children.Add(rows[i]);
        }

        var bg = new SolidColorBrush(Theme.Card);
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Background = bg, BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, 14), Child = sp,
        };
        card.MouseEnter += (_, _) => Anim.ColorTo(bg, Theme.CardHover, 200);
        card.MouseLeave += (_, _) => Anim.ColorTo(bg, Theme.Card, 300);
        return card;
    }

    private static UIElement RowInline(string label, string? description, UIElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.TextColor) });
        if (description != null)
            text.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static UIElement RowStacked(string label, UIElement control)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.B(Theme.TextColor), Margin = new Thickness(0, 0, 0, 8) });
        sp.Children.Add(control);
        return sp;
    }

    private static ToggleSwitch Switch(bool initial, Action<bool> changed)
    {
        var t = new ToggleSwitch(initial, Theme.Accent);
        t.Toggled += changed;
        return t;
    }

    private static FancySlider MakeSlider(double min, double max, double step, double value, Func<double, string> format, Action<double> changed)
    {
        var s = new FancySlider(min, max, step, value, format, Theme.Accent);
        s.ValueChanged += changed;
        return s;
    }

    private static ColorSwatches Swatches(string selected, Action<string> changed)
    {
        var s = new ColorSwatches(selected);
        s.ColorChanged += changed;
        return s;
    }

    // ───────────────────────────── loop & lifecycle ─────────────────────────────

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_lapLog.IsVisible) _lapLog.View.InvalidateVisual();
        if (_inputs.IsVisible) _inputs.View.InvalidateVisual();
    }

    private void UpdateStatus()
    {
        bool live;
        string text;
        long packets;
        lock (_hub.Sync)
        {
            live = (DateTime.UtcNow - _hub.LastPacketUtc).TotalSeconds < 2;
            packets = _hub.PacketCount;
            text = _hub.WrongFormat is ushort fmt ? $"Set UDP format to 2026 (got {fmt})" : live ? "Live" : "Waiting for data";
        }
        _liveText.Text = text;
        _packetsText.Text = live ? $"{packets:N0} packets · UDP {_port}" : $"UDP {_port}";

        if (_wasLive == live) return;
        _wasLive = live;
        Anim.ColorTo(_liveDotBrush, live ? Theme.Live : Theme.C("#FFFFA21A"), 300);
        if (live)
            _liveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, Theme.Ms(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        else
        {
            _liveDot.BeginAnimation(OpacityProperty, null);
            _liveDot.Opacity = 1;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { CompositionTarget.Rendering -= OnFrame; _settings.Save(); return; }

        // Play a short exit animation first, then close for real.
        e.Cancel = true;
        _closing = true;
        _lapLog.FadeOut();
        _inputs.FadeOut();
        var fade = new DoubleAnimation(0, Theme.Ms(180)) { EasingFunction = Anim.Out };
        fade.Completed += (_, _) => Close();
        _root.BeginAnimation(OpacityProperty, fade);
        Anim.To(_rootScale, ScaleTransform.ScaleXProperty, 0.96, 180);
        Anim.To(_rootScale, ScaleTransform.ScaleYProperty, 0.96, 180);
    }
}
