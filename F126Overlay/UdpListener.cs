using System.Net;
using System.Net.Sockets;
using F126Telemetry;

namespace F126Overlay;

/// <summary>Owns the UDP socket and can move it to another port while the app is running.</summary>
public sealed class UdpListener(TelemetryHub hub) : IDisposable
{
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsListening => _udp != null;

    /// <summary>Starts listening on <paramref name="port"/>. Returns an error message, or null on success.</summary>
    public string? Start(int port)
    {
        if (port < 1 || port > 65535) return "Port must be between 1 and 65535.";

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            return $"Can't listen on UDP {port}: {ex.Message} Is another app (SimHub, F1Laps...) using it?";
        }

        Stop();
        _udp = udp;
        _cts = new CancellationTokenSource();
        Port = port;

        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(token);
                    hub.Handle(result.Buffer);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { /* e.g. ICMP port unreachable; keep listening */ }
            }
        });
        return null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts = null;
    }

    public void Dispose() => Stop();
}
