using System.Globalization;
using System.Text;

namespace F126Telemetry;

/// <summary>
/// Collects player-car samples for the lap in progress and writes one CSV per completed lap,
/// plus a laps.csv summary per session. Handles flashbacks by trimming rewound samples.
/// </summary>
public sealed class LapLogger(string rootDir)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string SampleHeader =
        "session_time,lap_distance_m,lap_time_ms,speed_kph,throttle,brake,steer,gear,rpm,drs," +
        "ers_store_j,ers_deploy_mode,fuel_kg,tyre_surf_rl,tyre_surf_rr,tyre_surf_fl,tyre_surf_fr," +
        "active_aero_mode,overtake_active,sector";

    private readonly List<string> _samples = [];
    private string? _sessionDir;
    private ulong _sessionUid;
    private int _lapNum = -1;
    private uint _s1, _s2;
    private bool _invalid;
    private float _lastDistance = float.MinValue;

    public string? SessionDir => _sessionDir;
    public uint BestLapMs { get; private set; }
    public int LapsWritten { get; private set; }
    public string? LastWrittenFile { get; private set; }

    public void StartSession(ulong uid, string trackName, string sessionName)
    {
        if (uid == _sessionUid && _sessionDir != null) return;
        _sessionUid = uid;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", Inv);
        _sessionDir = Path.Combine(rootDir, $"{stamp}_{Names.Safe(trackName)}_{Names.Safe(sessionName)}");
        Directory.CreateDirectory(_sessionDir);
        File.WriteAllText(Path.Combine(_sessionDir, "laps.csv"),
            "lap,lap_time_ms,lap_time,sector1_ms,sector2_ms,sector3_ms,valid,samples,file\n");
        ResetLap(-1);
        BestLapMs = 0;
    }

    /// <summary>Call with every Lap Data packet for the player car.</summary>
    public void OnLapData(LapInfo lap)
    {
        if (_sessionDir == null) return;
        int num = lap.CurrentLapNum;

        if (_lapNum == -1) { ResetLap(num); }
        else if (num == _lapNum + 1)
        {
            FinishLap(lap.LastLapTimeMs);
            ResetLap(num);
        }
        else if (num != _lapNum)
        {
            // Restart, flashback across the line, or a skipped lap: drop the partial lap.
            ResetLap(num);
        }

        if (lap.Sector1Ms > 0) _s1 = lap.Sector1Ms;
        if (lap.Sector2Ms > 0) _s2 = lap.Sector2Ms;
        _invalid |= lap.CurrentLapInvalid;

        // Flashback within the lap: distance jumps backwards, so trim samples past the new point.
        if (lap.LapDistance >= 0 && lap.LapDistance < _lastDistance - 50f)
            TrimAfter(lap.LapDistance);
        _lastDistance = lap.LapDistance;
    }

    /// <summary>Call with every Car Telemetry packet, using the latest lap/status data.</summary>
    public void AddSample(float sessionTime, CarTelemetry t, LapInfo lap, CarStatus? s, CarTelemetry2? t2)
    {
        if (_sessionDir == null || _lapNum < 0 || lap.LapDistance < 0) return;

        var sb = new StringBuilder(160);
        sb.Append(Inv, $"{sessionTime:F3},{lap.LapDistance:F2},{lap.CurrentLapTimeMs},{t.SpeedKph},");
        sb.Append(Inv, $"{t.Throttle:F3},{t.Brake:F3},{t.Steer:F3},{t.Gear},{t.EngineRpm},{t.Drs},");
        sb.Append(Inv, $"{(s?.ErsStoreEnergy ?? 0):F0},{(s?.ErsDeployMode ?? 0)},{(s?.FuelInTank ?? 0):F2},");
        sb.Append(Inv, $"{t.TyreSurfaceTemps[0]},{t.TyreSurfaceTemps[1]},{t.TyreSurfaceTemps[2]},{t.TyreSurfaceTemps[3]},");
        sb.Append(Inv, $"{(t2?.ActiveAeroMode ?? 0)},{(t2?.OvertakeActive ?? 0)},{lap.Sector + 1}");
        _samples.Add(sb.ToString());
    }

    private void ResetLap(int num)
    {
        _lapNum = num;
        _samples.Clear();
        _s1 = _s2 = 0;
        _invalid = false;
        _lastDistance = float.MinValue;
    }

    private void TrimAfter(float distance)
    {
        _samples.RemoveAll(row =>
        {
            var parts = row.Split(',', 3);
            return float.Parse(parts[1], Inv) > distance;
        });
    }

    private void FinishLap(uint lapTimeMs)
    {
        if (_sessionDir == null || _samples.Count < 20 || lapTimeMs == 0) return;

        var file = $"lap_{_lapNum:00}.csv";
        File.WriteAllText(Path.Combine(_sessionDir, file), SampleHeader + "\n" + string.Join('\n', _samples) + "\n");

        uint s3 = lapTimeMs > _s1 + _s2 && _s1 > 0 && _s2 > 0 ? lapTimeMs - _s1 - _s2 : 0;
        var row = string.Create(Inv,
            $"{_lapNum},{lapTimeMs},{Names.LapTime(lapTimeMs)},{_s1},{_s2},{s3},{(_invalid ? 0 : 1)},{_samples.Count},{file}\n");
        File.AppendAllText(Path.Combine(_sessionDir, "laps.csv"), row);

        if (!_invalid && (BestLapMs == 0 || lapTimeMs < BestLapMs)) BestLapMs = lapTimeMs;
        LapsWritten++;
        LastWrittenFile = file;
    }
}
