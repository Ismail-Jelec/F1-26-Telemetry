using System.Text;

namespace F126Telemetry;

/// <summary>Redraws a live console dashboard of the player's car ~10 times per second.</summary>
public sealed class Dashboard(TelemetryHub hub, LapLogger? logger, int port)
{
    private const string Reset = "\u001b[0m", Bold = "\u001b[1m", Dim = "\u001b[2m";
    private const string Red = "\u001b[31m", Green = "\u001b[32m", Yellow = "\u001b[33m";
    private const string Blue = "\u001b[34m", Magenta = "\u001b[35m", Cyan = "\u001b[36m";

    private float _ersMax = 4_000_000f; // grows if the car reports a bigger store
    private int _lastLineCount;

    public void Run(CancellationToken ct)
    {
        Console.CursorVisible = false;
        Console.Clear();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Draw();
                Thread.Sleep(100);
            }
        }
        finally
        {
            Console.CursorVisible = true;
            Console.Write(Reset);
        }
    }

    private void Draw()
    {
        List<string> lines;
        lock (hub.Sync) lines = BuildLines();

        int width = Math.Max(40, SafeWidth() - 1);
        var sb = new StringBuilder();
        sb.Append("\u001b[H"); // cursor home
        foreach (var line in lines) sb.Append(line).Append("\u001b[K\n"); // clear rest of line
        for (int i = lines.Count; i < _lastLineCount; i++) sb.Append("\u001b[K\n");
        _lastLineCount = lines.Count;
        Console.Write(sb.ToString());
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch { return 100; }
    }

    private List<string> BuildLines()
    {
        var L = new List<string>();
        var ago = DateTime.UtcNow - hub.LastPacketUtc;
        bool live = ago.TotalSeconds < 2;

        string status = live ? $"{Green}● LIVE{Reset}" : $"{Yellow}○ waiting for data on UDP {port}{Reset}";
        L.Add($"{Bold}{Red}F1 26 TELEMETRY{Reset}  {status}");
        L.Add(Dim + new string('─', 72) + Reset);

        if (hub.WrongFormat is ushort fmt)
        {
            L.Add($"{Red}Game is sending UDP format {fmt}. Set Settings > Telemetry > UDP Format to 2026.{Reset}");
            return L;
        }

        if (hub.Telemetry == null)
        {
            L.Add("In F1 26: Settings > Telemetry Settings");
            L.Add("  UDP Telemetry: On   Send Rate: 60Hz   UDP Format: 2026");
            L.Add($"With SimHub: game → SimHub (20777), then in SimHub forward UDP to 127.0.0.1:{port}");
            L.Add($"Without SimHub: set the game's UDP IP to 127.0.0.1 and Port to {port}");
            L.Add("Then drive out of the garage. Press Ctrl+C to quit.");
            return L;
        }

        var t = hub.Telemetry;
        var lap = hub.Lap;
        var s = hub.Status;
        var t2 = hub.Telemetry2;
        var ses = hub.Session;

        if (ses != null)
            L.Add($"{Bold}{Names.Track(ses.TrackId)}{Reset} · {Names.Session(ses.SessionType)} · " +
                  $"{Names.Weather(ses.Weather)} · air {ses.AirTemp}°C · track {ses.TrackTemp}°C");
        else
            L.Add("Session info pending…");

        if (lap != null)
        {
            string laps = ses != null && ses.TotalLaps > 0 ? $"{lap.CurrentLapNum}/{ses.TotalLaps}" : $"{lap.CurrentLapNum}";
            string inv = lap.CurrentLapInvalid ? $" {Red}INVALID{Reset}" : "";
            uint best = hub.PersonalBestMs;
            L.Add($"LAP {Bold}{laps}{Reset}   P{Bold}{lap.CarPosition}{Reset}   Sector {lap.Sector + 1}   " +
                  $"Current {Bold}{Names.LapTime(lap.CurrentLapTimeMs)}{Reset}{inv}");
            L.Add($"Last {Names.LapTime(lap.LastLapTimeMs)}   Best {Magenta}{Names.LapTime(best)}{Reset}   " +
                  $"Gap ahead {Gap(lap.DeltaToCarInFrontMs, lap.CarPosition)}   Leader {Gap(lap.DeltaToLeaderMs, lap.CarPosition)}");
        }
        L.Add("");

        string sugg = t.SuggestedGear > 0 && t.SuggestedGear != t.Gear ? $" {Dim}(suggest {t.SuggestedGear}){Reset}" : "";
        L.Add($"SPEED {Bold}{t.SpeedKph,3}{Reset} km/h    GEAR {Bold}{Names.Gear(t.Gear)}{Reset}{sugg}    RPM {t.EngineRpm,5}");
        L.Add($"REV   {Bar(t.RevLightsPercent / 100f, 30, t.RevLightsPercent > 90 ? Red : Cyan)}");
        L.Add($"THR   {Bar(t.Throttle, 30, Green)} {t.Throttle * 100,3:F0}%");
        L.Add($"BRK   {Bar(t.Brake, 30, Red)} {t.Brake * 100,3:F0}%");
        L.Add($"STEER {SteerBar(t.Steer, 31)}");
        L.Add("");

        if (s != null)
        {
            _ersMax = Math.Max(_ersMax, s.ErsStoreEnergy);
            L.Add($"ERS   {Bar(s.ErsStoreEnergy / _ersMax, 20, Yellow)} {s.ErsStoreEnergy / 1e6:F2} MJ   " +
                  $"mode {ErsMode(s.ErsDeployMode)}   deployed {s.ErsDeployedThisLap / 1e6:F2} MJ   " +
                  $"harvested {(s.ErsHarvestedMguk + s.ErsHarvestedMguh) / 1e6:F2}/{s.ErsHarvestLimitPerLap / 1e6:F2} MJ");
        }

        var aero = new List<string>();
        if (t2 != null)
        {
            aero.Add($"Active aero: mode {t2.ActiveAeroMode}{(t2.ActiveAeroAvailable != 0 ? $" {Green}available{Reset}" : "")}");
            if (t2.OvertakeActive != 0) aero.Add($"{Magenta}{Bold}OVERTAKE ACTIVE{Reset}");
            else if (t2.OvertakeAvailable != 0) aero.Add($"{Magenta}Overtake ready{Reset}");
            if (t2.DrivingWrongWay) aero.Add($"{Red}{Bold}WRONG WAY{Reset}");
        }
        if (t.Drs != 0) aero.Add($"{Green}{Bold}DRS{Reset}");
        if (aero.Count > 0) L.Add("AERO  " + string.Join("   ", aero));

        if (s != null)
        {
            L.Add($"FUEL  {s.FuelInTank:F1} kg ({s.FuelRemainingLaps:+0.0;-0.0} laps)   " +
                  $"TYRE {CompoundColor(s.VisualCompound)}{Names.Compound(s.VisualCompound)}{Reset} {s.TyresAgeLaps} laps   " +
                  $"BB {s.FrontBrakeBias}%   {(s.PitLimiter != 0 ? Yellow + "PIT LIMITER" + Reset : "")}");
        }
        L.Add("");

        // Wheel arrays are ordered RL, RR, FL, FR.
        L.Add($"{Dim}            FL      FR   │       RL      RR{Reset}");
        L.Add($"surface  {Temp(t.TyreSurfaceTemps[2])}  {Temp(t.TyreSurfaceTemps[3])}   │  {Temp(t.TyreSurfaceTemps[0])}  {Temp(t.TyreSurfaceTemps[1])}");
        L.Add($"inner    {Temp(t.TyreInnerTemps[2])}  {Temp(t.TyreInnerTemps[3])}   │  {Temp(t.TyreInnerTemps[0])}  {Temp(t.TyreInnerTemps[1])}");
        L.Add($"brakes   {t.BrakeTemps[2],5}°  {t.BrakeTemps[3],5}°   │  {t.BrakeTemps[0],5}°  {t.BrakeTemps[1],5}°");
        L.Add($"psi      {t.TyrePressures[2],6:F1}  {t.TyrePressures[3],6:F1}   │  {t.TyrePressures[0],6:F1}  {t.TyrePressures[1],6:F1}");
        L.Add($"engine   {t.EngineTemp}°C");
        L.Add("");

        if (logger?.SessionDir != null)
            L.Add($"{Dim}Logging {logger.LapsWritten} lap(s) → {logger.SessionDir}" +
                  (logger.LastWrittenFile != null ? $"  (last: {logger.LastWrittenFile})" : "") + Reset);
        L.Add($"{Dim}packets {hub.PacketCount}  dropped {hub.DroppedCount}   Ctrl+C to quit{Reset}");
        return L;
    }

    private static string Bar(float v, int width, string color)
    {
        v = Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1);
        int filled = (int)MathF.Round(v * width);
        return $"[{color}{new string('█', filled)}{Reset}{Dim}{new string('░', width - filled)}{Reset}]";
    }

    private static string SteerBar(float steer, int width)
    {
        steer = Math.Clamp(float.IsFinite(steer) ? steer : 0, -1, 1);
        int mid = width / 2;
        int pos = mid + (int)MathF.Round(steer * mid);
        var chars = Enumerable.Repeat('-', width).ToArray();
        chars[mid] = '|';
        chars[Math.Clamp(pos, 0, width - 1)] = '●';
        return $"[{new string(chars)}]";
    }

    private static string Temp(byte c)
    {
        string color = c < 80 ? Blue : c <= 105 ? Green : c <= 115 ? Yellow : Red;
        return $"{color}{c,5}°{Reset}";
    }

    private static string Gap(uint ms, byte position) =>
        position <= 1 || ms == 0 ? "--" : $"+{ms / 1000.0:F3}";

    private static string ErsMode(byte m) => m switch
    {
        0 => "None", 1 => "Medium", 2 => "Hotlap", 3 => "Overtake", _ => m.ToString(),
    };

    private static string CompoundColor(byte visual) => visual switch
    {
        16 => Red, 17 => Yellow, 18 => "\u001b[37m", 7 => Green, 8 => Blue, _ => "",
    };
}
