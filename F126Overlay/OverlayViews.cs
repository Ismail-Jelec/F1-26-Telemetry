using System.Globalization;
using System.Windows;
using System.Windows.Media;
using F126Telemetry;

namespace F126Overlay;

/// <summary>Base for overlays drawn directly with a DrawingContext every frame.</summary>
public abstract class OverlayView(TelemetryHub hub, OverlaySettings settings) : FrameworkElement
{
    protected readonly TelemetryHub Hub = hub;
    private readonly OverlaySettings _base = settings;

    /// <summary>When true the overlay shows a pulsing outline so you can see what to drag.</summary>
    public bool Unlocked { get; set; }

    private static readonly Dictionary<string, (Typeface Regular, Typeface Heavy)> Fonts = [];

    protected static double Now => Environment.TickCount64 / 1000.0;

    protected abstract Size ContentSize { get; }
    protected override Size MeasureOverride(Size availableSize) => ContentSize;

    private (Typeface Regular, Typeface Heavy) Faces
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(_base.FontFamily) ? "Segoe UI" : _base.FontFamily;
            if (!Fonts.TryGetValue(name, out var f))
            {
                var family = new FontFamily(name + ", Segoe UI");
                f = (new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                     new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                Fonts[name] = f;
            }
            return f;
        }
    }

    protected FormattedText Text(string s, double size, Brush brush, bool heavy = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, heavy ? Faces.Heavy : Faces.Regular,
            Math.Max(1, size), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Draws text with x as left, right or centre anchor and y as the vertical centre.</summary>
    protected Rect DrawText(DrawingContext dc, string s, double size, Brush brush, double x, double yCenter,
        TextAlignment align = TextAlignment.Left, bool heavy = false)
    {
        var ft = Text(s, size, brush, heavy);
        double left = align switch
        {
            TextAlignment.Right => x - ft.Width,
            TextAlignment.Center => x - ft.Width / 2,
            _ => x,
        };
        var origin = new Point(Math.Round(left), Math.Round(yCenter - ft.Height / 2));
        dc.DrawText(ft, origin);
        return new Rect(origin, new Size(ft.Width, ft.Height));
    }

    /// <summary>Background for the whole overlay, clipped to rounded corners. Call Pop() at the end.</summary>
    protected void BeginPanel(DrawingContext dc, Size size)
    {
        double r = Math.Clamp(_base.CornerRadius, 0, Math.Min(size.Width, size.Height) / 2);
        var rect = new Rect(size);
        dc.PushClip(new RectangleGeometry(rect, r, r));
        dc.DrawRectangle(Theme.Brush(_base.BackgroundColor, _base.BackgroundOpacity), null, rect);
    }

    protected void EndPanel(DrawingContext dc, Size size)
    {
        dc.Pop();
        if (!Unlocked) return;
        double r = Math.Clamp(_base.CornerRadius, 0, Math.Min(size.Width, size.Height) / 2);
        double a = 0.55 + 0.35 * Math.Sin(Now * 4);
        var pen = new Pen(Theme.Brush("#FFFFFF", a), 1.5) { DashStyle = DashStyles.Dash };
        dc.DrawRoundedRectangle(null, pen, new Rect(0.75, 0.75, size.Width - 1.5, size.Height - 1.5), r, r);
    }

    protected static string LapTime(uint ms)
    {
        if (ms == 0) return "--:--.---";
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}

/// <summary>"LAPTIME LOG": personal best, session best and the latest laps with delta to PB and fuel.</summary>
public sealed class LapLogView(TelemetryHub hub, LapLogSettings s) : OverlayView(hub, s)
{
    private int _seenLaps;
    private double _newLapAt = double.MinValue;
    private string _flashHex = "#FFFFFF";
    private uint _seenPb, _seenSb;
    private double _pbFlashAt = double.MinValue, _sbFlashAt = double.MinValue;

    private double W => Math.Round(Math.Clamp(s.Width, 200, 420));
    private double RowH => Math.Round(Math.Clamp(s.RowHeight, 18, 40));
    private double BestH => RowH * 2;
    private double TitleH => Math.Round(RowH * 0.92);
    private double Fs => Math.Clamp(s.TextScale, 0.6, 1.8) * RowH / 26;
    private int RowCount => Math.Clamp(s.Rows, 1, 30);

    protected override Size ContentSize => new(W,
        (s.ShowAccentStripe ? 3 : 0) + (s.ShowTitle ? TitleH : 0) + (s.ShowPersonalBest ? BestH : 0) +
        (s.ShowSessionBest ? BestH : 0) + (s.ShowColumnHeaders ? RowH : 0) + RowCount * RowH);

    protected override void OnRender(DrawingContext dc)
    {
        uint pb, sb;
        LapRecord[] newestFirst;
        int rows = RowCount;
        lock (Hub.Sync)
        {
            pb = Hub.PersonalBestMs;
            sb = Hub.SessionBestMs;
            newestFirst = Hub.PlayerLaps.AsEnumerable().Reverse().Take(rows + 1).ToArray();
            DetectChanges(Hub.PlayerLaps.Count, newestFirst, pb, sb);
        }

        var size = ContentSize;
        double w = W, rowH = RowH, fs = Fs;
        var text = Theme.Brush(s.TextColor);
        var grey = Theme.Brush(s.TextColor, 0.5);
        var header = Theme.Brush(s.HeaderColor);
        var bgAlpha = s.BackgroundOpacity;

        BeginPanel(dc, size);
        double y = 0;

        if (s.ShowAccentStripe)
        {
            dc.DrawRectangle(Theme.Brush(s.AccentColor), null, new Rect(0, 0, w, 3));
            y += 3;
        }
        if (s.ShowTitle)
        {
            dc.DrawRectangle(Theme.Brush(s.TitleBarColor, Math.Min(1, bgAlpha + 0.05)), null, new Rect(0, y, w, TitleH));
            DrawText(dc, s.TitleText ?? "", 11 * fs, text, w / 2, y + TitleH / 2, TextAlignment.Center, heavy: true);
            y += TitleH;
        }
        if (s.ShowPersonalBest)
        {
            DrawBestRow(dc, y, Theme.Brush(s.PersonalBestColor, bgAlpha), "PERSONAL\nSESSION BEST", pb, _pbFlashAt);
            y += BestH;
        }
        if (s.ShowSessionBest)
        {
            DrawBestRow(dc, y, Theme.Brush(s.SessionBestColor, Math.Max(0.35, bgAlpha)), "SESSION\nBEST", sb, _sbFlashAt);
            y += BestH;
        }

        double timeX = Math.Round(w * 0.177), deltaX = Math.Round(w * 0.54);
        if (s.ShowColumnHeaders)
        {
            dc.DrawRectangle(Theme.Brush(s.TitleBarColor, Math.Min(1, bgAlpha + 0.05)), null, new Rect(0, y, w, rowH));
            double cy = y + rowH / 2;
            DrawText(dc, "LAP", 11 * fs, header, 10, cy, heavy: true);
            DrawText(dc, "TIME", 11 * fs, header, timeX, cy, heavy: true);
            if (s.ShowDelta)
            {
                if (s.ShowFuel) DrawText(dc, "PS. BEST", 11 * fs, header, deltaX, cy, heavy: true);
                else DrawText(dc, "PS. BEST", 11 * fs, header, w - 10, cy, TextAlignment.Right, heavy: true);
            }
            if (s.ShowFuel) DrawText(dc, "FUEL", 11 * fs, header, w - 10, cy, TextAlignment.Right, heavy: true);
            y += rowH;
        }

        // Lap rows
        double t = Now - _newLapAt;
        double e = s.AnimateNewLap ? Theme.EaseOutCubic(t / 0.45) : 1;
        double slide = Math.Round((1 - e) * rowH);
        double areaBottom = y + rows * rowH;

        // Work out which laps go where.
        var placed = new List<(LapRecord? Lap, double Y, bool Newest)>();
        if (s.NewestOnTop)
        {
            for (int i = 0; i <= rows; i++)
                placed.Add((i < newestFirst.Length ? newestFirst[i] : null, y + i * rowH - slide, i == 0));
        }
        else
        {
            var oldestFirst = Enumerable.Reverse(newestFirst).ToArray();
            int m = oldestFirst.Length;
            if (m <= rows)
            {
                for (int j = 0; j < rows; j++)
                {
                    bool newest = j == m - 1;
                    placed.Add((j < m ? oldestFirst[j] : null, y + j * rowH + (newest ? slide : 0), newest));
                }
            }
            else
            {
                for (int j = 0; j < m; j++)
                    placed.Add((oldestFirst[j], y + (j - (m - rows)) * rowH + slide, j == m - 1));
            }
        }

        dc.PushClip(new RectangleGeometry(new Rect(0, y, w, rows * rowH)));
        int topLap = newestFirst.Length > 0 ? newestFirst[0].Lap : 0;
        for (int i = 0; i < placed.Count; i++)
        {
            var (lap, ry, newest) = placed[i];
            if (ry >= areaBottom || ry + rowH <= y) continue;

            // Stripes follow the lap number so they don't flicker while rows slide.
            int parity = lap != null ? lap.Lap : (s.NewestOnTop ? topLap - i : i);
            if (s.Stripes && (parity & 1) == 1)
                dc.DrawRectangle(Theme.Brush(s.StripeColor, bgAlpha), null, new Rect(0, ry, w, rowH));
            if (lap == null) continue;

            if (newest && s.FlashNewLap && t < 1.6)
                dc.DrawRectangle(Theme.Brush(_flashHex, 0.5 * (1 - t / 1.6)), null, new Rect(0, ry, w, rowH));

            double cy = ry + rowH / 2;
            DrawText(dc, lap.Lap.ToString(CultureInfo.InvariantCulture), 12 * fs, text, 10, cy, heavy: true);
            var timeRect = DrawText(dc, LapTime(lap.TimeMs), 12 * fs, lap.Valid ? text : grey, timeX, cy);
            if (!lap.Valid)
            {
                // Strike through: the lap still shows its time and delta, but it didn't count.
                var strike = new Pen(Theme.Brush(s.SlowerColor, 0.9), Math.Max(1.2, 1.4 * fs));
                double sy = Math.Round(cy) + 0.5;
                dc.DrawLine(strike, new Point(timeRect.Left - 2, sy), new Point(timeRect.Right + 2, sy));
            }

            if (s.ShowDelta)
            {
                var (delta, hex) = Delta(lap, pb, sb);
                var brush = hex == null ? grey : Theme.Brush(hex);
                if (s.ShowFuel) DrawText(dc, delta, 12 * fs, brush, deltaX, cy, heavy: true);
                else DrawText(dc, delta, 12 * fs, brush, w - 10, cy, TextAlignment.Right, heavy: true);
            }
            if (s.ShowFuel)
                DrawText(dc, lap.FuelKg.ToString("0.0", CultureInfo.InvariantCulture) + " kg", 12 * fs, text, w - 10, cy, TextAlignment.Right);
        }
        dc.Pop();

        EndPanel(dc, size);
    }

    private (string Text, string? Hex) Delta(LapRecord lap, uint pb, uint sb)
    {
        if (pb == 0) return ("-", null);
        if (lap.TimeMs == sb) return ("SB", s.SessionBestColor);
        if (lap.TimeMs == pb) return ("PB", s.FasterColor);
        double d = ((double)lap.TimeMs - pb) / 1000.0;
        return (d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture), d > 0 ? s.SlowerColor : s.FasterColor);
    }

    private void DetectChanges(int count, LapRecord[] newestFirst, uint pb, uint sb)
    {
        if (count > _seenLaps && newestFirst.Length > 0)
        {
            var lap = newestFirst[0];
            _newLapAt = Now;
            _flashHex = !lap.Valid ? "#8A8A94" : lap.TimeMs == sb ? s.SessionBestColor : lap.TimeMs == pb ? s.FasterColor : "#FFFFFF";
        }
        _seenLaps = count;

        if (pb != _seenPb) { if (pb != 0) _pbFlashAt = Now; _seenPb = pb; }
        if (sb != _seenSb) { if (sb != 0) _sbFlashAt = Now; _seenSb = sb; }
    }

    private void DrawBestRow(DrawingContext dc, double y, Brush bg, string label, uint ms, double flashAt)
    {
        double w = W, h = BestH, fs = Fs;
        dc.DrawRectangle(bg, null, new Rect(0, y, w, h));
        double t = Now - flashAt;
        if (s.GlowOnImprove && t < 1.2)
            dc.DrawRectangle(Theme.Brush("#FFFFFF", 0.3 * (1 - t / 1.2)), null, new Rect(0, y, w, h));

        var ft = Text(label, 12 * fs, Theme.Brush(s.TextColor), heavy: true);
        dc.DrawText(ft, new Point(10, Math.Round(y + h / 2 - ft.Height / 2)));
        double boxX = Math.Round(w * 0.446);
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.3), null, new Rect(boxX, y + h * 0.17, w - boxX - 8, h * 0.66), 5, 5);
        DrawText(dc, LapTime(ms), 21 * fs, Theme.Brush(s.TextColor), w - 14, y + h / 2, TextAlignment.Right, heavy: true);
    }
}

