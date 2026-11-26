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

    /// <summary>Rounding for boxes inside the panel, following the panel's own corner radius.</summary>
    protected double InnerRadius => Math.Clamp(_base.CornerRadius * 0.5, 0, 10);

    protected abstract Size ContentSize { get; }
    protected override Size MeasureOverride(Size availableSize) => ContentSize;

    /// <summary>Name used in settings for the bundled 7-segment font (DSEG7 Classic, SIL Open Font License).</summary>
    public const string DigitalFont = "Digital";

    private static (Typeface Regular, Typeface Heavy) FacesFor(string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name;
        if (!Fonts.TryGetValue(name, out var f))
        {
            var family = name == DigitalFont
                ? new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#DSEG7 Classic, Segoe UI")
                : new FontFamily(name + ", Segoe UI");
            f = (new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                 new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
            Fonts[name] = f;
        }
        return f;
    }

    private (Typeface Regular, Typeface Heavy) Faces => FacesFor(_base.FontFamily);

    /// <summary>Font for numbers: the number font if one is set, otherwise the normal font.</summary>
    private (Typeface Regular, Typeface Heavy) NumberFaces =>
        string.IsNullOrWhiteSpace(_base.NumberFont) ? Faces : FacesFor(_base.NumberFont);

    private bool DigitalNumbers => _base.NumberFont == DigitalFont || (string.IsNullOrWhiteSpace(_base.NumberFont) && _base.FontFamily == DigitalFont);

    protected FormattedText Text(string s, double size, Brush brush, bool heavy = false, bool numeric = false)
    {
        // The 7-segment font can't show letters clearly ("SB" reads as "56"), so text with letters
        // in it, like "PB", "kg" or "none", always uses the normal font.
        // Like real dashes, segment digits are for the big "hero" numbers only; small figures stay in the
        // normal font where they're easier to read at a glance.
        if (numeric && DigitalNumbers && (size < 16 || s.Any(c => !(char.IsDigit(c) || c is ':' or '.' or '-' or ' ' or '/')))) numeric = false;
        var faces = numeric ? NumberFaces : Faces;
        // 7-segment digits are big for their point size; scale them down to sit with normal text.
        if (numeric && DigitalNumbers) size *= 0.8;
        return new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, heavy ? faces.Heavy : faces.Regular,
            Math.Max(1, size), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected bool Tiles => _base.Style == "Tiles";

    /// <summary>Tile mode with everything merged into one tile.</summary>
    protected bool Combined => Tiles && _base.CombineTiles;

    /// <summary>A tile for one section (skipped when sections are combined into one tile).</summary>
    protected void SectionTile(DrawingContext dc, Rect rect, string hex)
    {
        if (Tiles && !_base.CombineTiles) DrawTile(dc, rect, hex);
    }

    /// <summary>The single tile used when sections are combined.</summary>
    protected void CombinedTile(DrawingContext dc, Rect rect, string hex)
    {
        if (Combined) DrawTile(dc, rect, hex);
    }

    /// <summary>The thin accent line on top: hidden in tile mode, where every tile has its own coloured outline.</summary>
    protected bool ShowStripe(bool enabled) => enabled && !Tiles;

    /// <summary>
    /// Dash-style tile: dark glassy fill tinted with the colour, a coloured outline and an optional soft glow.
    /// </summary>
    protected void DrawTile(DrawingContext dc, Rect rect, string hex)
    {
        if (rect.Width <= 2 || rect.Height <= 2) return;
        double r = Math.Clamp(Math.Max(_base.CornerRadius, 4), 0, Math.Min(rect.Width, rect.Height) / 2);
        if (_base.Glow)
        {
            for (int k = 3; k >= 1; k--)
            {
                double g = k * 1.4;
                var glowRect = new Rect(rect.X - g, rect.Y - g, rect.Width + g * 2, rect.Height + g * 2);
                dc.DrawRoundedRectangle(null, new Pen(Theme.Brush(hex, 0.22 / k), 1.6), glowRect, r + g, r + g);
            }
        }
        var c = Theme.Parse(hex, Colors.White);
        var fill = new LinearGradientBrush(
            Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(22, c.R, c.G, c.B), 90);
        fill.Freeze();
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.45), null, rect, r, r);
        if (_base.PanelOutline)
        {
            // Dark (or any colour) outline just outside the coloured border, so tiles pop on bright backgrounds.
            double ow = Math.Clamp(_base.PanelOutlineWidth, 0.5, 6);
            var outer = new Rect(rect.X - 0.75 - ow / 2, rect.Y - 0.75 - ow / 2, rect.Width + 1.5 + ow, rect.Height + 1.5 + ow);
            dc.DrawRoundedRectangle(null, new Pen(Theme.Brush(_base.PanelOutlineColor), ow), outer, r + ow / 2, r + ow / 2);
        }
        dc.DrawRoundedRectangle(fill, new Pen(Theme.Brush(hex, 0.9), 1.5), rect, r, r);
    }

    /// <summary>A section rectangle shrunk to leave a gap (and room for the glow) between tiles.</summary>
    protected static Rect TileRect(double x, double y, double w, double h, double gap = 4) =>
        new(x + gap, y + gap, Math.Max(0, w - gap * 2), Math.Max(0, h - gap * 2));

    /// <summary>Draws text with x as left, right or centre anchor and y as the vertical centre.</summary>
    protected Rect DrawText(DrawingContext dc, string s, double size, Brush brush, double x, double yCenter,
        TextAlignment align = TextAlignment.Left, bool heavy = false, bool numeric = false)
    {
        var ft = Text(s, size, brush, heavy, numeric);
        double left = align switch
        {
            TextAlignment.Right => x - ft.Width,
            TextAlignment.Center => x - ft.Width / 2,
            _ => x,
        };
        var origin = new Point(Math.Round(left), Math.Round(yCenter - ft.Height / 2));
        // Faint text (placeholders, hints) stays without an outline; it would only add noise.
        if (_base.TextOutline && !(brush is SolidColorBrush sb && sb.Color.A < 128))
        {
            // Outline: stroke the glyph shapes, then draw the text on top so only the outside edge shows.
            double ow = Math.Clamp(_base.TextOutlineWidth, 0.5, 5);
            var geo = ft.BuildGeometry(origin);
            dc.DrawGeometry(null, new Pen(Theme.Brush(_base.TextOutlineColor), ow * 2) { LineJoin = PenLineJoin.Round }, geo);
        }
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
        if (_base.PanelOutline && !Tiles)
        {
            double ow = Math.Clamp(_base.PanelOutlineWidth, 0.5, 6);
            double pr = Math.Clamp(_base.CornerRadius, 0, Math.Min(size.Width, size.Height) / 2);
            dc.DrawRoundedRectangle(null, new Pen(Theme.Brush(_base.PanelOutlineColor), ow),
                new Rect(ow / 2, ow / 2, size.Width - ow, size.Height - ow), Math.Max(0, pr - ow / 2), Math.Max(0, pr - ow / 2));
        }
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
        (ShowStripe(s.ShowAccentStripe) ? 3 : 0) + (s.ShowTitle ? TitleH : 0) + (s.ShowPersonalBest ? BestH : 0) +
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

        if (ShowStripe(s.ShowAccentStripe))
        {
            dc.DrawRectangle(Theme.Brush(s.AccentColor, s.AccentOpacity), null, new Rect(0, 0, w, 3));
            y += 3;
        }
        if (s.ShowTitle)
        {
            if (!Tiles) dc.DrawRectangle(Theme.Brush(s.TitleBarColor, Math.Min(1, bgAlpha + 0.05)), null, new Rect(0, y, w, TitleH));
            DrawText(dc, s.TitleText ?? "", 11 * fs, Tiles ? Theme.Brush(s.AccentColor) : text, w / 2, y + TitleH / 2, TextAlignment.Center, heavy: true);
            y += TitleH;
        }
        CombinedTile(dc, TileRect(0, y, w, size.Height - y, 3), s.AccentColor);
        if (s.ShowPersonalBest)
        {
            DrawBestRow(dc, y, Theme.Brush(s.PersonalBestColor, bgAlpha), s.FasterColor, "PERSONAL\nSESSION BEST", pb, _pbFlashAt);
            y += BestH;
        }
        if (s.ShowSessionBest)
        {
            DrawBestRow(dc, y, Theme.Brush(s.SessionBestColor, Math.Max(0.35, bgAlpha)), s.SessionBestColor, "SESSION\nBEST", sb, _sbFlashAt);
            y += BestH;
        }

        double timeX = Math.Round(w * 0.177), deltaX = Math.Round(w * 0.54);
        SectionTile(dc, TileRect(0, y, w, (s.ShowColumnHeaders ? rowH : 0) + rows * rowH), s.HeaderColor);
        if (s.ShowColumnHeaders)
        {
            if (!Tiles) dc.DrawRectangle(Theme.Brush(s.TitleBarColor, Math.Min(1, bgAlpha + 0.05)), null, new Rect(0, y, w, rowH));
            double cy = y + rowH / 2 + (Tiles ? 2 : 0);
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

        double rowX = Tiles ? 6 : 0, rowW = Tiles ? w - 12 : w;
        dc.PushClip(new RectangleGeometry(new Rect(rowX, y, rowW, rows * rowH - (Tiles ? 5 : 0))));
        int topLap = newestFirst.Length > 0 ? newestFirst[0].Lap : 0;
        for (int i = 0; i < placed.Count; i++)
        {
            var (lap, ry, newest) = placed[i];
            if (ry >= areaBottom || ry + rowH <= y) continue;

            // Stripes follow the lap number so they don't flicker while rows slide.
            int parity = lap != null ? lap.Lap : (s.NewestOnTop ? topLap - i : i);
            if (s.Stripes && (parity & 1) == 1)
                dc.DrawRectangle(Theme.Brush(s.StripeColor, Tiles ? bgAlpha * 0.6 : bgAlpha), null, new Rect(rowX, ry, rowW, rowH));
            if (lap == null)
            {
                // Faint placeholders so unused rows read as "waiting for laps", not as a broken block.
                double py = ry + rowH / 2;
                var faint = Theme.Brush(s.TextColor, 0.18);
                DrawText(dc, "–", 12 * fs, faint, 10, py);
                DrawText(dc, "--:--.---", 12 * fs, faint, timeX, py, numeric: true);
                continue;
            }

            if (newest && s.FlashNewLap && t < 1.6)
                dc.DrawRectangle(Theme.Brush(_flashHex, 0.5 * (1 - t / 1.6)), null, new Rect(rowX, ry, rowW, rowH));

            double cy = ry + rowH / 2;
            DrawText(dc, lap.Lap.ToString(CultureInfo.InvariantCulture), 12 * fs, text, 10, cy, heavy: true, numeric: true);
            var timeRect = DrawText(dc, LapTime(lap.TimeMs), 12 * fs, lap.Valid ? text : grey, timeX, cy, numeric: true);
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
                if (s.ShowFuel) DrawText(dc, delta, 12 * fs, brush, deltaX, cy, heavy: true, numeric: true);
                else DrawText(dc, delta, 12 * fs, brush, w - 10, cy, TextAlignment.Right, heavy: true, numeric: true);
            }
            if (s.ShowFuel)
                DrawText(dc, lap.FuelKg.ToString("0.0", CultureInfo.InvariantCulture) + " kg", 12 * fs, text, w - 10, cy, TextAlignment.Right, numeric: true);
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

    private bool _primed;

    private void DetectChanges(int count, LapRecord[] newestFirst, uint pb, uint sb)
    {
        if (!_primed)
        {
            // First look at the data (e.g. the app opened mid-session): no slide or flash for laps already done.
            _primed = true;
            _seenLaps = count; _seenPb = pb; _seenSb = sb;
            return;
        }
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

    private void DrawBestRow(DrawingContext dc, double y, Brush bg, string tileHex, string label, uint ms, double flashAt)
    {
        double w = W, h = BestH, fs = Fs;
        var area = Tiles ? TileRect(0, y, w, h) : new Rect(0, y, w, h);
        if (Tiles)
        {
            SectionTile(dc, area, tileHex);
            // Combined: no separate tile, just a soft tint so the best-lap rows still stand out.
            if (Combined) dc.DrawRoundedRectangle(Theme.Brush(tileHex, 0.16), null, area, InnerRadius, InnerRadius);
        }
        else dc.DrawRectangle(bg, null, area);
        double t = Now - flashAt;
        if (s.GlowOnImprove && t < 1.2)
            dc.DrawRoundedRectangle(Theme.Brush(Tiles ? tileHex : "#FFFFFF", 0.3 * (1 - t / 1.2)), null, area, Tiles ? Math.Max(4, s.CornerRadius) : 0, Tiles ? Math.Max(4, s.CornerRadius) : 0);

        var ft = Text(label, 12 * fs, Theme.Brush(s.TextColor), heavy: true);
        dc.DrawText(ft, new Point(Tiles ? 14 : 10, Math.Round(y + h / 2 - ft.Height / 2)));
        double boxX = Math.Round(w * 0.446);
        if (!Tiles)
            dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.3), null, new Rect(boxX, y + h * 0.17, w - boxX - 8, h * 0.66), InnerRadius, InnerRadius);
        DrawText(dc, LapTime(ms), 21 * fs, Theme.Brush(Tiles ? tileHex : s.TextColor), w - 14, y + h / 2, TextAlignment.Right, heavy: true, numeric: true);
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

        CombinedTile(dc, TileRect(0, 0, size.Width, size.Height, 3), s.SpeedColor);

        // Gear + speed
        if (GearW > 0)
        {
            SectionTile(dc, TileRect(0, 0, GearW, h, 4), s.GearColor);
            double cx = GearW / 2 + (Tiles ? 0 : 2);
            if (s.ShowGear)
            {
                double gy = s.ShowSpeed ? h * 0.41 : h / 2;
                DrawText(dc, Names.Gear(gear), h * 0.62, Theme.Brush(s.GearColor), cx, gy, TextAlignment.Center, heavy: true, numeric: true);
            }
            if (s.ShowSpeed)
            {
                double sy = s.ShowGear ? h * 0.83 : h / 2;
                double sz = s.ShowGear ? h * 0.19 : h * 0.4;
                DrawText(dc, speed.ToString(CultureInfo.InvariantCulture), sz, Theme.Brush(s.SpeedColor, 0.9), cx, sy, TextAlignment.Center, heavy: true, numeric: true);
            }
            x += GearW;
        }

        // Trace
        if (s.ShowTrace)
        {
            if (GearW == 0) x += Pad;
            var area = new Rect(x, Pad, TraceW, h - Pad * 2);
            if (Tiles) SectionTile(dc, area, s.SpeedColor);
            else
            {
                var frame = new Pen(Theme.Brush("#FFFFFF", 0.14), 1);
                dc.DrawRoundedRectangle(Theme.Brush(s.TraceBackgroundColor, 0.35 * s.BackgroundOpacity + 0.1), frame, area, InnerRadius, InnerRadius);
            }

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
                dc.PushClip(new RectangleGeometry(area, InnerRadius, InnerRadius));
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
            SectionTile(dc, new Rect(bx - 4, 4, BarW * 2 + BarGap + 8, h - 8), s.SpeedColor);
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
        dc.DrawRoundedRectangle(Theme.Brush("#000000", 0.4), new Pen(Theme.Brush("#FFFFFF", 0.14), 1), frame, Math.Min(InnerRadius, BarW / 2), Math.Min(InnerRadius, BarW / 2));
        double fill = frame.Height * value;
        if (fill > 0.5) dc.DrawRoundedRectangle(Theme.Brush(hex), null, new Rect(x, frame.Bottom - fill, BarW, fill), Math.Min(InnerRadius, BarW / 2), Math.Min(InnerRadius, BarW / 2));
        if (s.ShowBarValues)
            DrawText(dc, ((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture), Math.Max(8, h * 0.14),
                Theme.Brush("#FFFFFF"), x + BarW / 2, top / 2 + 1, TextAlignment.Center, heavy: true, numeric: true);
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
        (ShowStripe(s.ShowAccentStripe) ? 3 : 0) + (s.ShowTitle ? TitleH : 0) + MainH + (StatCount > 0 ? StatCount * StatH + 6 : 0));

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

        if (ShowStripe(s.ShowAccentStripe))
        {
            dc.DrawRectangle(Theme.Brush(s.AccentColor, s.AccentOpacity), null, new Rect(0, 0, w, 3));
            y += 3;
        }
        if (s.ShowTitle)
        {
            if (!Tiles) dc.DrawRectangle(Theme.Brush("#000000", 0.25), null, new Rect(0, y, w, TitleH));
            DrawText(dc, s.TitleText ?? "", 11 * fs, Tiles ? Theme.Brush(s.AccentColor) : text, 12, y + TitleH / 2, heavy: true);
            y += TitleH;
        }

        // Big counter
        double mainTop = y;
        CombinedTile(dc, TileRect(0, mainTop, w, size.Height - mainTop, 3), stateHex);
        SectionTile(dc, TileRect(0, mainTop, w, MainH), stateHex);
        double numY = mainTop + (s.ShowPips ? MainH * 0.45 : MainH / 2);
        var num = DrawText(dc, warnings.ToString(CultureInfo.InvariantCulture), 40 * fs, state, Tiles ? 14 : 12, numY, heavy: true, numeric: true);
        DrawText(dc, $"/ {next}", 16 * fs, muted, num.Right + 6, numY + 6 * fs, heavy: true, numeric: true);

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
                dc.DrawRoundedRectangle(i < inBlock ? state : Theme.Brush("#FFFFFF", 0.12), null, r, Math.Min(InnerRadius, r.Height / 2), Math.Min(InnerRadius, r.Height / 2));
            }
        }
        y += MainH;

        // Stats
        if (StatCount > 0)
        {
            SectionTile(dc, TileRect(0, y, w, StatCount * StatH + 6), s.TextColor);
            y += 3;
            void Stat(string label, string value, Brush valueBrush)
            {
                DrawText(dc, label, 12 * fs, muted, 12, y + StatH / 2);
                DrawText(dc, value, 12 * fs, valueBrush, w - 12, y + StatH / 2, TextAlignment.Right, heavy: true, numeric: true);
                y += StatH;
            }
            if (s.ShowInvalidLaps) Stat("Invalid laps", invalid.ToString(CultureInfo.InvariantCulture), invalid > 0 ? Theme.Brush(s.WarnColor) : text);
            if (s.ShowTotalWarnings) Stat("Total warnings", total.ToString(CultureInfo.InvariantCulture), total > 0 ? Theme.Brush(s.WarnColor) : text);
            if (s.ShowPenalties) Stat("Penalties", penalties > 0 ? $"+{penalties}s" : "none", penalties > 0 ? Theme.Brush(s.DangerColor) : text);
        }

        EndPanel(dc, size);
    }
}

