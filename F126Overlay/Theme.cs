using System.Windows;
using System.Windows.Media;

namespace F126Overlay;

/// <summary>Colours, fonts and small helpers shared by the overlays and the control panel.</summary>
public static class Theme
{
    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    public static readonly Color WindowBg = C("#FF101014");
    public static readonly Color Sidebar = C("#FF0B0B0E");
    public static readonly Color Card = C("#FF18181E");
    public static readonly Color CardHover = C("#FF1F1F27");
    public static readonly Color Line = C("#FF26262F");
    public static readonly Color TextColor = C("#FFF2F2F5");
    public static readonly Color Muted = C("#FF8C8C99");
    public static readonly Color Accent = C("#FFE10600");
    public static readonly Color Live = C("#FF2BD34F");
    public static readonly Color Track = C("#FF2C2C36");

    /// <summary>Preset colours offered in the swatch pickers.</summary>
    public static readonly string[] Presets =
    [
        "#E10600", "#FF8700", "#FFA21A", "#FFD400", "#E8C33A", "#2BD34F", "#00D2BE",
        "#2F7BFF", "#6C5CE7", "#9B00B8", "#FF3D9A", "#FFFFFF",
    ];

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color Parse(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : C(hex); }
        catch { return fallback; }
    }

    public static SolidColorBrush B(Color c) => new(c);
    public static SolidColorBrush B(string hex) => new(C(hex));

    private static readonly Dictionary<(string, int), SolidColorBrush> Cache = [];

    /// <summary>Frozen, cached brush of a hex colour with its alpha multiplied by <paramref name="alpha"/>.</summary>
    public static SolidColorBrush Brush(string hex, double alpha = 1.0)
    {
        int a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        if (Cache.TryGetValue((hex, a), out var b)) return b;
        var c = Parse(hex, Colors.White);
        c.A = (byte)(c.A * a / 255);
        b = new SolidColorBrush(c);
        b.Freeze();
        if (Cache.Count > 4000) Cache.Clear();
        Cache[(hex, a)] = b;
        return b;
    }

    public static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - Math.Pow(1 - t, 3);
    }

    public static double EaseOutBack(double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));
}
