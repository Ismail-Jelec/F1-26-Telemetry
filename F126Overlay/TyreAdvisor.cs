using F126Telemetry;

namespace F126Overlay;

public enum Conditions { Dry, Inter, Wet }

/// <summary>What the tyre strategy overlay shows. All of it is an estimate from live data.</summary>
public sealed record TyreAdvice(
    bool HasData,
    byte FittedVisual, int TyreAge, float[] Wear, float MaxWear, double? WearPerLap, double? LapsLeftOnTyre,
    byte WeatherNow, int RainNow, sbyte TrackTemp, Conditions ConditionsNow, WeatherSample[] Forecast,
    byte OptimalNowVisual, bool ChangeNow, string NowReason,
    int? NextStopLap, byte NextStopVisual, TyreSet? NextStopSet, string NextStopReason,
    string? WeatherChange, int CurrentLap, int TotalLaps)
{
    public static readonly TyreAdvice Empty = new(false, 0, 0, new float[4], 0, null, null, 0, 0, 0, Conditions.Dry, [],
        0, false, "Waiting for data", null, 0, null, "", null, 0, 0);
}

public static class TyreAdvisor
{
    public const byte Soft = 16, Medium = 17, Hard = 18, Inter = 7, Wet = 8;

    public static Conditions ConditionsFor(byte weather) => weather switch
    {
        <= 2 => Conditions.Dry,
        3 => Conditions.Inter,
        _ => Conditions.Wet,
    };

    public static Conditions ConditionsOfTyre(byte visual) => visual switch
    {
        Inter => Conditions.Inter,
        Wet => Conditions.Wet,
        _ => Conditions.Dry,
    };

