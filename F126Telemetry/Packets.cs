using System.Buffers.Binary;

namespace F126Telemetry;

// Layouts follow EA's "Data Output from F1 25: 2026 Season Pack" (UDP format 2026).
// All values are little endian. 24 car slots per array.

public enum PacketId : byte
{
    Motion = 0, Session = 1, LapData = 2, Event = 3, Participants = 4, CarSetups = 5,
    CarTelemetry = 6, CarStatus = 7, FinalClassification = 8, LobbyInfo = 9, CarDamage = 10,
    SessionHistory = 11, TyreSets = 12, MotionEx = 13, TimeTrial = 14, LapPositions = 15,
    CarTelemetry2 = 16,
}

public static class Spec
{
    public const ushort PacketFormat = 2026;
    public const int MaxCars = 24;
    public const int HeaderSize = 29;

    public const int CarTelemetrySize = 59;
    public const int LapDataSize = 57;
    public const int CarStatusSize = 59;
    public const int CarTelemetry2Size = 10;

    public const int CarTelemetryPacketSize = 1448;
    public const int LapDataPacketSize = 1399;
    public const int CarStatusPacketSize = 1445;
    public const int SessionPacketSize = 926;
    public const int CarTelemetry2PacketSize = 269;

    public const int CarDamageSize = 46;
    public const int CarDamagePacketSize = 1133;   // 29 + 24 * 46
    public const int TyreSetSize = 10;
    public const int MaxTyreSets = 20;
    public const int TyreSetsPacketSize = 231;     // 29 + 1 + 20 * 10 + 1
    public const int MaxForecastSamples = 64;
}

/// <summary>Little-endian cursor over a byte array.</summary>
public ref struct PacketReader(ReadOnlySpan<byte> data, int offset = 0)
{
    private readonly ReadOnlySpan<byte> _data = data;
    public int Pos = offset;

    public byte U8() => _data[Pos++];
    public sbyte I8() => (sbyte)_data[Pos++];
    public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Pos..]); Pos += 2; return v; }
    public short I16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_data[Pos..]); Pos += 2; return v; }
    public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Pos..]); Pos += 4; return v; }
    public ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[Pos..]); Pos += 8; return v; }
    public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_data[Pos..]); Pos += 4; return v; }
    public void Skip(int n) => Pos += n;
}

public readonly record struct PacketHeader(
    ushort PacketFormat, byte GameYear, byte GameMajorVersion, byte GameMinorVersion,
    byte PacketVersion, PacketId PacketId, ulong SessionUid, float SessionTime,
    uint FrameIdentifier, uint OverallFrameIdentifier, byte PlayerCarIndex, byte SecondaryPlayerCarIndex)
{
    public static PacketHeader Read(ReadOnlySpan<byte> d)
    {
        var r = new PacketReader(d);
        return new PacketHeader(r.U16(), r.U8(), r.U8(), r.U8(), r.U8(), (PacketId)r.U8(),
            r.U64(), r.F32(), r.U32(), r.U32(), r.U8(), r.U8());
    }
}

public sealed class CarTelemetry
{
    public ushort SpeedKph;
    public float Throttle, Steer, Brake;     // 0..1, -1..1, 0..1
    public byte Clutch;                      // 0..100
    public sbyte Gear;                       // -1 R, 0 N, 1-8
    public ushort EngineRpm;
    public byte Drs;                         // 0 off, 1 on
    public byte RevLightsPercent;
    public ushort RevLightsBitValue;
    public readonly ushort[] BrakeTemps = new ushort[4];  // RL, RR, FL, FR
    public readonly byte[] TyreSurfaceTemps = new byte[4];
    public readonly byte[] TyreInnerTemps = new byte[4];
    public byte EngineTemp;
    public readonly float[] TyrePressures = new float[4];
    public readonly byte[] SurfaceType = new byte[4];
    public sbyte SuggestedGear;              // 0 = none

