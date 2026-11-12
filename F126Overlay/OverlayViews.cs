using System.Globalization;
using System.Windows;
using System.Windows.Media;
using F126Telemetry;

namespace F126Overlay;

/// <summary>Base for overlays drawn directly with a DrawingContext every frame.</summary>
public abstract class OverlayView(TelemetryHub hub) : FrameworkElement
{
    protected readonly TelemetryHub Hub = hub;

    /// <summary>When true the overlay shows a pulsing outline so you can see what to drag.</summary>
    public bool Unlocked { get; set; }

    protected static readonly Typeface Regular = new(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    protected static readonly Typeface Heavy = new(Theme.Font, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    protected static double Now => Environment.TickCount64 / 1000.0;

    protected abstract Size ContentSize { get; }
    protected override Size MeasureOverride(Size availableSize) => ContentSize;

    protected FormattedText Text(string s, double size, Brush brush, bool heavy = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, heavy ? Heavy : Regular,
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Draws text with x as left, right or centre anchor and y as the vertical centre.</summary>
    protected void DrawText(DrawingContext dc, string s, double size, Brush brush, double x, double yCenter,
        TextAlignment align = TextAlignment.Left, bool heavy = false)
    {
        var ft = Text(s, size, brush, heavy);
        double left = align switch
        {
            TextAlignment.Right => x - ft.Width,
            TextAlignment.Center => x - ft.Width / 2,
            _ => x,
        };
        dc.DrawText(ft, new Point(left, yCenter - ft.Height / 2));
    }

    protected void DrawUnlockedHint(DrawingContext dc, Size size, double radius)
    {
        if (!Unlocked) return;
        double a = 0.55 + 0.35 * Math.Sin(Now * 4);
        var pen = new Pen(Theme.Brush("#FFFFFF", a), 1.5) { DashStyle = DashStyles.Dash };
        dc.DrawRoundedRectangle(null, pen, new Rect(0.75, 0.75, size.Width - 1.5, size.Height - 1.5), radius, radius);
    }

    protected static string LapTime(uint ms)
    {
        if (ms == 0) return "--:--.---";
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}

/// <summary>"LAPTIME LOG": personal best, session best and the latest laps with delta to PB and fuel.</summary>
public sealed class LapLogView(TelemetryHub hub, LapLogSettings s) : OverlayView(hub)
{
    private const double W = 260, RowH = 26, BestH = 52, HeaderH = 24;

    private int _seenLaps;
    private double _newLapAt = double.MinValue;
    private string _flashHex = "#FFFFFF";
    private uint _seenPb, _seenSb;
    private double _pbFlashAt = double.MinValue, _sbFlashAt = double.MinValue;

    protected override Size ContentSize =>
        new(W, 3 + HeaderH + BestH + (s.ShowSessionBest ? BestH : 0) + RowH + Math.Clamp(s.Rows, 1, 30) * RowH);

    protected override void OnRender(DrawingContext dc)
    {
        uint pb, sb;
        LapRecord[] laps;
        int rows = Math.Clamp(s.Rows, 1, 30);
        lock (Hub.Sync)
        {
            pb = Hub.PersonalBestMs;
            sb = Hub.SessionBestMs;
            laps = Hub.PlayerLaps.AsEnumerable().Reverse().Take(rows + 1).ToArray();
            DetectChanges(Hub.PlayerLaps.Count, laps, pb, sb);
        }

        double bg = s.BackgroundOpacity;
        var panel = Theme.Brush("#141419", bg);
        var panelAlt = Theme.Brush("#1C1C23", bg);
        var panelDark = Theme.Brush("#0B0B0F", Math.Min(1, bg + 0.05));
        var header = Theme.Brush(s.HeaderColor);
        var white = Theme.Brush("#FFFFFF");
        var grey = Theme.Brush("#8A8A94");
        var size = ContentSize;

        // Title bar
        dc.DrawRectangle(Theme.Brush(s.AccentColor), null, new Rect(0, 0, W, 3));
        dc.DrawRectangle(panelDark, null, new Rect(0, 3, W, HeaderH));
        DrawText(dc, "LAPTIME LOG", 11, white, W / 2, 3 + HeaderH / 2, TextAlignment.Center, heavy: true);

        double y = 3 + HeaderH;
        DrawBestRow(dc, y, panel, "PERSONAL\nSESSION BEST", pb, _pbFlashAt);
        y += BestH;
        if (s.ShowSessionBest)
        {
            DrawBestRow(dc, y, Theme.Brush(s.SessionBestColor, Math.Max(0.35, bg)), "SESSION\nBEST", sb, _sbFlashAt);
            y += BestH;
        }

        // Column headers
        dc.DrawRectangle(panelDark, null, new Rect(0, y, W, RowH));
        DrawText(dc, "LAP", 11, header, 10, y + RowH / 2, heavy: true);
        DrawText(dc, "TIME", 11, header, 46, y + RowH / 2, heavy: true);
        if (s.ShowDelta)
        {
            if (s.ShowFuel) DrawText(dc, "PS. BEST", 11, header, 140, y + RowH / 2, heavy: true);
            else DrawText(dc, "PS. BEST", 11, header, W - 10, y + RowH / 2, TextAlignment.Right, heavy: true);
        }
        if (s.ShowFuel) DrawText(dc, "FUEL", 11, header, W - 10, y + RowH / 2, TextAlignment.Right, heavy: true);
        y += RowH;

        // Lap rows: the newest slides in from the top and flashes.
        double t = Now - _newLapAt;
        double slide = (1 - Theme.EaseOutCubic(t / 0.45)) * RowH;
        int topLap = laps.Length > 0 ? laps[0].Lap : 0;

        dc.PushClip(new RectangleGeometry(new Rect(0, y, W, rows * RowH)));
        for (int i = 0; i <= rows; i++)
        {
            double ry = y + i * RowH - slide;
            if (ry > y + rows * RowH) break;
            bool alt = ((topLap - i) & 1) == 1;
            dc.DrawRectangle(alt ? panelAlt : panel, null, new Rect(0, ry, W, RowH));
            if (i >= laps.Length) continue;

            var lap = laps[i];
            if (i == 0 && t < 1.6)
                dc.DrawRectangle(Theme.Brush(_flashHex, 0.5 * (1 - t / 1.6)), null, new Rect(0, ry, W, RowH));

            double cy = ry + RowH / 2;
            DrawText(dc, lap.Lap.ToString(CultureInfo.InvariantCulture), 12, white, 10, cy, heavy: true);
            DrawText(dc, LapTime(lap.TimeMs), 12, lap.Valid ? white : grey, 46, cy);

            if (s.ShowDelta)
            {
                var (delta, hex) = Delta(lap, pb, sb);
                var brush = Theme.Brush(hex);
                if (s.ShowFuel) DrawText(dc, delta, 12, brush, 140, cy, heavy: true);
                else DrawText(dc, delta, 12, brush, W - 10, cy, TextAlignment.Right, heavy: true);
            }
            if (s.ShowFuel)
                DrawText(dc, lap.FuelKg.ToString("0.0", CultureInfo.InvariantCulture) + " kg", 12, white, W - 10, cy, TextAlignment.Right);
        }
        dc.Pop();

        DrawUnlockedHint(dc, size, 0);
    }

    private (string Text, string Hex) Delta(LapRecord lap, uint pb, uint sb)
    {
        if (!lap.Valid) return ("INVALID", "#8A8A94");
        if (pb == 0) return ("-", "#8A8A94");
        if (lap.TimeMs == sb) return ("SB", s.SessionBestColor);
        if (lap.TimeMs == pb) return ("PB", "#2BD34F");
        double d = ((double)lap.TimeMs - pb) / 1000.0;
        return (d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture), d > 0 ? "#FF4A4A" : "#2BD34F");
    }

    private void DetectChanges(int count, LapRecord[] newestFirst, uint pb, uint sb)
    {
        if (count > _seenLaps && newestFirst.Length > 0)
        {
            var lap = newestFirst[0];
            _newLapAt = Now;
            _flashHex = !lap.Valid ? "#8A8A94" : lap.TimeMs == sb ? s.SessionBestColor : lap.TimeMs == pb ? "#2BD34F" : "#FFFFFF";
        }
        _seenLaps = count;

        if (pb != _seenPb) { if (pb != 0) _pbFlashAt = Now; _seenPb = pb; }
        if (sb != _seenSb) { if (sb != 0) _sbFlashAt = Now; _seenSb = sb; }
    }

    private void DrawBestRow(DrawingContext dc, double y, Brush bg, string label, uint ms, double flashAt)
    {
        dc.DrawRectangle(bg, null, new Rect(0, y, W, BestH));
        double t = Now - flashAt;
        if (t < 1.2) dc.DrawRectangle(Theme.Brush("#FFFFFF", 0.35 * (1 - t / 1.2)), null, new Rect(0, y, W, BestH));

        var ft = Text(label, 12, Theme.Brush("#FFFFFF"), heavy: true);
        dc.DrawText(ft, new Point(10, y + BestH / 2 - ft.Height / 2));
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.3), null, new Rect(116, y + 9, W - 124, 34), 5, 5);

        // Time "pops" briefly when it improves.
        double pop = t < 0.35 ? 1 + 0.12 * (1 - Theme.EaseOutCubic(t / 0.35)) : 1;
        double cx = W - 14, cy = y + BestH / 2;
        dc.PushTransform(new ScaleTransform(pop, pop, cx, cy));
        DrawText(dc, LapTime(ms), 21, Theme.Brush("#FFFFFF"), cx, cy, TextAlignment.Right, heavy: true);
        dc.Pop();
    }
}

/// <summary>Gear, speed, a scrolling throttle/brake trace and pedal bars.</summary>
public sealed class InputsView(TelemetryHub hub, InputsSettings s) : OverlayView(hub)
{
    private const double H = 64, GearW = 54, TraceW = 168, BarsW = 46;

