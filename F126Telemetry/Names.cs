namespace F126Telemetry;

/// <summary>Lookup tables from the UDP spec appendix. Unknown ids fall back to the number.</summary>
public static class Names
{
    private static readonly Dictionary<int, string> Tracks = new()
    {
        [0] = "Melbourne", [2] = "Shanghai", [3] = "Sakhir", [4] = "Catalunya", [5] = "Monaco",
        [6] = "Montreal", [7] = "Silverstone", [9] = "Hungaroring", [10] = "Spa", [11] = "Monza",
        [12] = "Singapore", [13] = "Suzuka", [14] = "Abu Dhabi", [15] = "Texas", [16] = "Brazil",
        [17] = "Austria", [19] = "Mexico", [20] = "Baku", [26] = "Zandvoort", [27] = "Imola",
        [29] = "Jeddah", [30] = "Miami", [31] = "Las Vegas", [32] = "Losail",
        [39] = "Silverstone (Reverse)", [40] = "Austria (Reverse)", [41] = "Zandvoort (Reverse)",
    };

    private static readonly Dictionary<int, string> Sessions = new()
    {
        [0] = "Unknown", [1] = "P1", [2] = "P2", [3] = "P3", [4] = "Short Practice",
        [5] = "Q1", [6] = "Q2", [7] = "Q3", [8] = "Short Quali", [9] = "One-Shot Quali",
        [10] = "Sprint Shootout 1", [11] = "Sprint Shootout 2", [12] = "Sprint Shootout 3",
        [13] = "Short Sprint Shootout", [14] = "One-Shot Sprint Shootout",
        [15] = "Race", [16] = "Race 2", [17] = "Race 3", [18] = "Time Trial",
    };

    private static readonly string[] WeatherNames =
        ["Clear", "Light cloud", "Overcast", "Light rain", "Heavy rain", "Storm"];

    public static string Track(int id) => Tracks.TryGetValue(id, out var n) ? n : $"Track {id}";
    public static string Session(int id) => Sessions.TryGetValue(id, out var n) ? n : $"Session {id}";
    public static string Weather(int id) => id >= 0 && id < WeatherNames.Length ? WeatherNames[id] : $"Weather {id}";

    public static string Compound(byte visual) => visual switch
    {
        16 => "SOFT", 17 => "MEDIUM", 18 => "HARD", 7 => "INTER", 8 => "WET",
        _ => $"#{visual}",
    };

    public static string Gear(sbyte g) => g switch { -1 => "R", 0 => "N", _ => g.ToString() };

    public static string LapTime(uint ms)
    {
        if (ms == 0) return "--:--.---";
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>File-name-safe version of a label.</summary>
    public static string Safe(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
