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
    public CarDamage? Damage;
    public TyreSets? Tyres;

    /// <summary>Tyre wear at the start of the current stint, used to estimate wear per lap.</summary>
    public float StintStartWear;
    public float StintStartDistance = float.NaN;
    public byte StintCompound;
    public ulong SessionUid;
    public DateTime LastPacketUtc = DateTime.MinValue;
    public long PacketCount;
    public long DroppedCount;
    public ushort? WrongFormat;   // set if packets arrive in a non-2026 format

    /// <summary>Raised (on the UDP thread) with the button flags that were just pressed down.</summary>
    public event Action<uint>? ButtonsPressed;
    private uint _buttonsHeld;
    private long _buttonsAt;

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

        // Button events are handled on their own: they can arrive in menus (no player car) and the game
        // has been known to send them with an empty session id, which must not reset the lap history.
        if (h.PacketId == PacketId.Event && h.PacketFormat == Spec.PacketFormat && EventPacket.TryReadButtons(data, out uint held))
        {
            uint pressed;
            lock (Sync)
            {
                PacketCount++;
                LastPacketUtc = DateTime.UtcNow;
                // The game doesn't reliably send a "released" event, so a button that stayed "held" in our
                // memory would ignore its next press. Treat the state as released once no button event
                // has arrived for a moment; while a button is really held the game keeps sending events.
                long now = Environment.TickCount64;
                if (now - _buttonsAt > 300) _buttonsHeld = 0;
                _buttonsAt = now;
                pressed = held & ~_buttonsHeld; // only buttons that went from up to down
                _buttonsHeld = held;
            }
            if (pressed != 0) ButtonsPressed?.Invoke(pressed);
            return;
        }

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

                    case PacketId.CarDamage when data.Length >= Spec.CarDamagePacketSize:
                        Damage = CarDamage.Read(data, car);
                        TrackStint();
                        break;

                    case PacketId.TyreSets when data.Length >= Spec.TyreSetsPacketSize:
                        var sets = TyreSets.Read(data);
                        if (sets.CarIndex == car) Tyres = sets;
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

    /// <summary>
    /// Average tyre wear per lap (percent, worst tyre) in the current stint, or null until about half a lap is driven.
    /// Call while holding Sync.
    /// </summary>
    public double? WearPerLap()
    {
        if (Damage == null || Lap == null || Session == null || Session.TrackLength == 0 || float.IsNaN(StintStartDistance)) return null;
        double laps = (Lap.TotalDistance - StintStartDistance) / Session.TrackLength;
        if (laps < 0.5) return null;
        double rate = (Damage.MaxWear - StintStartWear) / laps;
        return rate > 0 ? rate : null;
    }

    private void TrackStint()
    {
        if (Damage == null || Lap == null) return;
        float wear = Damage.MaxWear;
        byte compound = Status?.VisualCompound ?? 0;
        // New stint: first data, a tyre change (wear drops) or a different compound.
        if (float.IsNaN(StintStartDistance) || wear < StintStartWear - 3 || compound != StintCompound
            || Lap.TotalDistance < StintStartDistance)
        {
            StintStartWear = wear;
            StintStartDistance = Lap.TotalDistance;
            StintCompound = compound;
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
        Damage = null;
        Tyres = null;
        StintStartDistance = float.NaN;
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

            // Keep the game's *latest* verdict, not a sticky one: the game clears the flag again after a
            // flashback or a lap restart (common in Time Trial), and the last value before the lap number
            // changes is what the game actually counted.
            _carLapNum[i] = l.CurrentLapNum;
            _carLapInvalid[i] = l.CurrentLapInvalid;
        }
    }
}
