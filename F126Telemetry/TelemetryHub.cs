namespace F126Telemetry;

/// <summary>A completed lap by the player.</summary>
public sealed record LapRecord(int Lap, uint TimeMs, bool Valid, float FuelKg);

/// <summary>
/// Decodes incoming datagrams, keeps the player's latest state, lap history, session best
/// and a short input trace, and feeds the (optional) lap logger.
/// </summary>
public sealed class TelemetryHub(LapLogger? logger)
{
    public const int TraceLength = 600; // samples (10 s at 60 Hz)

    public readonly object Sync = new();

    public CarTelemetry? Telemetry;
    public CarTelemetry2? Telemetry2;
    public LapInfo? Lap;
    public CarStatus? Status;
    public SessionInfo? Session;
    public ulong SessionUid;
    public DateTime LastPacketUtc = DateTime.MinValue;
    public long PacketCount;
    public long DroppedCount;
    public ushort? WrongFormat;   // set if packets arrive in a non-2026 format

    /// <summary>Player laps, oldest first.</summary>
    public readonly List<LapRecord> PlayerLaps = [];
    /// <summary>Fastest valid lap by any car this session (0 = none yet).</summary>
    public uint SessionBestMs;
    /// <summary>Fastest valid lap by the player this session (0 = none yet).</summary>
    public uint PersonalBestMs;

    private ulong _historyUid;
    private readonly byte[] _carLapNum = new byte[Spec.MaxCars];
    private readonly bool[] _carLapInvalid = new bool[Spec.MaxCars];

    private readonly float[] _traceThrottle = new float[TraceLength];
    private readonly float[] _traceBrake = new float[TraceLength];
    private int _traceHead, _traceCount;

    public void Handle(ReadOnlySpan<byte> data)
    {
        if (data.Length < Spec.HeaderSize) { DroppedCount++; return; }

        var h = PacketHeader.Read(data);
        lock (Sync)
        {
            PacketCount++;
            LastPacketUtc = DateTime.UtcNow;

            if (h.PacketFormat != Spec.PacketFormat) { WrongFormat = h.PacketFormat; DroppedCount++; return; }
            WrongFormat = null;

            if (h.SessionUid != _historyUid) ResetHistory(h.SessionUid);

            int car = h.PlayerCarIndex;
            if (car >= Spec.MaxCars) return; // 255 = spectating

            try
            {
                switch (h.PacketId)
                {
                    case PacketId.Session when data.Length >= Spec.SessionPacketSize:
                        Session = SessionInfo.Read(data);
                        if (h.SessionUid != SessionUid)
                        {
                            SessionUid = h.SessionUid;
                            logger?.StartSession(h.SessionUid, Names.Track(Session.TrackId), Names.Session(Session.SessionType));
                        }
                        break;

                    case PacketId.LapData when data.Length >= Spec.LapDataPacketSize:
                        TrackLaps(data, car);
                        Lap = LapInfo.Read(data, car);
                        logger?.OnLapData(Lap);
                        break;

                    case PacketId.CarTelemetry when data.Length >= Spec.CarTelemetryPacketSize:
                        Telemetry = CarTelemetry.Read(data, car);
                        AddTrace(Telemetry.Throttle, Telemetry.Brake);
                        if (Lap != null) logger?.AddSample(h.SessionTime, Telemetry, Lap, Status, Telemetry2);
                        break;

                    case PacketId.CarStatus when data.Length >= Spec.CarStatusPacketSize:
                        Status = CarStatus.Read(data, car);
                        break;

                    case PacketId.CarTelemetry2 when data.Length >= Spec.CarTelemetry2PacketSize:
                        Telemetry2 = CarTelemetry2.Read(data, car);
                        break;
                }
            }
            catch (Exception)
            {
                // UDP is lossy and layouts may shift with patches: skip a bad packet, never crash.
                DroppedCount++;
            }
        }
    }

    /// <summary>Copies the input trace (oldest first) into the given arrays. Call while holding Sync.</summary>
    public int CopyTrace(float[] throttle, float[] brake)
    {
        int n = Math.Min(_traceCount, Math.Min(throttle.Length, brake.Length));
        int start = (_traceHead - n + TraceLength) % TraceLength;
        for (int i = 0; i < n; i++)
        {
            int idx = (start + i) % TraceLength;
            throttle[i] = _traceThrottle[idx];
            brake[i] = _traceBrake[idx];
        }
        return n;
    }

    private void AddTrace(float throttle, float brake)
    {
        _traceThrottle[_traceHead] = throttle;
        _traceBrake[_traceHead] = brake;
        _traceHead = (_traceHead + 1) % TraceLength;
        if (_traceCount < TraceLength) _traceCount++;
    }

    private void ResetHistory(ulong uid)
    {
        _historyUid = uid;
        PlayerLaps.Clear();
        SessionBestMs = PersonalBestMs = 0;
        Array.Clear(_carLapNum);
        Array.Clear(_carLapInvalid);
        _traceCount = _traceHead = 0;
    }

    /// <summary>Detects completed laps for every car to keep lap history and the session best.</summary>
    private void TrackLaps(ReadOnlySpan<byte> data, int playerCar)
    {
        for (int i = 0; i < Spec.MaxCars; i++)
        {
            var l = LapInfo.Read(data, i);
            if (l.ResultStatus < 2 || l.CurrentLapNum == 0) continue; // empty slot or inactive car

            byte prev = _carLapNum[i];
            if (prev != 0 && l.CurrentLapNum == prev + 1 && l.LastLapTimeMs > 0)
            {
                bool valid = !_carLapInvalid[i];
                if (valid && (SessionBestMs == 0 || l.LastLapTimeMs < SessionBestMs))
                    SessionBestMs = l.LastLapTimeMs;

                if (i == playerCar)
                {
                    PlayerLaps.Add(new LapRecord(prev, l.LastLapTimeMs, valid, Status?.FuelInTank ?? 0));
                    if (valid && (PersonalBestMs == 0 || l.LastLapTimeMs < PersonalBestMs))
                        PersonalBestMs = l.LastLapTimeMs;
                }
            }

            if (l.CurrentLapNum != prev)
            {
                _carLapNum[i] = l.CurrentLapNum;
                _carLapInvalid[i] = l.CurrentLapInvalid;
            }
            else
            {
                _carLapInvalid[i] |= l.CurrentLapInvalid;
            }
        }
    }
}
