using System.Text.Json;

namespace F126Overlay;

/// <summary>Settings shared by every overlay.</summary>
public abstract class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.9;
    public double[]? Position { get; set; }
}

public sealed class LapLogSettings : OverlaySettings
{
    public int Rows { get; set; } = 8;
    public string AccentColor { get; set; } = "#E10600";
    public string SessionBestColor { get; set; } = "#9B00B8";
    public string HeaderColor { get; set; } = "#E8C33A";
    public bool ShowSessionBest { get; set; } = true;
    public bool ShowFuel { get; set; } = true;
    public bool ShowDelta { get; set; } = true;
}

public sealed class InputsSettings : OverlaySettings
{
    public double TraceSeconds { get; set; } = 5;
    public string GearColor { get; set; } = "#FFA21A";
    public string ThrottleColor { get; set; } = "#2BD34F";
    public string BrakeColor { get; set; } = "#FF2E2E";
    public bool ShowSpeed { get; set; } = true;
    public bool ShowBars { get; set; } = true;
    public bool FillTrace { get; set; } = true;
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