    /// <summary>Reads the given car's entry from a Car Telemetry packet (id 6).</summary>
    public static CarTelemetry Read(ReadOnlySpan<byte> d, int carIndex)
    {
        var r = new PacketReader(d, Spec.HeaderSize + carIndex * Spec.CarTelemetrySize);
        var t = new CarTelemetry
        {
            SpeedKph = r.U16(), Throttle = r.F32(), Steer = r.F32(), Brake = r.F32(),
            Clutch = r.U8(), Gear = r.I8(), EngineRpm = r.U16(), Drs = r.U8(),
            RevLightsPercent = r.U8(), RevLightsBitValue = r.U16(),
        };
        for (int i = 0; i < 4; i++) t.BrakeTemps[i] = r.U16();
        for (int i = 0; i < 4; i++) t.TyreSurfaceTemps[i] = r.U8();
        for (int i = 0; i < 4; i++) t.TyreInnerTemps[i] = r.U8();
        t.EngineTemp = r.U8();
        for (int i = 0; i < 4; i++) t.TyrePressures[i] = r.F32();
        for (int i = 0; i < 4; i++) t.SurfaceType[i] = r.U8();

        // Trailing fields after the 24-car array: mfdPanel, mfdPanelSecondary, suggestedGear
        t.SuggestedGear = (sbyte)d[Spec.HeaderSize + Spec.MaxCars * Spec.CarTelemetrySize + 2];
        return t;
    }
}

public sealed class LapInfo
{
    public uint LastLapTimeMs, CurrentLapTimeMs;
    public uint Sector1Ms, Sector2Ms;        // combined minutes + ms parts; 0 if not set yet
    public uint DeltaToCarInFrontMs, DeltaToLeaderMs;
    public float LapDistance, TotalDistance, SafetyCarDelta;
    public byte CarPosition, CurrentLapNum, PitStatus, NumPitStops, Sector;  // Sector: 0,1,2
    public bool CurrentLapInvalid;
    public byte Penalties, TotalWarnings, CornerCuttingWarnings;
    public byte GridPosition, DriverStatus, ResultStatus;
    public float SpeedTrapFastest;

    /// <summary>Reads the given car's entry from a Lap Data packet (id 2).</summary>
    public static LapInfo Read(ReadOnlySpan<byte> d, int carIndex)
    {
        var r = new PacketReader(d, Spec.HeaderSize + carIndex * Spec.LapDataSize);
        var l = new LapInfo { LastLapTimeMs = r.U32(), CurrentLapTimeMs = r.U32() };
        ushort s1Ms = r.U16(); byte s1Min = r.U8();
        ushort s2Ms = r.U16(); byte s2Min = r.U8();
        ushort frontMs = r.U16(); byte frontMin = r.U8();
        ushort leaderMs = r.U16(); byte leaderMin = r.U8();
        l.Sector1Ms = s1Min * 60_000u + s1Ms;
        l.Sector2Ms = s2Min * 60_000u + s2Ms;
        l.DeltaToCarInFrontMs = frontMin * 60_000u + frontMs;
        l.DeltaToLeaderMs = leaderMin * 60_000u + leaderMs;
        l.LapDistance = r.F32(); l.TotalDistance = r.F32(); l.SafetyCarDelta = r.F32();
        l.CarPosition = r.U8(); l.CurrentLapNum = r.U8(); l.PitStatus = r.U8(); l.NumPitStops = r.U8();
        l.Sector = r.U8(); l.CurrentLapInvalid = r.U8() != 0; l.Penalties = r.U8();
        l.TotalWarnings = r.U8(); l.CornerCuttingWarnings = r.U8();
        r.Skip(2); // unserved drive-through / stop-go
        l.GridPosition = r.U8(); l.DriverStatus = r.U8(); l.ResultStatus = r.U8();
        r.Skip(1 + 2 + 2 + 1); // pitLaneTimerActive, pitLaneTimeInLane, pitStopTimer, pitStopShouldServePen
        l.SpeedTrapFastest = r.F32();
        return l;
    }
}