/// <summary>Tyre strategy: current tyre and wear, weather + forecast, optimal tyre now, and the next stop.</summary>
public sealed class TyreStrategyView(TelemetryHub hub, TyreStrategySettings s) : OverlayView(hub, s)
{
    private double W => Math.Round(Math.Clamp(s.Width, 230, 420));
    private double Fs => Math.Clamp(s.TextScale, 0.6, 1.8);
    private double H(double h) => Math.Round(h * Fs);

    private double SectionsHeight =>
        (ShowStripe(s.ShowAccentStripe) ? 3 : 0) + (s.ShowTitle ? H(24) : 0) +
        (s.ShowCurrent ? H(60) : 0) + (s.ShowWheels ? H(40) : 0) +
        (s.ShowWeather ? H(30) : 0) + (s.ShowForecast ? H(44) : 0) +
        (s.ShowOptimalNow ? H(58) : 0) + 4;

    protected override Size ContentSize => new(W, SectionsHeight);

    protected override void OnRender(DrawingContext dc)
    {
        TyreAdvice a;
        lock (Hub.Sync) a = TyreAdvisor.Advise(Hub, Math.Clamp(s.PitWearThreshold, 30, 95));

        var size = ContentSize;
        double w = W, fs = Fs, y = 0;
        var text = Theme.Brush(s.TextColor);
        var muted = Theme.Brush(s.TextColor, 0.6);
        var line = Theme.Brush("#FFFFFF", 0.07);

        BeginPanel(dc, size);
        if (ShowStripe(s.ShowAccentStripe)) { dc.DrawRectangle(Theme.Brush(s.AccentColor, s.AccentOpacity), null, new Rect(0, 0, w, 3)); y += 3; }
        if (s.ShowTitle)
        {
            if (!Tiles) dc.DrawRectangle(Theme.Brush("#000000", 0.25), null, new Rect(0, y, w, H(24)));
            DrawText(dc, s.TitleText ?? "", 11 * fs, Tiles ? Theme.Brush(s.AccentColor) : text, 12, y + H(24) / 2, heavy: true);
            if (!a.HasData) DrawText(dc, "waiting for data", 10 * fs, muted, w - 12, y + H(24) / 2, TextAlignment.Right);
            y += H(24);
        }

        void Divider() { if (!Tiles || Combined) dc.DrawRectangle(line, null, new Rect(10, y, w - 20, 1)); }
        CombinedTile(dc, TileRect(0, y, w, size.Height - y, 3), s.AccentColor);
        string WearHex(double v) => v >= s.PitWearThreshold ? s.SoftColor : v >= s.PitWearThreshold * 0.75 ? s.MediumColor : s.InterColor;

        // Current tyre
        if (s.ShowCurrent)
        {
            double h = H(60), cy = y + h / 2;
            SectionTile(dc, TileRect(0, y, w, h), CompoundHex(a.FittedVisual));
            DrawChip(dc, 12 + 18 * fs, cy, 18 * fs, a.FittedVisual);
            double tx = 12 + 44 * fs;
            DrawText(dc, CompoundName(a.FittedVisual), 14 * fs, text, tx, cy - 9 * fs, heavy: true);
            DrawText(dc, $"{a.TyreAge} laps old · wear {a.MaxWear:0}%", 11 * fs, muted, tx, cy + 10 * fs);

            string left = a.LapsLeftOnTyre is double l ? (l >= 99 ? "99+" : $"{l:0}") : "–";
            var wearHex = a.MaxWear >= s.PitWearThreshold ? s.SoftColor : a.MaxWear >= s.PitWearThreshold * 0.75 ? s.MediumColor : s.InterColor;
            DrawText(dc, left, 24 * fs, Theme.Brush(wearHex), w - 12, cy - 6 * fs, TextAlignment.Right, heavy: true, numeric: true);
            DrawText(dc, "laps left", 10 * fs, muted, w - 12, cy + 14 * fs, TextAlignment.Right);
            y += h;
        }

        // Wear per wheel
        if (s.ShowWheels)
        {
            double h = H(40);
            Divider();
            SectionTile(dc, TileRect(0, y, w, h, 3), WearHex(a.MaxWear));
            string[] labels = ["FL", "FR", "RL", "RR"];
            int[] idx = [2, 3, 0, 1]; // wear array is RL, RR, FL, FR
            double cellW = (w - 24) / 4;
            for (int i = 0; i < 4; i++)
            {
                float v = a.Wear[idx[i]];
                double x = 12 + i * cellW;
                DrawText(dc, labels[i], 9 * fs, muted, x, y + h * 0.3);
                DrawText(dc, $"{v:0}%", 12 * fs, text, x + cellW - 6, y + h * 0.3, TextAlignment.Right, heavy: true, numeric: true);
                var bar = new Rect(x, y + h * 0.62, cellW - 6, 4 * fs);
                dc.DrawRoundedRectangle(Theme.Brush("#FFFFFF", 0.1), null, bar, Math.Min(InnerRadius, bar.Height / 2), Math.Min(InnerRadius, bar.Height / 2));
                double fill = bar.Width * Math.Clamp(v / 100.0, 0, 1);
                var hex = v >= s.PitWearThreshold ? s.SoftColor : v >= s.PitWearThreshold * 0.75 ? s.MediumColor : s.InterColor;
                if (fill > 0.5) dc.DrawRoundedRectangle(Theme.Brush(hex), null, new Rect(bar.X, bar.Y, fill, bar.Height), Math.Min(InnerRadius, bar.Height / 2), Math.Min(InnerRadius, bar.Height / 2));
            }
            y += h;
        }

        // One tile around weather + forecast, coloured by the conditions now.
        if (Tiles && (s.ShowWeather || s.ShowForecast))
        {
            var nowHex = a.ConditionsNow == Conditions.Dry ? s.TextColor : a.ConditionsNow == Conditions.Inter ? s.InterColor : s.WetColor;
            SectionTile(dc, TileRect(0, y, w, (s.ShowWeather ? H(30) : 0) + (s.ShowForecast ? H(44) : 0), 3), nowHex);
        }

        // Weather now
        if (s.ShowWeather)
        {
            double h = H(30);
            Divider();
            DrawText(dc, $"{Names.Weather(a.WeatherNow)} · {a.RainNow}% rain", 12 * fs, text, 12, y + h / 2, heavy: true);
            DrawText(dc, $"track {a.TrackTemp}°C", 11 * fs, muted, w - 12, y + h / 2, TextAlignment.Right);
            y += h;
        }

        // Forecast strip
        if (s.ShowForecast)
        {
            double h = H(44);
            var samples = a.Forecast.Where(f => f.TimeOffsetMin > 0).Take(Math.Clamp(s.ForecastCount, 1, 8)).ToArray();
            if (samples.Length == 0)
                DrawText(dc, "No forecast for this session", 11 * fs, muted, 12, y + h / 2);
            else
            {
                double cellW = (w - 24) / samples.Length;
                for (int i = 0; i < samples.Length; i++)
                {
                    var f = samples[i];
                    var c = TyreAdvisor.ConditionsFor(f.Weather);
                    var cHex = c == Conditions.Dry ? s.TextColor : c == Conditions.Inter ? s.InterColor : s.WetColor;
                    var cell = new Rect(12 + i * cellW + 2, y + 4, cellW - 4, h - 8);
                    dc.DrawRoundedRectangle(Theme.Brush(cHex, c == Conditions.Dry ? 0.06 : 0.16), null, cell, InnerRadius, InnerRadius);
                    DrawText(dc, $"+{f.TimeOffsetMin}'", 9 * fs, muted, cell.X + cell.Width / 2, cell.Y + cell.Height * 0.25, TextAlignment.Center);
                    DrawText(dc, ShortWeather(f.Weather), 10 * fs, Theme.Brush(cHex), cell.X + cell.Width / 2, cell.Y + cell.Height * 0.52, TextAlignment.Center, heavy: true);
                    DrawText(dc, $"{f.RainPercent}%", 9 * fs, muted, cell.X + cell.Width / 2, cell.Y + cell.Height * 0.8, TextAlignment.Center);
                }
            }
            y += h;
        }

        // Optimal tyre now
        if (s.ShowOptimalNow)
        {
            double h = H(58), cy = y + h / 2;
            Divider();
            SectionTile(dc, TileRect(0, y, w, h, 3), a.ChangeNow && a.HasData ? s.AlertColor : CompoundHex(a.OptimalNowVisual));
            if (a.ChangeNow && a.HasData)
            {
                double alpha = s.PulseOnChange ? 0.18 + 0.14 * Math.Sin(Now * 5) : 0.25;
                var pulse = Tiles ? TileRect(0, y, w, h, 3) : new Rect(0, y + 1, w, h - 1);
                double pr = Tiles ? Math.Max(4, s.CornerRadius) : 0;
                dc.DrawRoundedRectangle(Theme.Brush(s.AlertColor, alpha), null, pulse, pr, pr);
            }
            DrawText(dc, "OPTIMAL NOW", 9 * fs, a.ChangeNow ? Theme.Brush(s.AlertColor) : muted, 12, y + H(12), heavy: true);
            DrawChip(dc, 12 + 13 * fs, cy + 6 * fs, 13 * fs, a.OptimalNowVisual);
            DrawText(dc, CompoundName(a.OptimalNowVisual), 13 * fs, text, 12 + 32 * fs, cy + 6 * fs - 7 * fs, heavy: true);
            DrawText(dc, a.NowReason, 10 * fs, a.ChangeNow ? text : muted, 12 + 32 * fs, cy + 6 * fs + 8 * fs);
            y += h;
        }

        EndPanel(dc, size);
    }