    private readonly float[] _thr = new float[TelemetryHub.TraceLength];
    private readonly float[] _brk = new float[TelemetryHub.TraceLength];
    private sbyte _lastGear = 127;
    private double _gearChangedAt = double.MinValue;
    private double _thrSmooth, _brkSmooth;

    private double Width_ => GearW + TraceW + 8 + (s.ShowBars ? BarsW : 0);
    protected override Size ContentSize => new(Width_, H);

    protected override void OnRender(DrawingContext dc)
    {
        sbyte gear = 0; ushort speed = 0; float throttle = 0, brake = 0; int n;
        lock (Hub.Sync)
        {
            if (Hub.Telemetry is { } t) { gear = t.Gear; speed = t.SpeedKph; throttle = t.Throttle; brake = t.Brake; }
            n = Hub.CopyTrace(_thr, _brk);
        }

        if (gear != _lastGear) { if (_lastGear != 127) _gearChangedAt = Now; _lastGear = gear; }
        _thrSmooth += (Clamp01(throttle) - _thrSmooth) * 0.4;
        _brkSmooth += (Clamp01(brake) - _brkSmooth) * 0.4;

        double w = Width_;
        dc.DrawRoundedRectangle(Theme.Brush("#141419", s.BackgroundOpacity), null, new Rect(0, 0, w, H), 8, 8);

        // Gear (pops on change) + speed
        double gt = Now - _gearChangedAt;
        double pop = gt < 0.25 ? 1 + 0.35 * (1 - Theme.EaseOutCubic(gt / 0.25)) : 1;
        double gearY = s.ShowSpeed ? 26 : H / 2;
        dc.PushTransform(new ScaleTransform(pop, pop, GearW / 2 + 2, gearY));
        DrawText(dc, Names.Gear(gear), 40, Theme.Brush(s.GearColor), GearW / 2 + 2, gearY, TextAlignment.Center, heavy: true);
        dc.Pop();
        if (s.ShowSpeed)
            DrawText(dc, speed.ToString(CultureInfo.InvariantCulture), 12, Theme.Brush("#FFFFFF", 0.85), GearW / 2 + 2, 53, TextAlignment.Center, heavy: true);

        // Trace
        var area = new Rect(GearW, 7, TraceW, H - 14);
        var frame = new Pen(Theme.Brush("#FFFFFF", 0.14), 1);
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.35 * s.BackgroundOpacity + 0.1), frame, area, 4, 4);
        int want = Math.Clamp((int)Math.Round(s.TraceSeconds * 60), 30, TelemetryHub.TraceLength);
        int count = Math.Min(n, want);
        int offset = n - count;
        if (count > 1)
        {
            dc.PushClip(new RectangleGeometry(area, 4, 4));
            if (s.FillTrace) dc.DrawGeometry(Theme.Brush(s.ThrottleColor, 0.18), null, Trace(_thr, offset, count, want, area, closed: true));
            dc.DrawGeometry(null, new Pen(Theme.Brush(s.ThrottleColor), 1.7) { LineJoin = PenLineJoin.Round }, Trace(_thr, offset, count, want, area, false));
            if (s.FillTrace) dc.DrawGeometry(Theme.Brush(s.BrakeColor, 0.18), null, Trace(_brk, offset, count, want, area, closed: true));
            dc.DrawGeometry(null, new Pen(Theme.Brush(s.BrakeColor), 1.7) { LineJoin = PenLineJoin.Round }, Trace(_brk, offset, count, want, area, false));
            dc.Pop();
        }

