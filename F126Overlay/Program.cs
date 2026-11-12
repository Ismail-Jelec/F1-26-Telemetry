using System.Net;
using System.Net.Sockets;
using System.Windows;
using F126Telemetry;

namespace F126Overlay;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var settings = Settings.Load();
        var logger = settings.LogLaps ? new LapLogger(Settings.LapLogDir) : null;
        var hub = new TelemetryHub(logger);
        using var cts = new CancellationTokenSource();

        UdpClient? udp = null;
        string? error = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, settings.Port));
        }
        catch (SocketException ex)
        {
            error = $"Could not listen on UDP {settings.Port}: {ex.Message}. Is another app using that port?";
        }

        if (udp != null)
        {
            var client = udp;
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var result = await client.ReceiveAsync(cts.Token);
                        hub.Handle(result.Buffer);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { /* keep listening */ }
                }
            });
        }

        if (args.Contains("--demo") && udp != null)
            Task.Run(() => DemoSender.Run(settings.Port, cts.Token));

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new ControlWindow(hub, settings, error, cts.Token));

        cts.Cancel();
        udp?.Dispose();
        settings.Save();
    }
}
