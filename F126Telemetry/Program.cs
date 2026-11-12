using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using F126Telemetry;

// Usage: dotnet run -- [--port 20999] [--log-dir laps] [--no-log] [--demo]
int port = 20999;
string logDir = Path.Combine(AppContext.BaseDirectory, "laps");
bool log = true, demo = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--log-dir" when i + 1 < args.Length: logDir = Path.GetFullPath(args[++i]); break;
        case "--no-log": log = false; break;
        case "--demo": demo = true; break;
        case "-h" or "--help":
            Console.WriteLine("F1 26 telemetry\n  --port N       UDP port (default 20999)\n  --log-dir DIR  where lap CSVs go (default ./laps next to the exe)\n  --no-log       live dashboard only\n  --demo         feed fake data, no game needed");
            return;
    }
}

ConsoleVt.Enable();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var logger = log ? new LapLogger(logDir) : null;
var hub = new TelemetryHub(logger);

UdpClient udp;
try
{
    udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Could not listen on UDP port {port}: {ex.Message}");
    Console.Error.WriteLine("Is another telemetry app (SimHub, F1Laps…) already using it? Try --port with another number.");
    return;
}

var receiver = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var result = await udp.ReceiveAsync(cts.Token);
            hub.Handle(result.Buffer);
        }
        catch (OperationCanceledException) { break; }
        catch (SocketException) { /* e.g. ICMP port unreachable on Windows; keep listening */ }
    }
});

if (demo) _ = Task.Run(() => DemoSender.Run(port, cts.Token));

new Dashboard(hub, logger, port).Run(cts.Token);

udp.Dispose();
await receiver;
Console.WriteLine();
if (logger?.SessionDir != null) Console.WriteLine($"Laps saved in {logger.SessionDir}");

/// <summary>Turns on ANSI colour handling for classic Windows consoles (Windows Terminal already has it).</summary>
static class ConsoleVt
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    public static void Enable()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!OperatingSystem.IsWindows()) return;
        var h = GetStdHandle(-11);
        if (GetConsoleMode(h, out var mode)) SetConsoleMode(h, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }
}