    /// <summary>Builds advice from the hub. Call while holding hub.Sync.</summary>
    public static TyreAdvice Advise(TelemetryHub hub, double pitWearThreshold)
    {
        var lap = hub.Lap;
        var status = hub.Status;
        var session = hub.Session;
        var damage = hub.Damage;
        if (lap == null || status == null || session == null) return TyreAdvice.Empty;

        float[] wear = damage != null ? (float[])damage.TyresWear.Clone() : new float[4];
        float maxWear = damage?.MaxWear ?? 0;
        double? rate = hub.WearPerLap();
        double? lapsLeft = rate is > 0 ? Math.Max(0, (pitWearThreshold - maxWear) / rate.Value) : null;

        int currentLap = lap.CurrentLapNum;
        int totalLaps = session.TotalLaps;
        bool race = session.SessionType is >= 15 and <= 17 && totalLaps > 0;
        double lapMs = lap.LastLapTimeMs > 0 ? lap.LastLapTimeMs : 95_000;

        // Forecast for this session only, soonest first.
        var forecast = session.Forecast.Where(f => f.SessionType == session.SessionType)
            .OrderBy(f => f.TimeOffsetMin).ToArray();
        int rainNow = forecast.Length > 0 ? forecast[0].RainPercent : 0;

        // Conditions now: current weather, but look 5 minutes ahead so you're not caught out.
        var nowCond = ConditionsFor(session.Weather);
        var soon = forecast.FirstOrDefault(f => f.TimeOffsetMin is > 0 and <= 5);
        if (soon.TimeOffsetMin > 0 && ConditionsFor(soon.Weather) > nowCond) nowCond = ConditionsFor(soon.Weather);

        var fittedCond = ConditionsOfTyre(status.VisualCompound);
        bool changeNow = fittedCond != nowCond;
        byte optimalNow = nowCond switch
        {
            Conditions.Inter => Inter,
            Conditions.Wet => Wet,
            _ => fittedCond == Conditions.Dry ? status.VisualCompound : PickDryForRemaining(hub.Tyres, race ? totalLaps - currentLap + 1 : 0)?.VisualCompound ?? Medium,
        };
        string nowReason = changeNow
            ? nowCond switch
            {
                Conditions.Inter => "Rain on track: switch to intermediates",
                Conditions.Wet => "Heavy rain: switch to full wets",
                _ => "Track is dry: switch to slicks",
            }
            : maxWear >= pitWearThreshold ? $"Tyres past {pitWearThreshold:0}% wear: box soon" : "Right tyre for the conditions";

        // First forecast change of conditions.
        string? weatherChange = null;
        double? changeInLaps = null;
        Conditions? changeTo = null;
        foreach (var f in forecast)
        {
            var c = ConditionsFor(f.Weather);
            if (f.TimeOffsetMin == 0 || c == nowCond) continue;
            changeTo = c;
            changeInLaps = f.TimeOffsetMin * 60_000.0 / lapMs;
            weatherChange = c switch
            {
                Conditions.Dry => $"Drying in ~{f.TimeOffsetMin} min",
                Conditions.Inter => $"Rain in ~{f.TimeOffsetMin} min ({f.RainPercent}%)",
                _ => $"Heavy rain in ~{f.TimeOffsetMin} min ({f.RainPercent}%)",
            };
            break;
        }

        // Next stop: whichever comes first, worn tyres, a weather change, or the pit window closing.
        int? stopLap = null;
        string stopReason = "";
        Conditions stopCond = nowCond;
        if (changeNow) { stopLap = currentLap; stopReason = "Box this lap"; }
        if (lapsLeft is double l)
        {
            int byWear = currentLap + (int)Math.Floor(l);
            if (stopLap == null || byWear < stopLap) { stopLap = byWear; stopReason = $"Tyres reach {pitWearThreshold:0}% wear"; }
        }
        if (changeInLaps is double cl && changeTo is Conditions to)
        {
            int byWeather = currentLap + (int)Math.Round(cl);
            if (stopLap == null || byWeather <= stopLap)
            {
                stopLap = byWeather;
                stopCond = to;
                stopReason = to == Conditions.Dry ? "Track drying" : "Rain expected";
            }
        }
        if (race && session.PitWindowLatestLap > 0 && session.PitWindowLatestLap >= currentLap
            && (stopLap == null || session.PitWindowLatestLap < stopLap))
        {
            stopLap = session.PitWindowLatestLap;
            stopReason = "End of pit window";
        }
        if (race && stopLap > totalLaps) { stopLap = null; stopReason = "Should make it to the end"; }

        byte stopVisual;
        TyreSet? stopSet = null;
        switch (stopCond)
        {
            case Conditions.Inter: stopSet = PickWet(hub.Tyres, Inter); stopVisual = Inter; break;
            case Conditions.Wet: stopSet = PickWet(hub.Tyres, Wet); stopVisual = Wet; break;
            default:
                int remaining = race && stopLap != null ? totalLaps - stopLap.Value + 1 : 0;
                stopSet = PickDryForRemaining(hub.Tyres, remaining);
                stopVisual = stopSet?.VisualCompound ?? (remaining > 25 ? Hard : remaining > 12 || remaining == 0 ? Medium : Soft);
                break;
        }

        return new TyreAdvice(true, status.VisualCompound, status.TyresAgeLaps, wear, maxWear, rate, lapsLeft,
            session.Weather, rainNow, session.TrackTemp, nowCond, forecast, optimalNow, changeNow, nowReason,
            stopLap, stopVisual, stopSet, stopReason, weatherChange, currentLap, totalLaps);
    }

    /// <summary>
    /// Best available dry set for the laps that are left: the fastest set that lasts to the end,
    /// otherwise the longest-lasting one. Laps 0 means "unknown" and picks the fastest.
    /// </summary>
    private static TyreSet? PickDryForRemaining(TyreSets? tyres, int lapsRemaining)
    {
        if (tyres == null) return null;
        var dry = tyres.Sets.Where(t => t.Available && !t.Fitted && !t.IsWet).ToArray();
        if (dry.Length == 0) return null;
        if (lapsRemaining <= 0) return dry.OrderBy(t => t.LapDeltaMs).ThenBy(t => t.Wear).First();

        var lasting = dry.Where(t => t.UsableLife >= lapsRemaining).ToArray();
        return lasting.Length > 0
            ? lasting.OrderBy(t => t.LapDeltaMs).ThenBy(t => t.Wear).First()
            : dry.OrderByDescending(t => t.UsableLife).ThenBy(t => t.LapDeltaMs).First();
    }

    private static TyreSet? PickWet(TyreSets? tyres, byte visual) =>
        tyres?.Sets.Where(t => t.Available && !t.Fitted && t.VisualCompound == visual).OrderBy(t => t.Wear).Cast<TyreSet?>().FirstOrDefault();
}
