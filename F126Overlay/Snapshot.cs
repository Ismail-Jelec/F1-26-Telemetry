using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using F126Telemetry;

namespace F126Overlay;

/// <summary>
/// Renders every overlay with demo data to PNG files (run with --snapshot DIR).
/// Used by the Windows build check so changes to the look can be reviewed as images.
/// </summary>
public static class Snapshot
{
    private const double Scale = 2; // render at 2x so details are visible

    public static void Run(string dir)
    {
        _ = new Application(); // pack:// URIs (bundled font) need an Application
        Directory.CreateDirectory(dir);

        var hub = new TelemetryHub(null);
        // ~5.5 minutes: several laps (one invalid), warnings, worn tyres and the rain arriving.
        DemoSender.Simulate(data => hub.Handle(data), 330);

        var monitor = new FpsMonitor();
        var rng = new Random(7);

        var looks = new List<(string Name, Settings Settings)> { ("default", new Settings()) };
        foreach (var theme in OverlayTheme.All)
        {
            var s = new Settings();
            theme.ApplyTo(s);
            looks.Add((theme.Name.Replace(' ', '-').ToLowerInvariant(), s));
        }

        // Extra variants to review the outline and combined-tile options.
        var dash = OverlayTheme.All.First(t => t.Name == "Dash");
        var combined = new Settings();
        dash.ApplyTo(combined);
        foreach (var o in new OverlaySettings[] { combined.LapLog, combined.Inputs, combined.TrackLimits, combined.Tyres, combined.Fps })
        {
            o.CombineTiles = true;
            o.PanelOutline = true;
            o.TextOutline = true;
        }
        looks.Add(("dash-combined-outlined", combined));
        var flatOutlined = new Settings();
        foreach (var o in new OverlaySettings[] { flatOutlined.LapLog, flatOutlined.Inputs, flatOutlined.TrackLimits, flatOutlined.Tyres, flatOutlined.Fps })
        {
            o.PanelOutline = true;
            o.TextOutline = true;
            o.BackgroundOpacity = 0.35;
        }
        looks.Add(("flat-outlined-see-through", flatOutlined));

        foreach (var (name, s) in looks)
        {
            // Fresh fake frames right before rendering (the counter only shows the last couple of seconds).
            monitor.AddFakeFrames("F1_25.exe", Enumerable.Range(0, 1400).Select(i => 6.9 + rng.NextDouble() * 0.8 + (i % 211 == 0 ? 9 : 0)));
            s.Fps.Enabled = true;
            s.Fps.TargetApp = "F1_25.exe";
            var views = new (string Title, FrameworkElement View)[]
            {
                ("Lap time log", new LapLogView(hub, s.LapLog)),
                ("Track limits", new TrackLimitsView(hub, s.TrackLimits)),
                ("Tyre strategy", new TyreStrategyView(hub, s.Tyres)),
                ("Inputs", new InputsView(hub, s.Inputs)),
                ("FPS", new FpsView(hub, s.Fps, monitor)),
            };
            Save(Compose(views, name), Path.Combine(dir, $"overlays-{name}.png"));
        }
    }

    private static BitmapSource Render(FrameworkElement view)
    {
        view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        view.Arrange(new Rect(view.DesiredSize));
        view.UpdateLayout();
        var size = view.DesiredSize;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(view);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Lays the overlays out on a dark, game-like background with labels.</summary>
    private static BitmapSource Compose((string Title, FrameworkElement View)[] views, string themeName)
    {
        var shots = views.Select(v => (v.Title, Bitmap: Render(v.View))).ToArray();
        const double margin = 30, gap = 30;
        // Column 1: lap log; column 2: track limits + tyre strategy; column 3: inputs + FPS.
        var columns = new[] { new[] { 0 }, new[] { 1, 2 }, new[] { 3, 4 } };
        double Width(int i) => shots[i].Bitmap.Width / Scale;
        double Height(int i) => shots[i].Bitmap.Height / Scale;
        double totalW = margin * 2 + columns.Sum(c => c.Max(Width)) + gap * (columns.Length - 1);
        double totalH = margin * 2 + 30 + columns.Max(c => c.Sum(i => Height(i) + 24) + gap * (c.Length - 1));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x3A, 0x45, 0x52), Color.FromRgb(0x12, 0x15, 0x1A), 70);
            dc.DrawRectangle(bg, null, new Rect(0, 0, totalW, totalH));
            var tf = new Typeface("Segoe UI");
            void Label(string text, double x, double y, double size, Brush b) =>
                dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, 1), new Point(x, y));
            Label($"Theme: {themeName}", margin, 8, 14, Brushes.White);

            double x = margin;
            foreach (var col in columns)
            {
                double y = margin + 30;
                foreach (int i in col)
                {
                    Label(shots[i].Title, x, y, 11, new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)));
                    y += 18;
                    dc.DrawImage(shots[i].Bitmap, new Rect(x, y, Width(i), Height(i)));
                    y += Height(i) + 6 + gap;
                }
                x += col.Max(Width) + gap;
            }
        }
        var output = new RenderTargetBitmap((int)Math.Ceiling(totalW * Scale), (int)Math.Ceiling(totalH * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        output.Render(visual);
        return output;
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