    private string CompoundName(byte visual) => visual switch
    {
        TyreAdvisor.Soft => "SOFT", TyreAdvisor.Medium => "MEDIUM", TyreAdvisor.Hard => "HARD",
        TyreAdvisor.Inter => "INTERMEDIATE", TyreAdvisor.Wet => "WET", 0 => "–", _ => Names.Compound(visual),
    };

    private string CompoundHex(byte visual) => visual switch
    {
        TyreAdvisor.Soft => s.SoftColor, TyreAdvisor.Medium => s.MediumColor, TyreAdvisor.Hard => s.HardColor,
        TyreAdvisor.Inter => s.InterColor, TyreAdvisor.Wet => s.WetColor, _ => "#8A8A94",
    };

    private static string Letter(byte visual) => visual switch
    {
        TyreAdvisor.Soft => "S", TyreAdvisor.Medium => "M", TyreAdvisor.Hard => "H",
        TyreAdvisor.Inter => "I", TyreAdvisor.Wet => "W", _ => "?",
    };

    private static string ShortWeather(byte w) => w switch
    {
        0 => "Clear", 1 => "Cloudy", 2 => "Overcast", 3 => "Lt rain", 4 => "Rain", 5 => "Storm", _ => "?",
    };

    /// <summary>Tyre "chip": dark disc with a coloured sidewall ring and the compound letter.</summary>
    private void DrawChip(DrawingContext dc, double cx, double cy, double r, byte visual)
    {
        var colour = Theme.Brush(CompoundHex(visual));
        dc.DrawEllipse(Theme.Brush("#0A0A0C"), new Pen(colour, Math.Max(2, r * 0.28)), new Point(cx, cy), r - r * 0.14, r - r * 0.14);
        DrawText(dc, Letter(visual), r * 0.95, colour, cx, cy, TextAlignment.Center, heavy: true);
    }
}

