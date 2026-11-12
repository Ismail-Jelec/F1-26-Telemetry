using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace F126Telemetry;

/// <summary>
/// Sends fake 2026-format packets to localhost so the app can be tried without the game running.
/// Simulates a 5 km lap of roughly 80 seconds for the player car in slot 0.
/// </summary>
public static class DemoSender
{
    private const float TrackLength = 5000f;

    public static void Run(int port, CancellationToken ct)
    {
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);
        var rng = new Random(26);
        ulong uid = (ulong)rng.NextInt64();
        uint frame = 0;
        float sessionTime = 0, lapDist = -150f, totalDist = 0, ers = 3_000_000f, fuel = 30f;
        int lapNum = 1;
        uint lapTimeMs = 0, lastLapMs = 0, s1 = 0, s2 = 0;
        const float dt = 1f / 60f;

        while (!ct.IsCancellationRequested)
        {
            // Speed profile: fast straights, three slow corners per lap.
            float phase = Math.Max(0, lapDist) / TrackLength * MathF.Tau;
            float corner = MathF.Pow(MathF.Max(0, MathF.Cos(phase * 3)), 8); // 1 at corner apex
            float speed = 310f - 220f * corner + (float)rng.NextDouble() * 2f;
            float throttle = Math.Clamp(1f - corner * 1.4f, 0, 1);
            float brake = corner > 0.35f && corner < 0.9f ? Math.Clamp((corner - 0.35f) * 2.2f, 0, 1) : 0;
            float steer = MathF.Sin(phase * 3) * corner;
            sbyte gear = (sbyte)Math.Clamp((int)(speed / 40f) + 1, 1, 8);
            ushort rpm = (ushort)(9000 + (speed % 40f) / 40f * 3000);

            float metres = speed / 3.6f * dt;
            lapDist += metres; totalDist += metres; sessionTime += dt;
            if (lapDist >= 0) lapTimeMs += (uint)(dt * 1000);
            ers = Math.Clamp(ers + (brake > 0 ? 40_000f : -9_000f * throttle), 0, 4_000_000f);
            fuel = Math.Max(0, fuel - 0.0004f);

            if (s1 == 0 && lapDist > TrackLength / 3) s1 = lapTimeMs;
            if (s2 == 0 && lapDist > TrackLength * 2 / 3) s2 = lapTimeMs - s1;
            if (lapDist >= TrackLength)
            {
                lapDist -= TrackLength; lastLapMs = lapTimeMs; lapTimeMs = 0; s1 = s2 = 0; lapNum++;
            }
            byte sector = (byte)(lapDist < TrackLength / 3 ? 0 : lapDist < TrackLength * 2 / 3 ? 1 : 2);

            frame++;
            var hdr = (PacketId id) => new Header(id, uid, sessionTime, frame);

            if (frame % 60 == 1) Send(udp, target, BuildSession(hdr(PacketId.Session)));
            Send(udp, target, BuildLap(hdr(PacketId.LapData), lastLapMs, lapTimeMs, s1, s2, lapDist, totalDist, lapNum, sector));
            Send(udp, target, BuildTelemetry(hdr(PacketId.CarTelemetry), (ushort)speed, throttle, steer, brake, gear, rpm));
            Send(udp, target, BuildStatus(hdr(PacketId.CarStatus), ers, fuel));
            Send(udp, target, BuildTelemetry2(hdr(PacketId.CarTelemetry2), throttle > 0.95f));

            Thread.Sleep(16);
        }
    }

    private readonly record struct Header(PacketId Id, ulong Uid, float Time, uint Frame);

    private static void Send(UdpClient udp, IPEndPoint target, byte[] data) => udp.Send(data, data.Length, target);

    private sealed class Writer(int size)
    {
        public readonly byte[] Buf = new byte[size];
        public int Pos;
        public Writer U8(int v) { Buf[Pos++] = (byte)v; return this; }
        public Writer U16(int v) { BinaryPrimitives.WriteUInt16LittleEndian(Buf.AsSpan(Pos), (ushort)v); Pos += 2; return this; }
        public Writer U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Buf.AsSpan(Pos), v); Pos += 4; return this; }
        public Writer U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(Buf.AsSpan(Pos), v); Pos += 8; return this; }
        public Writer F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(Buf.AsSpan(Pos), v); Pos += 4; return this; }
        public Writer WriteHeader(Header h) =>
            U16(Spec.PacketFormat).U8(25).U8(1).U8(0).U8(1).U8((int)h.Id)
            .U64(h.Uid).F32(h.Time).U32(h.Frame).U32(h.Frame).U8(0).U8(255);
        public Writer At(int pos) { Pos = pos; return this; }
    }

    private static byte[] BuildSession(Header h) =>
        new Writer(Spec.SessionPacketSize).WriteHeader(h)
            .U8(1).U8(34).U8(22).U8(10).U16((int)TrackLength).U8(18).U8(7).Buf; // Time Trial, Silverstone

    private static byte[] BuildLap(Header h, uint last, uint cur, uint s1, uint s2, float dist, float total, int lapNum, byte sector)
    {
        var w = new Writer(Spec.LapDataPacketSize).WriteHeader(h)
            .U32(last).U32(cur)
            .U16((int)(s1 % 60000)).U8((int)(s1 / 60000))
            .U16((int)(s2 % 60000)).U8((int)(s2 / 60000))
            .U16(0).U8(0).U16(0).U8(0)
            .F32(dist).F32(total).F32(0)
            .U8(1).U8(lapNum).U8(0).U8(0).U8(sector).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0).U8(1)
            .U8(4).U8(2).U8(0).U16(0).U16(0).U8(0).F32(320f).U8(1);
        return w.Buf;
    }

    private static byte[] BuildTelemetry(Header h, ushort speed, float thr, float steer, float brk, sbyte gear, ushort rpm)
    {
        var w = new Writer(Spec.CarTelemetryPacketSize).WriteHeader(h)
            .U16(speed).F32(thr).F32(steer).F32(brk).U8(0).U8(gear).U16(rpm).U8(0)
            .U8(Math.Clamp((rpm - 9000) / 30, 0, 100)).U16(0);
        foreach (var b in new[] { 520, 530, 610, 600 }) w.U16(b + (int)(brk * 300));
        foreach (var s in new[] { 92, 94, 98, 101 }) w.U8(s);
        foreach (var i in new[] { 99, 100, 103, 104 }) w.U8(i);
        w.U8(105);
        foreach (var p in new[] { 21.8f, 21.9f, 23.1f, 23.2f }) w.F32(p);
        w.U8(0).U8(0).U8(0).U8(0);
        w.At(Spec.HeaderSize + Spec.MaxCars * Spec.CarTelemetrySize).U8(255).U8(255).U8(0);
        return w.Buf;
    }

    private static byte[] BuildStatus(Header h, float ers, float fuel) =>
        new Writer(Spec.CarStatusPacketSize).WriteHeader(h)
            .U8(0).U8(0).U8(1).U8(56).U8(0).F32(fuel).F32(110f).F32(1.4f)
            .U16(13000).U16(4000).U8(8).U8(0).U16(0).U8(17).U8(17).U8(6).U8(1)
            .F32(400_000f).F32(350_000f).F32(ers).U8(1)
            .F32(1_200_000f).F32(0f).F32(4_000_000f).F32(2_100_000f).U8(0).Buf;

    private static byte[] BuildTelemetry2(Header h, bool overtake) =>
        new Writer(Spec.CarTelemetry2PacketSize).WriteHeader(h)
            .U8(overtake ? 1 : 0).U8(1).U16(0).U8(1).U8(overtake ? 1 : 0).U16(0).U8(1).U8(0).Buf;
}