/// <summary>Gear, speed, a scrolling throttle/brake trace and pedal bars.</summary>
public sealed class InputsView(TelemetryHub hub, InputsSettings s) : OverlayView(hub, s)
{
    private const double Pad = 7, BarW = 13, BarGap = 5;

    private readonly float[] _thr = new float[TelemetryHub.TraceLength];
    private readonly float[] _brk = new float[TelemetryHub.TraceLength];
    private double _thrSmooth, _brkSmooth;

    private double H => Math.Round(Math.Clamp(s.Height, 40, 140));
    private double GearW => s.ShowGear || s.ShowSpeed ? Math.Round(H * 0.85) : 0;
    private double TraceW => s.ShowTrace ? Math.Round(Math.Clamp(s.TraceWidth, 60, 400)) : 0;
    private double BarsW => s.ShowBars ? BarW * 2 + BarGap + Pad * 2 : 0;

    protected override Size ContentSize
    {
        get
        {
            double w = GearW + TraceW + BarsW + (s.ShowTrace ? Pad : 0);
            return new Size(Math.Max(w, 40), H);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        sbyte gear = 0; ushort speed = 0; float throttle = 0, brake = 0; int n;
        lock (Hub.Sync)
        {
            if (Hub.Telemetry is { } t) { gear = t.Gear; speed = t.SpeedKph; throttle = t.Throttle; brake = t.Brake; }
            n = Hub.CopyTrace(_thr, _brk);
        }

        if (s.SmoothBars)
        {
            _thrSmooth += (Clamp01(throttle) - _thrSmooth) * 0.4;
            _brkSmooth += (Clamp01(brake) - _brkSmooth) * 0.4;
        }
        else { _thrSmooth = Clamp01(throttle); _brkSmooth = Clamp01(brake); }

        var size = ContentSize;
        double h = H, x = 0;
        BeginPanel(dc, size);

        // Gear + speed
        if (GearW > 0)
        {
            double cx = GearW / 2 + 2;
            if (s.ShowGear)
            {
                double gy = s.ShowSpeed ? h * 0.41 : h / 2;
                DrawText(dc, Names.Gear(gear), h * 0.62, Theme.Brush(s.GearColor), cx, gy, TextAlignment.Center, heavy: true);
            }
            if (s.ShowSpeed)
            {
                double sy = s.ShowGear ? h * 0.83 : h / 2;
                double sz = s.ShowGear ? h * 0.19 : h * 0.4;
                DrawText(dc, speed.ToString(CultureInfo.InvariantCulture), sz, Theme.Brush(s.SpeedColor, 0.9), cx, sy, TextAlignment.Center, heavy: true);
            }
            x += GearW;
        }

        // Trace
        if (s.ShowTrace)
        {
            if (GearW == 0) x += Pad;
            var area = new Rect(x, Pad, TraceW, h - Pad * 2);
            var frame = new Pen(Theme.Brush("#FFFFFF", 0.14), 1);
            dc.DrawRoundedRectangle(Theme.Brush(s.TraceBackgroundColor, 0.35 * s.BackgroundOpacity + 0.1), frame, area, 4, 4);

            if (s.GridLines)
            {
                var gridPen = new Pen(Theme.Brush("#FFFFFF", 0.08), 1);
                for (int g = 1; g <= 3; g++)
                {
                    double gy = Math.Round(area.Top + area.Height * g / 4) + 0.5;
                    dc.DrawLine(gridPen, new Point(area.Left, gy), new Point(area.Right, gy));
                }
            }

            int want = Math.Clamp((int)Math.Round(s.TraceSeconds * 60), 30, TelemetryHub.TraceLength);
            int count = Math.Min(n, want);
            int offset = n - count;
            if (count > 1)
            {
                double thick = Math.Clamp(s.LineThickness, 0.5, 5);
                dc.PushClip(new RectangleGeometry(area, 4, 4));
                if (s.FillTrace) dc.DrawGeometry(Theme.Brush(s.ThrottleColor, 0.18), null, Trace(_thr, offset, count, want, area, closed: true));
                dc.DrawGeometry(null, new Pen(Theme.Brush(s.ThrottleColor), thick) { LineJoin = PenLineJoin.Round }, Trace(_thr, offset, count, want, area, false));
                if (s.FillTrace) dc.DrawGeometry(Theme.Brush(s.BrakeColor, 0.18), null, Trace(_brk, offset, count, want, area, closed: true));
                dc.DrawGeometry(null, new Pen(Theme.Brush(s.BrakeColor), thick) { LineJoin = PenLineJoin.Round }, Trace(_brk, offset, count, want, area, false));
                dc.Pop();
            }
            x += TraceW;
        }

        if (s.ShowBars)
        {
            double bx = x + Pad;
            DrawBar(dc, bx, _thrSmooth, s.ThrottleColor);
            DrawBar(dc, bx + BarW + BarGap, _brkSmooth, s.BrakeColor);
        }

        EndPanel(dc, size);
    }

    private static double Clamp01(float v) => Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1);

