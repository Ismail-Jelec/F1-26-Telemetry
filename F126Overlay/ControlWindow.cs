using System.Diagnostics;
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

/// <summary>The customisation panel: sidebar navigation, live preview, sub-tabs and live-applied settings.</summary>
public sealed class ControlWindow : Window
{
    private static readonly (string Value, string Label)[] FontChoices =
    [
        ("Segoe UI", "Segoe UI"), ("Bahnschrift", "Bahnschrift"), ("Arial", "Arial"),
        ("Verdana", "Verdana"), ("Trebuchet MS", "Trebuchet"), ("Consolas", "Consolas"),
    ];

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
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _pageTitle = new();
    private readonly TextBlock _pageSubtitle = new();
    private readonly List<(NavItem Item, Func<UIElement> Build, string Title, string Subtitle)> _pages = [];
    private int _currentPage;
    private readonly Dictionary<string, int> _subTab = new() { ["laplog"] = 0, ["inputs"] = 0 };
    private readonly List<OverlayView> _previews = [];

    private readonly Ellipse _liveDot = new() { Width = 8, Height = 8 };
    private readonly SolidColorBrush _liveDotBrush = new(Theme.Muted);
    private readonly TextBlock _liveText = new();
    private readonly TextBlock _packetsText = new();
    private bool? _wasLive;

    private readonly Border _toast;
    private readonly TextBlock _toastText = new();
    private readonly TranslateTransform _toastMove = new(0, 20);

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private StackPanel _navPanel = null!;
    private bool _demoRunning;
    private bool _closing;

    public ControlWindow(TelemetryHub hub, Settings settings, string? startupError, CancellationToken ct)
    {
        _hub = hub;
        _settings = settings;
        _port = settings.Port;
        _ct = ct;
        _startupError = startupError;

        Title = "F1 26 Overlay Studio";
        Width = 920; Height = 760;
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

        var overlayGrid = new Grid();
        _root.Child = overlayGrid;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overlayGrid.Children.Add(layout);

        layout.Children.Add(BuildSidebar());
        var main = BuildMain(out _scroll);
        Grid.SetColumn(main, 1);
        layout.Children.Add(main);

        // Toast
        _toastText.Foreground = Brushes.White;
        _toastText.FontSize = 13;
        _toastText.FontWeight = FontWeights.SemiBold;
        _toast = new Border
        {
            Background = Theme.B(Theme.CardHover), BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(220, 0, 0, 22), Child = _toastText, Opacity = 0, IsHitTestVisible = false,
            RenderTransform = _toastMove,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5 },
        };
        overlayGrid.Children.Add(_toast);