public sealed class CarStatus
{
    public byte TractionControl, AntiLockBrakes, FuelMix, FrontBrakeBias, PitLimiter;
    public float FuelInTank, FuelCapacity, FuelRemainingLaps;
    public ushort MaxRpm, IdleRpm;
    public byte MaxGears, DrsAllowed;
    public ushort DrsActivationDistance;
    public byte ActualCompound, VisualCompound, TyresAgeLaps;
    public sbyte FiaFlag;                    // -1 invalid, 0 none, 1 green, 2 blue, 3 yellow
    public float EnginePowerIce, EnginePowerMguk, ErsStoreEnergy;
    public byte ErsDeployMode;
    public float ErsHarvestedMguk, ErsHarvestedMguh, ErsHarvestLimitPerLap, ErsDeployedThisLap;

    /// <summary>Reads the given car's entry from a Car Status packet (id 7).</summary>
    public static CarStatus Read(ReadOnlySpan<byte> d, int carIndex)
    {
        var r = new PacketReader(d, Spec.HeaderSize + carIndex * Spec.CarStatusSize);
        return new CarStatus
        {
            TractionControl = r.U8(), AntiLockBrakes = r.U8(), FuelMix = r.U8(), FrontBrakeBias = r.U8(),
            PitLimiter = r.U8(), FuelInTank = r.F32(), FuelCapacity = r.F32(), FuelRemainingLaps = r.F32(),
            MaxRpm = r.U16(), IdleRpm = r.U16(), MaxGears = r.U8(), DrsAllowed = r.U8(),
            DrsActivationDistance = r.U16(), ActualCompound = r.U8(), VisualCompound = r.U8(),
            TyresAgeLaps = r.U8(), FiaFlag = r.I8(), EnginePowerIce = r.F32(), EnginePowerMguk = r.F32(),
            ErsStoreEnergy = r.F32(), ErsDeployMode = r.U8(), ErsHarvestedMguk = r.F32(),
            ErsHarvestedMguh = r.F32(), ErsHarvestLimitPerLap = r.F32(), ErsDeployedThisLap = r.F32(),
        };
    }
}

public sealed class CarTelemetry2
{
    public byte ActiveAeroMode, ActiveAeroAvailable;
    public ushort ActiveAeroActivationDistance;
    public byte OvertakeAvailable, OvertakeActive;
    public ushort OvertakeActivationDistance;
    public bool Regs2026, DrivingWrongWay;

    /// <summary>Reads the given car's entry from a Car Telemetry 2 packet (id 16).</summary>
    public static CarTelemetry2 Read(ReadOnlySpan<byte> d, int carIndex)
    {
        var r = new PacketReader(d, Spec.HeaderSize + carIndex * Spec.CarTelemetry2Size);
        return new CarTelemetry2
        {
            ActiveAeroMode = r.U8(), ActiveAeroAvailable = r.U8(), ActiveAeroActivationDistance = r.U16(),
            OvertakeAvailable = r.U8(), OvertakeActive = r.U8(), OvertakeActivationDistance = r.U16(),
            Regs2026 = r.U8() != 0, DrivingWrongWay = r.U8() != 0,
        };
    }
}

public readonly record struct WeatherSample(
    byte SessionType, byte TimeOffsetMin, byte Weather, sbyte TrackTemp, sbyte AirTemp, byte RainPercent);

public sealed class SessionInfo
{
    public byte Weather;                 // 0 clear, 1 light cloud, 2 overcast, 3 light rain, 4 heavy rain, 5 storm
    public sbyte TrackTemp, AirTemp;
    public byte TotalLaps;
    public ushort TrackLength;
    public byte SessionType;
    public sbyte TrackId;
    public ushort SessionTimeLeft;
    public byte SafetyCarStatus;
    public byte ForecastAccuracy;        // 0 perfect, 1 approximate
    public byte PitWindowIdealLap, PitWindowLatestLap, PitRejoinPosition;
    public WeatherSample[] Forecast = [];