/// <summary>FPS counter: frame rate of the game (via PresentMon), 1% low, frame time and a frame-time graph.</summary>
public sealed class FpsView(TelemetryHub hub, FpsSettings s, FpsMonitor monitor) : OverlayView(hub, s)
{
    private const double Pad = 10;

    private string? _app;
    private double _appCheckedAt = double.MinValue;
    private double _shownFps;
    private (double Fps, double Low1, double FrameMs, double[] Recent)? _stats;
    private string _status = "";

    private double Fs => Math.Clamp(s.TextScale, 0.6, 2.0);
    private double MainH => Math.Round(54 * Fs);
    private double GraphH => Math.Round(30 * Fs);
    private bool HasRight => s.ShowLow || s.ShowFrameTime || (s.ShowStatus && _stats == null);
    private bool HasMain => s.ShowNumber || HasRight;

    /// <summary>The app being measured: the one you chose, else the focused window (checked twice a second).</summary>
    private string? TargetApp()
    {
        if (!string.IsNullOrWhiteSpace(s.TargetApp)) return s.TargetApp.Trim();
        if (Now - _appCheckedAt < 0.5) return _app;
        _appCheckedAt = Now;
        var fg = FpsMonitor.ForegroundExe();
        // Ignore our own windows and the desktop so the reading stays on the game when you alt-tab to the panel.
        if (fg != null && !fg.StartsWith("F126", StringComparison.OrdinalIgnoreCase) && !fg.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            _app = fg;
        return _app;
    }

    // Widths of each part, so the panel can shrink to what's switched on.
    private double NumberW => Text("000", 30 * Fs, Brushes.White, heavy: true, numeric: true).Width + (s.ShowLabel ? Text("FPS", 11 * Fs, Brushes.White, heavy: true).Width + 5 : 0);
    private double RightW
    {
        get
        {
            double w = 0;
            if (_stats != null)
            {
                if (s.ShowLow) w = Math.Max(w, Text("1% 000", 11 * Fs, Brushes.White, heavy: true, numeric: true).Width);
                if (s.ShowFrameTime) w = Math.Max(w, Text("00.0 ms", 11 * Fs, Brushes.White, numeric: true).Width);
            }
            else if (s.ShowStatus) w = Text(_status, 10 * Fs, Brushes.White).Width;
            return w;
        }
    }

    private double W
    {
        get
        {
            if (!s.AutoWidth) return Math.Round(Math.Clamp(s.Width, 80, 320));
            double content = (s.ShowNumber ? NumberW : 0) + (s.ShowNumber && RightW > 0 ? 16 : 0) + RightW;
            if (s.ShowGraph) content = Math.Max(content, 90 * Fs);
            return Math.Round(Math.Max(40, content + Pad * 2));
        }
    }

    protected override Size ContentSize =>
        new(W, Math.Max(8, (ShowStripe(s.ShowAccentStripe) ? 3 : 0) + (HasMain ? MainH : 0) + (s.ShowGraph ? GraphH : 0)));

    private void Refresh()
    {
        string? app = TargetApp();
        _stats = app != null ? monitor.Stats(app) : null;
        _status =
            !FpsMonitor.IsDownloaded || !FpsMonitor.HasPermission ? "setup needed" :
            !monitor.IsRunning ? "not running" :
            _stats == null ? "waiting for game" : "";
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Refresh();
        return ContentSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        Refresh();
        var size = ContentSize;
        if (Math.Abs(size.Width - ActualWidth) > 0.5 || Math.Abs(size.Height - ActualHeight) > 0.5) InvalidateMeasure();

        double w = size.Width, fs = Fs, y = 0;
        var muted = Theme.Brush(s.TextColor, 0.6);

        BeginPanel(dc, size);
        if (ShowStripe(s.ShowAccentStripe)) { dc.DrawRectangle(Theme.Brush(s.AccentColor, s.AccentOpacity), null, new Rect(0, 0, w, 3)); y += 3; }

        if (Tiles)
        {
            double fpsNow = _stats?.Fps ?? 0;
            string tileHex = _stats == null ? s.TextColor : fpsNow >= s.GoodFps ? s.GoodColor : fpsNow >= s.OkFps ? s.OkColor : s.BadColor;
            DrawTile(dc, TileRect(0, y, w, size.Height - y, 3), tileHex);
        }

        if (HasMain)
        {
            double cy = y + MainH / 2;
            if (_stats is { } st)
            {
                // Ease the number a little so it's readable instead of flickering every frame.
                _shownFps = _shownFps <= 0 ? st.Fps : _shownFps + (st.Fps - _shownFps) * 0.15;
                if (s.ShowNumber)
                {
                    string hex = _shownFps >= s.GoodFps ? s.GoodColor : _shownFps >= s.OkFps ? s.OkColor : s.BadColor;
                    var num = DrawText(dc, Math.Round(_shownFps).ToString(CultureInfo.InvariantCulture), 30 * fs, Theme.Brush(hex), Pad, cy, heavy: true, numeric: true);
                    if (s.ShowLabel) DrawText(dc, "FPS", 11 * fs, muted, num.Right + 5, cy + 6 * fs, heavy: true);
                }

                double rx = s.ShowNumber ? w - Pad : Pad;
                var align = s.ShowNumber ? TextAlignment.Right : TextAlignment.Left;
                double ry = s.ShowLow && s.ShowFrameTime ? cy - 8 * fs : cy;
                if (s.ShowLow)
                {
                    string lowHex = st.Low1 >= s.GoodFps ? s.GoodColor : st.Low1 >= s.OkFps ? s.OkColor : s.BadColor;
                    DrawText(dc, $"1% {Math.Round(st.Low1)}", 11 * fs, Theme.Brush(lowHex), rx, ry, align, heavy: true, numeric: true);
                    ry += 16 * fs;
                }
                if (s.ShowFrameTime)
                    DrawText(dc, $"{st.FrameMs:0.0} ms", 11 * fs, muted, rx, ry, align, numeric: true);
            }
            else
            {
                _shownFps = 0;
                if (s.ShowNumber)
                {
                    var num = DrawText(dc, "–", 30 * fs, muted, Pad, cy, heavy: true);
                    if (s.ShowLabel) DrawText(dc, "FPS", 11 * fs, muted, num.Right + 5, cy + 6 * fs, heavy: true);
                }
                if (s.ShowStatus)
                    DrawText(dc, _status, 10 * fs, muted, s.ShowNumber ? w - Pad : Pad, cy, s.ShowNumber ? TextAlignment.Right : TextAlignment.Left);
            }
            y += MainH;
        }

        if (s.ShowGraph)
        {
            var area = new Rect(Pad, y + (HasMain ? 0 : 6), w - Pad * 2, GraphH - (HasMain ? 8 : 12));
            dc.DrawRoundedRectangle(Theme.Brush("#000000", Tiles ? 0.2 : 0.3), null, area, InnerRadius, InnerRadius);
            if (_stats is { } g && g.Recent.Length > 1)
            {
                // Scale so the "OK" frame time sits at mid height; spikes go to the top.
                double maxMs = 2000.0 / Math.Max(1, s.OkFps);
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    double step = area.Width / (g.Recent.Length - 1);
                    for (int i = 0; i < g.Recent.Length; i++)
                    {
                        double v = Math.Clamp(g.Recent[i] / maxMs, 0, 1);
                        var p = new Point(area.Left + i * step, area.Bottom - 2 - v * (area.Height - 4));
                        if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, true);
                    }
                }
                geo.Freeze();
                dc.PushClip(new RectangleGeometry(area, InnerRadius, InnerRadius));
                double midY = Math.Round(area.Top + area.Height / 2) + 0.5;
                dc.DrawLine(new Pen(Theme.Brush("#FFFFFF", 0.1), 1), new Point(area.Left, midY), new Point(area.Right, midY));
                dc.DrawGeometry(null, new Pen(Theme.Brush(s.GoodColor), 1.4 * fs) { LineJoin = PenLineJoin.Round }, geo);
                dc.Pop();
            }
        }

        EndPanel(dc, size);
    }
}
