using System.Text.Json;

namespace F126Overlay;

/// <summary>Settings shared by every overlay.</summary>
public abstract class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.9;
    public string BackgroundColor { get; set; } = "#141419";
    public double CornerRadius { get; set; } = 0;
    public string FontFamily { get; set; } = "Segoe UI";
    public double[]? Position { get; set; }
}

public sealed class LapLogSettings : OverlaySettings
{
    // Layout
    public double Width { get; set; } = 260;
    public double RowHeight { get; set; } = 26;
    public double TextScale { get; set; } = 1.0;
    public int Rows { get; set; } = 8;

    // Content
    public bool ShowAccentStripe { get; set; } = true;
    public bool ShowTitle { get; set; } = true;
    public string TitleText { get; set; } = "LAPTIME LOG";
    public bool ShowPersonalBest { get; set; } = true;
    public bool ShowSessionBest { get; set; } = true;
    public bool ShowColumnHeaders { get; set; } = true;
    public bool ShowDelta { get; set; } = true;
    public bool ShowFuel { get; set; } = true;
    public bool Stripes { get; set; } = true;
    public bool NewestOnTop { get; set; } = true;

    // Colours
    public string AccentColor { get; set; } = "#E10600";
    public string TitleBarColor { get; set; } = "#0B0B0F";
    public string StripeColor { get; set; } = "#22222A";
    public string PersonalBestColor { get; set; } = "#141419";
    public string SessionBestColor { get; set; } = "#9B00B8";
    public string HeaderColor { get; set; } = "#E8C33A";
    public string TextColor { get; set; } = "#FFFFFF";
    public string FasterColor { get; set; } = "#2BD34F";
    public string SlowerColor { get; set; } = "#FF4A4A";

    // Effects
    public bool AnimateNewLap { get; set; } = true;
    public bool FlashNewLap { get; set; } = true;
    public bool GlowOnImprove { get; set; } = true;
}

public sealed class InputsSettings : OverlaySettings
{
    public InputsSettings() { CornerRadius = 8; }

    // Layout
    public double Height { get; set; } = 64;
    public double TraceWidth { get; set; } = 168;
    public double LineThickness { get; set; } = 1.7;
    public double TraceSeconds { get; set; } = 5;

    // Content
    public bool ShowGear { get; set; } = true;
    public bool ShowSpeed { get; set; } = true;
    public bool ShowTrace { get; set; } = true;
    public bool ShowBars { get; set; } = true;
    public bool ShowBarValues { get; set; } = true;
    public bool FillTrace { get; set; } = true;
    public bool GridLines { get; set; }

    // Colours
    public string GearColor { get; set; } = "#FFA21A";
    public string SpeedColor { get; set; } = "#FFFFFF";
    public string ThrottleColor { get; set; } = "#2BD34F";
    public string BrakeColor { get; set; } = "#FF2E2E";
    public string TraceBackgroundColor { get; set; } = "#000000";

    // Effects
    public bool SmoothBars { get; set; } = true;
}

/// <summary>User settings, saved as JSON in %AppData%\F126Overlay\settings.json.</summary>
public sealed class Settings
{
    public int Port { get; set; } = 20999;
    public bool Locked { get; set; }
    public bool LogLaps { get; set; } = true;
    public LapLogSettings LapLog { get; set; } = new();
    public InputsSettings Inputs { get; set; } = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "F126Overlay");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                s.LapLog ??= new();
                s.Inputs ??= new();
                return s;
            }
        }
        catch { /* corrupt or old file: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not fatal */ }
    }

    public static string LapLogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "F126Telemetry", "laps");
}

public static class SettingsExtensions
{
    /// <summary>Copies every writable property from a fresh default instance into an existing one.</summary>
    public static void ResetToDefaults<T>(this T target) where T : new()
    {
        var defaults = new T();
        foreach (var p in typeof(T).GetProperties())
            if (p.CanWrite && p.CanRead) p.SetValue(target, p.GetValue(defaults));
    }
}

/// <summary>Ready-made colour themes applied to both overlays.</summary>
public sealed record OverlayTheme(
    string Name, string Background, string Accent, string TitleBar, string Stripe, string PersonalBest,
    string SessionBest, string Header, string Text, string Gear, string Throttle, string Brake, string Font, double Radius)
{
    public static readonly OverlayTheme[] All =
    [
        new("Classic F1", "#141419", "#E10600", "#0B0B0F", "#22222A", "#141419", "#9B00B8", "#E8C33A", "#FFFFFF", "#FFA21A", "#2BD34F", "#FF2E2E", "Segoe UI", 0),
        new("Midnight", "#0D1117", "#2F7BFF", "#090C10", "#161B22", "#0D1117", "#6C5CE7", "#58A6FF", "#E6EDF3", "#58A6FF", "#3FB950", "#F85149", "Segoe UI", 10),
        new("Neon", "#0A0A12", "#FF3D9A", "#07070D", "#15152A", "#0A0A12", "#9B00B8", "#00D2BE", "#FFFFFF", "#00D2BE", "#39FF14", "#FF3D9A", "Bahnschrift", 6),
        new("Stealth", "#000000", "#FFFFFF", "#000000", "#111114", "#000000", "#6C5CE7", "#9A9AA4", "#FFFFFF", "#FFFFFF", "#D0D0D0", "#FF4A4A", "Bahnschrift", 0),
        new("Papaya", "#121212", "#FF8700", "#0A0A0A", "#1E1E1E", "#121212", "#9B00B8", "#FF8700", "#FFFFFF", "#FF8700", "#47C7FC", "#FF2E2E", "Bahnschrift", 4),
        new("Racing Green", "#0B1A14", "#00A86B", "#06110D", "#12271E", "#0B1A14", "#9B00B8", "#C9A227", "#F2F2F2", "#C9A227", "#2BD34F", "#FF4A4A", "Segoe UI", 8),
    ];

    public void ApplyTo(Settings s)
    {
        foreach (OverlaySettings o in new OverlaySettings[] { s.LapLog, s.Inputs })
        {
            o.BackgroundColor = Background;
            o.FontFamily = Font;
            o.CornerRadius = Radius;
        }
        var l = s.LapLog;
        l.AccentColor = Accent; l.TitleBarColor = TitleBar; l.StripeColor = Stripe; l.PersonalBestColor = PersonalBest;
        l.SessionBestColor = SessionBest; l.HeaderColor = Header; l.TextColor = Text;
        var i = s.Inputs;
        i.GearColor = Gear; i.SpeedColor = Text; i.ThrottleColor = Throttle; i.BrakeColor = Brake;
    }
}