        _pages.Add((null!, BuildLapLogPage, "Lap time log", "Personal best, session best and your latest laps"));
        _pages.Add((null!, BuildInputsPage, "Inputs", "Gear, speed and a live throttle / brake trace"));
        _pages.Add((null!, BuildThemesPage, "Themes", "One-click looks for both overlays"));
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
        var status = new Border { CornerRadius = new CornerRadius(12), Background = Theme.B(Theme.Card), Padding = new Thickness(12, 10, 12, 10) };
        var sp = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _liveDot.Fill = _liveDotBrush;
        _liveDot.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_liveDot);
        _liveText.Margin = new Thickness(8, 0, 0, 0);
        _liveText.FontWeight = FontWeights.SemiBold;
        _liveText.FontSize = 13;
        _liveText.Foreground = Theme.B(Theme.TextColor);
        _liveText.TextWrapping = TextWrapping.Wrap;
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

    private void BuildNav()
    {
        string[] icons = ["", "", "", ""];
        for (int i = 0; i < _pages.Count; i++)
        {
            int index = i;
            var item = new NavItem(icons[i], _pages[i].Title);
            item.Click += () => SelectPage(index);
            _navPanel.Children.Add(item);
            _pages[i] = (item, _pages[i].Build, _pages[i].Title, _pages[i].Subtitle);
        }
    }

    private UIElement BuildMain(out ScrollViewer scroll)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Top bar: page title + window buttons. The empty area drags the window.
        var top = new Grid { Background = Brushes.Transparent, Margin = new Thickness(28, 0, 14, 0) };
        top.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && !e.Handled) DragMove(); };
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

        scroll = new ScrollViewer
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
        // Mark the press as handled so the title bar doesn't start a window drag and eat the click.
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private void SelectPage(int index, bool animateTitle = true)
    {
        _currentPage = index;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Item.SetSelected(i == index);
        var page = _pages[index];
        _pageTitle.Text = page.Title;
        _pageSubtitle.Text = page.Subtitle;
        if (animateTitle)
        {
            Anim.EnterUp(_pageTitle, 0, 8);
            Anim.EnterUp(_pageSubtitle, 40, 8);
        }

        _previews.Clear();
        _pageHost.Children.Clear();
        var content = page.Build();
        _pageHost.Children.Add(content);
        _scroll.ScrollToTop();
        Stagger(content);
    }

    private static void Stagger(UIElement content)
    {
        if (content is not Panel panel) return;
        int n = 0;
        foreach (UIElement child in panel.Children) Anim.EnterUp(child, 40 + n++ * 50);
    }

    private void ShowToast(string message)
    {
        _toastText.Text = message;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), Anim.Out as EasingFunctionBase));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        _toast.BeginAnimation(OpacityProperty, fade);
        _toastMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, Theme.Ms(300)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
    }

    // ───────────────────────────── overlay pages ─────────────────────────────

    /// <summary>Preview card on top, sub-tabs below; the selected tab's cards are swapped in with an animation.</summary>
    private UIElement OverlayPage(string key, OverlaySettings s, OverlayWindow w, OverlayView preview, string showLabel,
        (string Name, Func<UIElement[]> Cards)[] tabs)
    {
        var page = new StackPanel();
        _previews.Add(preview);

        // Live preview on a "track" background
        var stage = new Border
        {
            CornerRadius = new CornerRadius(12), MinHeight = 170, Padding = new Thickness(20), ClipToBounds = true,
            Background = new LinearGradientBrush(Theme.C("#FF2B3440"), Theme.C("#FF101318"), 65),
        };
        stage.Child = new Viewbox
        {
            Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = preview,
        };

        var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headText.Children.Add(new TextBlock { Text = "Live preview", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Theme.B(Theme.TextColor) });
        headText.Children.Add(new TextBlock { Text = "Updates as you change settings. Use demo data if the game isn't running.", FontSize = 12, Foreground = Theme.B(Theme.Muted), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(headText);
        var headRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!_demoRunning)
        {
            var demo = new PillButton("Run demo");
            demo.Margin = new Thickness(0, 0, 14, 0);
            demo.Click += () => { StartDemo(); demo.Visibility = Visibility.Collapsed; };
            headRight.Children.Add(demo);
        }
        headRight.Children.Add(new TextBlock { Text = showLabel, Foreground = Theme.B(Theme.TextColor), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        headRight.Children.Add(Switch(s.Enabled, on => { s.Enabled = on; if (on) w.FadeIn(); else w.FadeOut(); RequestSave(); }));
        Grid.SetColumn(headRight, 1);
        head.Children.Add(headRight);

        var previewCard = CardRaw(new StackPanel { Children = { head, stage } });
        page.Children.Add(previewCard);

        // Sub-tabs
        var tabHost = new StackPanel();
        void ShowTab(int i)
        {
            _subTab[key] = i;
            tabHost.Children.Clear();
            foreach (var card in tabs[i].Cards()) tabHost.Children.Add(card);
            Stagger(tabHost);
        }
        var seg = new SegmentedTabs(tabs.Select(t => t.Name).ToArray(), _subTab[key]) { Margin = new Thickness(0, 0, 0, 14) };
        seg.Selected += ShowTab;
        page.Children.Add(seg);
        page.Children.Add(tabHost);
        ShowTab(_subTab[key]);
        return page;
    }

    private UIElement BuildLapLogPage()
    {
        var s = _settings.LapLog;
        var w = _lapLog;
        void C() { w.ApplyAppearance(); RequestSave(); }

        return OverlayPage("laplog", s, w, new LapLogView(_hub, s), "Show in game",
        [
            ("Layout", () =>
            [
                Card("Size", "Changes apply live",
                    RowStacked("Overall size", MakeSlider(0.5, 2.0, 0.05, s.Scale, Pct, v => { s.Scale = v; C(); })),
                    RowStacked("Width", MakeSlider(200, 420, 5, s.Width, Px, v => { s.Width = v; C(); })),
                    RowStacked("Row height", MakeSlider(18, 40, 1, s.RowHeight, Px, v => { s.RowHeight = v; C(); })),
                    RowStacked("Text size", MakeSlider(0.6, 1.8, 0.05, s.TextScale, Pct, v => { s.TextScale = v; C(); })),
                    RowStacked("Lap rows", MakeSlider(3, 20, 1, s.Rows, v => $"{v:0}", v => { s.Rows = (int)v; C(); })),
                    RowStacked("Corner radius", MakeSlider(0, 20, 1, s.CornerRadius, Px, v => { s.CornerRadius = v; C(); }))),
                TransparencyCard(s, C),
                FontCard(s, C),
                PositionCard(w),
            ]),
            ("Content", () =>
            [
                Card("Sections", "Turn parts of the log on or off",
                    RowInline("Accent stripe", "Thin coloured line on top", Switch(s.ShowAccentStripe, on => { s.ShowAccentStripe = on; C(); })),
                    RowInline("Title bar", null, Switch(s.ShowTitle, on => { s.ShowTitle = on; C(); })),
                    RowInline("Title text", null, TitleBox(s, C)),
                    RowInline("Personal best row", "Your fastest valid lap this session", Switch(s.ShowPersonalBest, on => { s.ShowPersonalBest = on; C(); })),
                    RowInline("Session best row", "Fastest valid lap of any car", Switch(s.ShowSessionBest, on => { s.ShowSessionBest = on; C(); })),
                    RowInline("Column headers", "LAP / TIME / PS. BEST / FUEL", Switch(s.ShowColumnHeaders, on => { s.ShowColumnHeaders = on; C(); }))),
                Card("Columns", null,
                    RowInline("Gap to personal best", "PS. BEST column", Switch(s.ShowDelta, on => { s.ShowDelta = on; C(); })),
                    RowInline("Fuel", "Fuel left at the end of each lap", Switch(s.ShowFuel, on => { s.ShowFuel = on; C(); }))),
                Card("Rows", null,
                    RowInline("Striped rows", "Alternate row shading", Switch(s.Stripes, on => { s.Stripes = on; C(); })),
                    RowInline("Newest lap on top", "Off puts the newest lap at the bottom", Switch(s.NewestOnTop, on => { s.NewestOnTop = on; C(); }))),
            ]),
            ("Colours", () =>
            [
                Card("Panel", null,
                    RowStacked("Background", Colour(s.BackgroundColor, c => { s.BackgroundColor = c; C(); })),
                    RowStacked("Title bar & column headers", Colour(s.TitleBarColor, c => { s.TitleBarColor = c; C(); })),
                    RowStacked("Row stripes", Colour(s.StripeColor, c => { s.StripeColor = c; C(); }))),
                Card("Highlights", null,
                    RowStacked("Accent stripe", Colour(s.AccentColor, c => { s.AccentColor = c; C(); })),
                    RowStacked("Personal best row", Colour(s.PersonalBestColor, c => { s.PersonalBestColor = c; C(); })),
                    RowStacked("Session best row", Colour(s.SessionBestColor, c => { s.SessionBestColor = c; C(); }))),
                Card("Text", null,
                    RowStacked("Text", Colour(s.TextColor, c => { s.TextColor = c; C(); })),
                    RowStacked("Column headers", Colour(s.HeaderColor, c => { s.HeaderColor = c; C(); })),
                    RowStacked("Faster than PB", Colour(s.FasterColor, c => { s.FasterColor = c; C(); })),
                    RowStacked("Slower than PB", Colour(s.SlowerColor, c => { s.SlowerColor = c; C(); }))),
            ]),
            ("Effects", () =>
            [
                Card("Animations", "Small touches when something happens",
                    RowInline("New lap slides in", null, Switch(s.AnimateNewLap, on => { s.AnimateNewLap = on; C(); })),
                    RowInline("Flash new lap", "Purple for session best, green for PB", Switch(s.FlashNewLap, on => { s.FlashNewLap = on; C(); })),
                    RowInline("Glow when a best improves", "PB and session best rows light up briefly", Switch(s.GlowOnImprove, on => { s.GlowOnImprove = on; C(); }))),
            ]),
        ]);
    }

    private UIElement BuildInputsPage()
    {
        var s = _settings.Inputs;
        var w = _inputs;
        void C() { w.ApplyAppearance(); RequestSave(); }

        return OverlayPage("inputs", s, w, new InputsView(_hub, s), "Show in game",
        [
            ("Layout", () =>
            [
                Card("Size", "Changes apply live",
                    RowStacked("Overall size", MakeSlider(0.5, 2.0, 0.05, s.Scale, Pct, v => { s.Scale = v; C(); })),
                    RowStacked("Height", MakeSlider(40, 140, 2, s.Height, Px, v => { s.Height = v; C(); })),
                    RowStacked("Trace width", MakeSlider(60, 400, 5, s.TraceWidth, Px, v => { s.TraceWidth = v; C(); })),
                    RowStacked("Line thickness", MakeSlider(0.5, 5, 0.1, s.LineThickness, v => $"{v:0.0}", v => { s.LineThickness = v; C(); })),
                    RowStacked("Corner radius", MakeSlider(0, 30, 1, s.CornerRadius, Px, v => { s.CornerRadius = v; C(); }))),
                TransparencyCard(s, C),
                FontCard(s, C),
                PositionCard(w),
            ]),
            ("Content", () =>
            [
                Card("Elements", null,
                    RowInline("Gear", null, Switch(s.ShowGear, on => { s.ShowGear = on; C(); })),
                    RowInline("Speed", null, Switch(s.ShowSpeed, on => { s.ShowSpeed = on; C(); })),
                    RowInline("Pedal bars", "Throttle and brake bars", Switch(s.ShowBars, on => { s.ShowBars = on; C(); })),
                    RowInline("Bar percentages", "Numbers above the bars", Switch(s.ShowBarValues, on => { s.ShowBarValues = on; C(); }))),
                Card("Trace", null,
                    RowInline("Show trace", "Scrolling throttle / brake graph", Switch(s.ShowTrace, on => { s.ShowTrace = on; C(); })),
                    RowStacked("Trace length", MakeSlider(2, 10, 0.5, s.TraceSeconds, v => $"{v:0.#} s", v => { s.TraceSeconds = v; C(); })),
                    RowInline("Fill under lines", null, Switch(s.FillTrace, on => { s.FillTrace = on; C(); })),
                    RowInline("Grid lines", "25 / 50 / 75 % guides", Switch(s.GridLines, on => { s.GridLines = on; C(); }))),
            ]),
            ("Colours", () =>
            [
                Card("Panel", null,
                    RowStacked("Background", Colour(s.BackgroundColor, c => { s.BackgroundColor = c; C(); })),
                    RowStacked("Trace background", Colour(s.TraceBackgroundColor, c => { s.TraceBackgroundColor = c; C(); }))),
                Card("Elements", null,
                    RowStacked("Gear", Colour(s.GearColor, c => { s.GearColor = c; C(); })),
                    RowStacked("Speed", Colour(s.SpeedColor, c => { s.SpeedColor = c; C(); })),
                    RowStacked("Throttle", Colour(s.ThrottleColor, c => { s.ThrottleColor = c; C(); })),
                    RowStacked("Brake", Colour(s.BrakeColor, c => { s.BrakeColor = c; C(); }))),
            ]),
            ("Effects", () =>
            [
                Card("Motion", null,
                    RowInline("Smooth pedal bars", "Off shows the raw value every frame", Switch(s.SmoothBars, on => { s.SmoothBars = on; C(); }))),
            ]),
        ]);
    }

    // ───────────────────────────── themes & general ─────────────────────────────

    private UIElement BuildThemesPage()
    {
        var page = new StackPanel();
        var grid = new WrapPanel();

        foreach (var theme in OverlayTheme.All)
        {
            var bg = new SolidColorBrush(Theme.Card);
            var scale = new ScaleTransform(1, 1);
            var sp = new StackPanel();

            // Mini mock of the lap log in the theme's colours
            var mock = new Border
            {
                Height = 86, CornerRadius = new CornerRadius(Math.Min(theme.Radius, 10)), Background = Theme.B(theme.Background),
                ClipToBounds = true, Margin = new Thickness(0, 0, 0, 12),
            };
            var mockStack = new StackPanel();
            mockStack.Children.Add(new Border { Height = 3, Background = Theme.B(theme.Accent) });
            mockStack.Children.Add(new Border { Height = 14, Background = Theme.B(theme.TitleBar) });
            mockStack.Children.Add(new Border { Height = 18, Background = Theme.B(theme.SessionBest) });
            for (int i = 0; i < 3; i++)
            {
                var r = new Grid { Height = 17, Background = Theme.B(i % 2 == 0 ? theme.Stripe : theme.Background) };
                r.Children.Add(new Border { Width = 26, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.B(theme.Text), Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0) });
                r.Children.Add(new Border { Width = 22, Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.B(i == 0 ? theme.Throttle : theme.Brake), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) });
                mockStack.Children.Add(r);
            }
            mock.Child = mockStack;
            sp.Children.Add(mock);

            sp.Children.Add(new TextBlock { Text = theme.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Theme.B(Theme.TextColor) });
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var hex in new[] { theme.Accent, theme.Header, theme.Gear, theme.Throttle, theme.Brake })
                dots.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Theme.B(hex), Margin = new Thickness(0, 0, 5, 0) });
            dots.Children.Add(new TextBlock { Text = theme.Font, FontSize = 11, Foreground = Theme.B(Theme.Muted), Margin = new Thickness(4, -2, 0, 0) });
            sp.Children.Add(dots);

            var card = new Border
            {
                Width = 196, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(14), CornerRadius = new CornerRadius(14),
                Background = bg, BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, Child = sp,
            };
            card.MouseEnter += (_, _) => { Anim.ColorTo(bg, Theme.CardHover, 150); Anim.To(scale, ScaleTransform.ScaleXProperty, 1.03, 150); Anim.To(scale, ScaleTransform.ScaleYProperty, 1.03, 150); };
            card.MouseLeave += (_, _) => { Anim.ColorTo(bg, Theme.Card, 200); Anim.To(scale, ScaleTransform.ScaleXProperty, 1, 200); Anim.To(scale, ScaleTransform.ScaleYProperty, 1, 200); };
            card.MouseLeftButtonUp += (_, _) =>
            {
                theme.ApplyTo(_settings);
                _lapLog.ApplyAppearance();
                _inputs.ApplyAppearance();
                RequestSave();
                ShowToast($"{theme.Name} applied");
            };
            grid.Children.Add(card);
        }

        page.Children.Add(Card("Pick a theme", "Sets colours, font and corner radius on both overlays. You can still fine-tune everything afterwards.", grid));
        return page;
    }

    private UIElement BuildGeneralPage()
    {
        var page = new StackPanel();

        if (_startupError != null)
            page.Children.Add(Card("Connection problem", _startupError));

        page.Children.Add(Card("Overlays", null,
            RowInline("Lock overlays", "Clicks go through to the game. Unlock to drag overlays (they get a dashed outline).",
                Switch(_settings.Locked, on => SetLocked(on)))));

        var dataButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var folder = new PillButton("Open lap folder");
        folder.Click += () =>
        {
            Directory.CreateDirectory(Settings.LapLogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", Settings.LapLogDir) { UseShellExecute = true });
        };
        var demo = new PillButton(_demoRunning ? "Demo running" : "Run demo data", primary: !_demoRunning);
        demo.Click += () => { StartDemo(); ((TextBlock)demo.Child).Text = "Demo running"; };
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
            _subTab["laplog"] = _subTab["inputs"] = 0;
            RequestSave();
            ShowToast("Everything reset to default");
        };
        page.Children.Add(Card("Reset", "Puts colours, sizes and positions of both overlays back to default.", reset));
        return page;
    }

    // ───────────────────────────── building blocks ─────────────────────────────

    private static string Pct(double v) => $"{v * 100:0}%";
    private static string Px(double v) => $"{v:0} px";

    private void SetLocked(bool on)
    {
        _settings.Locked = on;
        _lapLog.SetLocked(on);
        _inputs.SetLocked(on);
        RequestSave();
    }

    private void StartDemo()
    {
        if (_demoRunning) return;
        _demoRunning = true;
        Task.Run(() => DemoSender.Run(_port, _ct));
        ShowToast("Demo data running");
    }

    private Border TransparencyCard(OverlaySettings s, Action changed) =>
        Card("Transparency", null,
            RowStacked("Overall opacity", MakeSlider(0.2, 1.0, 0.05, s.Opacity, Pct, v => { s.Opacity = v; changed(); })),
            RowStacked("Background opacity", MakeSlider(0, 1.0, 0.05, s.BackgroundOpacity, Pct, v => { s.BackgroundOpacity = v; changed(); })));

    private static Border FontCard(OverlaySettings s, Action changed)
    {
        var chips = new ChoiceChips(FontChoices, s.FontFamily, previewFont: true);
        chips.Changed += f => { s.FontFamily = f; changed(); };
        return Card("Font", "Bahnschrift gives a condensed, racing-style look", chips);
    }

    private static TextBox TitleBox(LapLogSettings s, Action changed)
    {
        var box = Ui.TextField(s.TitleText, 170);
        box.TextChanged += (_, _) => { s.TitleText = box.Text; changed(); };
        return box;
    }

    private Border PositionCard(OverlayWindow w)
    {
        var reset = new PillButton("Reset position");
        reset.Click += w.ResetPosition;
        var unlock = new PillButton(_settings.Locked ? "Unlock to move" : "Lock in place", primary: _settings.Locked);
        unlock.Click += () =>
        {
            SetLocked(!_settings.Locked);
            ((TextBlock)unlock.Child).Text = _settings.Locked ? "Unlock to move" : "Lock in place";
            ShowToast(_settings.Locked ? "Overlays locked" : "Overlays unlocked: drag them into place");
        };
        var row = new WrapPanel();
        row.Children.Add(unlock);
        row.Children.Add(reset);
        return Card("Position", "Unlock, drag the overlay with your mouse, then lock it again.", row);
    }

    private static Border CardRaw(UIElement child)
    {
        var bg = new SolidColorBrush(Theme.Card);
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Background = bg, BorderBrush = Theme.B(Theme.Line), BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, 14), Child = child,
        };
        card.MouseEnter += (_, _) => Anim.ColorTo(bg, Theme.CardHover, 200);
        card.MouseLeave += (_, _) => Anim.ColorTo(bg, Theme.Card, 300);
        return card;
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
        return CardRaw(sp);
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

    private static ColorField Colour(string selected, Action<string> changed)
    {
        var f = new ColorField(selected);
        f.ColorChanged += changed;
        return f;
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
        if (WindowState != WindowState.Minimized)
            foreach (var p in _previews) { p.InvalidateMeasure(); p.InvalidateVisual(); }
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
        fade.Completed += (_, _) => Dispatcher.BeginInvoke(new Action(Close));
        _root.BeginAnimation(OpacityProperty, fade);
        Anim.To(_rootScale, ScaleTransform.ScaleXProperty, 0.96, 180);
        Anim.To(_rootScale, ScaleTransform.ScaleYProperty, 0.96, 180);
    }
}