        if (s.ShowBars)
        {
            double bx = GearW + TraceW + 10;
            DrawBar(dc, bx, _thrSmooth, s.ThrottleColor);
            DrawBar(dc, bx + 18, _brkSmooth, s.BrakeColor);
        }

        DrawUnlockedHint(dc, new Size(w, H), 8);
    }

    private static double Clamp01(float v) => Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1);

    private void DrawBar(DrawingContext dc, double x, double value, string hex)
    {
        var frame = new Rect(x, 18, 13, H - 25);
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.4), new Pen(Theme.Brush("#FFFFFF", 0.14), 1), frame, 3, 3);
        double h = frame.Height * value;
        if (h > 0.5) dc.DrawRoundedRectangle(Theme.Brush(hex), null, new Rect(x, frame.Bottom - h, 13, h), 3, 3);
        DrawText(dc, ((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture), 9, Theme.Brush("#FFFFFF"), x + 6.5, 10, TextAlignment.Center, heavy: true);
    }

    private static StreamGeometry Trace(float[] values, int offset, int count, int window, Rect area, bool closed)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double step = area.Width / (window - 1);
            double x0 = area.Right - (count - 1) * step; // newest sample on the right edge
            double Y(int i) => area.Bottom - 2 - Clamp01(values[offset + i]) * (area.Height - 4);

            if (closed)
            {
                ctx.BeginFigure(new Point(x0, area.Bottom), true, true);
                for (int i = 0; i < count; i++) ctx.LineTo(new Point(x0 + i * step, Y(i)), false, false);
                ctx.LineTo(new Point(area.Right, area.Bottom), false, false);
            }
            else
            {
                ctx.BeginFigure(new Point(x0, Y(0)), false, false);
                for (int i = 1; i < count; i++) ctx.LineTo(new Point(x0 + i * step, Y(i)), true, true);
            }
        }
        g.Freeze();
        return g;
    }
}