    /// <summary>Reads a Session packet (id 1) up to the pit window fields.</summary>
    public static SessionInfo Read(ReadOnlySpan<byte> d)
    {
        var r = new PacketReader(d, Spec.HeaderSize);
        var s = new SessionInfo
        {
            Weather = r.U8(), TrackTemp = r.I8(), AirTemp = r.I8(), TotalLaps = r.U8(),
            TrackLength = r.U16(), SessionType = r.U8(), TrackId = r.I8(),
        };
        r.Skip(1);                       // formula
        s.SessionTimeLeft = r.U16();
        r.Skip(2 + 1 + 1 + 1 + 1 + 1);   // duration, pit speed limit, paused, spectating, spectator idx, SLI Pro
        r.Skip(1 + 21 * 5);              // marshal zones
        s.SafetyCarStatus = r.U8();
        r.Skip(1);                       // network game
        int n = Math.Min((int)r.U8(), Spec.MaxForecastSamples);
        var samples = new WeatherSample[n];
        int start = r.Pos;
        for (int i = 0; i < n; i++)
        {
            var w = new PacketReader(d, start + i * 8);
            byte type = w.U8(), offset = w.U8(), weather = w.U8();
            sbyte track = w.I8(); w.Skip(1);
            sbyte air = w.I8(); w.Skip(1);
            samples[i] = new WeatherSample(type, offset, weather, track, air, w.U8());
        }
        s.Forecast = samples;
        r.Pos = start + Spec.MaxForecastSamples * 8;
        s.ForecastAccuracy = r.U8();
        r.Skip(1 + 4 + 4 + 4);           // AI difficulty, link identifiers
        s.PitWindowIdealLap = r.U8();
        s.PitWindowLatestLap = r.U8();
        s.PitRejoinPosition = r.U8();
        return s;
    }
}

public sealed class CarDamage
{
    public readonly float[] TyresWear = new float[4];   // percent, RL RR FL FR
    public readonly byte[] TyresDamage = new byte[4];
    public readonly byte[] TyreBlisters = new byte[4];

    public float MaxWear => Math.Max(Math.Max(TyresWear[0], TyresWear[1]), Math.Max(TyresWear[2], TyresWear[3]));

    /// <summary>Reads the given car's entry from a Car Damage packet (id 10).</summary>
    public static CarDamage Read(ReadOnlySpan<byte> d, int carIndex)
    {
        var r = new PacketReader(d, Spec.HeaderSize + carIndex * Spec.CarDamageSize);
        var c = new CarDamage();
        for (int i = 0; i < 4; i++) c.TyresWear[i] = r.F32();
        for (int i = 0; i < 4; i++) c.TyresDamage[i] = r.U8();
        r.Skip(4); // brakes damage
        for (int i = 0; i < 4; i++) c.TyreBlisters[i] = r.U8();
        return c;
    }
}

public readonly record struct TyreSet(
    int Index, byte ActualCompound, byte VisualCompound, byte Wear, bool Available, byte RecommendedSession,
    byte LifeSpan, byte UsableLife, short LapDeltaMs, bool Fitted)
{
    public bool IsWet => VisualCompound is 7 or 8;
}

public sealed class TyreSets
{
    public byte CarIndex;
    public TyreSet[] Sets = [];
    public byte FittedIndex;

    /// <summary>Reads a Tyre Sets packet (id 12). Only sent for the player's car(s).</summary>
    public static TyreSets Read(ReadOnlySpan<byte> d)
    {
        var r = new PacketReader(d, Spec.HeaderSize);
        var t = new TyreSets { CarIndex = r.U8() };
        var sets = new List<TyreSet>(Spec.MaxTyreSets);
        for (int i = 0; i < Spec.MaxTyreSets; i++)
        {
            byte actual = r.U8(), visual = r.U8(), wear = r.U8(), available = r.U8(), rec = r.U8(), life = r.U8(), usable = r.U8();
            short delta = r.I16();
            byte fitted = r.U8();
            if (visual == 0 && actual == 0) continue; // empty slot
            sets.Add(new TyreSet(i, actual, visual, wear, available != 0, rec, life, usable, delta, fitted != 0));
        }
        t.Sets = sets.ToArray();
        t.FittedIndex = r.U8();
        return t;
    }
}
