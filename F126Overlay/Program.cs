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
        using var listener = new UdpListener(hub);

        string? error = listener.Start(settings.Port);

        if (args.Contains("--demo") && error == null)
            Task.Run(() => DemoSender.Run(settings.Port, cts.Token));

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new ControlWindow(hub, settings, listener, error, cts.Token));

        cts.Cancel();
        settings.Save();
    }
}