    private void DrawBar(DrawingContext dc, double x, double value, string hex)
    {
        double h = H;
        double top = s.ShowBarValues ? Math.Round(h * 0.28) : Pad;
        var frame = new Rect(x, top, BarW, h - top - Pad);
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.4), new Pen(Theme.Brush("#FFFFFF", 0.14), 1), frame, 3, 3);
        double fill = frame.Height * value;
        if (fill > 0.5) dc.DrawRoundedRectangle(Theme.Brush(hex), null, new Rect(x, frame.Bottom - fill, BarW, fill), 3, 3);
        if (s.ShowBarValues)
            DrawText(dc, ((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture), Math.Max(8, h * 0.14),
                Theme.Brush("#FFFFFF"), x + BarW / 2, top / 2 + 1, TextAlignment.Center, heavy: true);
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

/// <summary>Track limits counter: corner-cutting warnings vs. your limit, invalid laps, warnings and penalties.</summary>
public sealed class TrackLimitsView(TelemetryHub hub, TrackLimitsSettings s) : OverlayView(hub, s)
{
    private int _seenWarnings = -1;
    private double _flashAt = double.MinValue;

    private double W => Math.Round(Math.Clamp(s.Width, 150, 360));
    private double Fs => Math.Clamp(s.TextScale, 0.6, 1.8);
    private double TitleH => Math.Round(24 * Fs);
    private double MainH => Math.Round((s.ShowPips ? 70 : 58) * Fs);
    private double StatH => Math.Round(24 * Fs);
    private int StatCount => (s.ShowInvalidLaps ? 1 : 0) + (s.ShowTotalWarnings ? 1 : 0) + (s.ShowPenalties ? 1 : 0);

    protected override Size ContentSize => new(W,
        (s.ShowAccentStripe ? 3 : 0) + (s.ShowTitle ? TitleH : 0) + MainH + (StatCount > 0 ? StatCount * StatH + 6 : 0));

    protected override void OnRender(DrawingContext dc)
    {
        int warnings = 0, total = 0, penalties = 0, invalid = 0;
        lock (Hub.Sync)
        {
            if (Hub.Lap is { } lap)
            {
                warnings = lap.CornerCuttingWarnings;
                total = lap.TotalWarnings;
                penalties = lap.Penalties;
                if (lap.CurrentLapInvalid) invalid++;
            }
            foreach (var l in Hub.PlayerLaps) if (!l.Valid) invalid++;
        }

        if (warnings > _seenWarnings && _seenWarnings >= 0) _flashAt = Now;
        _seenWarnings = warnings;

        // Penalties come every `step` warnings: 3/3 -> shown as 3/6, 6/6 -> 6/9 and so on.
        int step = Math.Clamp(s.WarningLimit, 1, 10);
        int next = (warnings / step + 1) * step;   // warning count of the next penalty
        int inBlock = warnings - (next - step);    // progress towards the next penalty
        int reached = warnings / step;             // penalties earned from warnings so far
        var (stateHex, stateText) =
            warnings == 0 ? (s.OkColor, "CLEAN") :
            inBlock == step - 1 ? (s.DangerColor, "LAST WARNING") :
            inBlock == 0 ? (s.DangerColor, reached == 1 ? "PENALTY" : $"PENALTY x{reached}") :
            (s.WarnColor, "WARNING");

        var size = ContentSize;
        double w = W, fs = Fs, y = 0;
        var text = Theme.Brush(s.TextColor);
        var muted = Theme.Brush(s.TextColor, 0.6);
        var state = Theme.Brush(stateHex);

        BeginPanel(dc, size);

        double t = Now - _flashAt;
        if (s.FlashOnWarning && t < 1.5)
            dc.DrawRectangle(Theme.Brush(stateHex, 0.4 * (1 - t / 1.5)), null, new Rect(size));

        if (s.ShowAccentStripe)
        {
            dc.DrawRectangle(Theme.Brush(s.AccentColor), null, new Rect(0, 0, w, 3));
            y += 3;
        }
        if (s.ShowTitle)
        {
            dc.DrawRectangle(Theme.Brush("#000000", 0.25), null, new Rect(0, y, w, TitleH));
            DrawText(dc, s.TitleText ?? "", 11 * fs, text, 12, y + TitleH / 2, heavy: true);
            y += TitleH;
        }

        // Big counter
        double mainTop = y;
        double numY = mainTop + (s.ShowPips ? MainH * 0.45 : MainH / 2);
        var num = DrawText(dc, warnings.ToString(CultureInfo.InvariantCulture), 40 * fs, state, 12, numY, heavy: true);
        DrawText(dc, $"/ {next}", 16 * fs, muted, num.Right + 6, numY + 6 * fs, heavy: true);

        DrawText(dc, stateText, 11 * fs, state, w - 12, numY - 8 * fs, TextAlignment.Right, heavy: true);
        DrawText(dc, $"{next - warnings} to next penalty", 11 * fs, muted, w - 12, numY + 9 * fs, TextAlignment.Right);

        if (s.ShowPips)
        {
            double pipY = mainTop + MainH * 0.84;
            double gap = 5 * fs;
            double pipW = Math.Min(28 * fs, (w - 24 - gap * (step - 1)) / step);
            for (int i = 0; i < step; i++)
            {
                var r = new Rect(12 + i * (pipW + gap), pipY - 3 * fs, pipW, 6 * fs);
                dc.DrawRoundedRectangle(i < inBlock ? state : Theme.Brush("#FFFFFF", 0.12), null, r, 3 * fs, 3 * fs);
            }
        }
        y += MainH;

        // Stats
        if (StatCount > 0)
        {
            y += 3;
            void Stat(string label, string value, Brush valueBrush)
            {
                DrawText(dc, label, 12 * fs, muted, 12, y + StatH / 2);
                DrawText(dc, value, 12 * fs, valueBrush, w - 12, y + StatH / 2, TextAlignment.Right, heavy: true);
                y += StatH;
            }
            if (s.ShowInvalidLaps) Stat("Invalid laps", invalid.ToString(CultureInfo.InvariantCulture), invalid > 0 ? Theme.Brush(s.WarnColor) : text);
            if (s.ShowTotalWarnings) Stat("Total warnings", total.ToString(CultureInfo.InvariantCulture), total > 0 ? Theme.Brush(s.WarnColor) : text);
            if (s.ShowPenalties) Stat("Penalties", penalties > 0 ? $"+{penalties}s" : "none", penalties > 0 ? Theme.Brush(s.DangerColor) : text);
        }

        EndPanel(dc, size);
    }
}
