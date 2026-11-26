using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace F126Overlay;

/// <summary>
/// Real frame rate from Intel PresentMon (open source, MIT). PresentMon reads the frames your GPU presents
/// through Windows event tracing, which works for any game and any GPU vendor.
/// Windows only allows that for administrators or members of the "Performance Log Users" group.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    private const string PerformanceLogUsersSid = "S-1-5-32-559";
    private const double WindowSeconds = 10;

    public static string ToolDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F126Overlay", "tools");
    public static string ExePath => Path.Combine(ToolDir, "PresentMon.exe");

    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<(long Tick, double Ms)>> _frames = new(StringComparer.OrdinalIgnoreCase);
    private Process? _process;
    private string? _lastError;

    public bool IsRunning => _process is { HasExited: false };
    public string? LastError { get { lock (_sync) return _lastError; } }

    public static bool IsDownloaded => File.Exists(ExePath);

    /// <summary>True when Windows lets PresentMon read frame events (admin, or in Performance Log Users).</summary>
    public static bool HasPermission
    {
        get
        {
            try
            {
                var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                return principal.IsInRole(WindowsBuiltInRole.Administrator)
                    || principal.IsInRole(new SecurityIdentifier(PerformanceLogUsersSid));
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Adds the current user to "Performance Log Users" (shows a Windows admin prompt).
    /// Takes effect after signing out and in again.
    /// </summary>
    public static bool GrantPermission(out string? error)
    {
        error = null;
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"Add-LocalGroupMember -SID '{PerformanceLogUsersSid}' -Member '{sid}' -ErrorAction SilentlyContinue\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            p?.WaitForExit(30_000);
            return p?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message; // e.g. the admin prompt was cancelled
            return false;
        }
    }

    /// <summary>Downloads the PresentMon console app from its GitHub releases.</summary>
    public static async Task<string?> DownloadAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("F126Overlay");
            var json = await http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=30");
            using var doc = JsonDocument.Parse(json);

            string? url = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.GetProperty("prerelease").GetBoolean()) continue;
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    // The console app is "PresentMon-<version>-x64.exe" (not the capture app installer).
                    if (name.StartsWith("PresentMon-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
                if (url != null) break;
            }
            if (url == null) return "Couldn't find the PresentMon console app in its GitHub releases.";

            Directory.CreateDirectory(ToolDir);
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(ExePath, bytes);
            return null;
        }
        catch (Exception ex)
        {
            return $"Download failed: {ex.Message}";
        }
    }

    public string? Start()
    {
        if (IsRunning) return null;
        if (!IsDownloaded) return "PresentMon isn't downloaded yet.";
        try
        {
            var psi = new ProcessStartInfo(ExePath, "--output_stdout --stop_existing_session --session_name F126Overlay")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();
            _process = p;
            lock (_sync) _lastError = null;

            _ = Task.Run(() => ReadOutput(p));
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (_sync) _lastError = e.Data.Trim(); };
            p.BeginErrorReadLine();
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't start PresentMon: {ex.Message}";
        }
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false } p) p.Kill(); } catch { /* already gone */ }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();

    private void ReadOutput(Process p)
    {
        int appCol = -1, msCol = -1;
        string? line;
        try
        {
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var cols = line.Split(',');
                if (appCol < 0)
                {
                    // Header row: find the application and the frame-time columns (names differ between versions).
                    appCol = Array.FindIndex(cols, c => c.Equals("Application", StringComparison.OrdinalIgnoreCase));
                    foreach (var name in new[] { "MsBetweenPresents", "FrameTime", "MsBetweenAppStart", "MsBetweenSimulationStart", "MsBetweenDisplayChange" })
                    {
                        msCol = Array.FindIndex(cols, c => c.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (msCol >= 0) break;
                    }
                    if (appCol < 0 || msCol < 0)
                    {
                        // Not the header (e.g. a status message): print it as the last error and keep looking.
                        lock (_sync) _lastError = line.Trim();
                        appCol = -1;
                    }
                    continue;
                }
                if (cols.Length <= Math.Max(appCol, msCol)) continue;
                if (!double.TryParse(cols[msCol], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) || ms <= 0) continue;

                long now = Environment.TickCount64;
                lock (_sync)
                {
                    if (!_frames.TryGetValue(cols[appCol], out var q)) _frames[cols[appCol]] = q = new Queue<(long, double)>();
                    q.Enqueue((now, ms));
                    while (q.Count > 0 && now - q.Peek().Tick > WindowSeconds * 1000) q.Dequeue();
                }
            }
        }
        catch { /* process ended */ }
    }

    /// <summary>Adds made-up frames (for screenshots / previews without a game running).</summary>
    public void AddFakeFrames(string app, IEnumerable<double> frameMs)
    {
        long now = Environment.TickCount64;
        var list = frameMs.ToList();
        lock (_sync)
        {
            if (!_frames.TryGetValue(app, out var q)) _frames[app] = q = new Queue<(long, double)>();
            long t = now - (long)list.Sum();
            foreach (var ms in list) { t += (long)ms; q.Enqueue((Math.Min(t, now), ms)); }
        }
    }

    /// <summary>Process names (with .exe) currently presenting frames, busiest first.</summary>
    public string[] ActiveApps()
    {
        long now = Environment.TickCount64;
        lock (_sync)
            return _frames.Where(kv => kv.Value.Count > 0 && now - kv.Value.Last().Tick < 2000)
                .OrderByDescending(kv => kv.Value.Count).Select(kv => kv.Key).ToArray();
    }

    /// <summary>FPS over the last second, 1% low over the last 10 seconds, and recent frame times (ms).</summary>
    public (double Fps, double Low1, double FrameMs, double[] Recent)? Stats(string app)
    {
        long now = Environment.TickCount64;
        lock (_sync)
        {
            if (!_frames.TryGetValue(app, out var q) || q.Count == 0 || now - q.Last().Tick > 2000) return null;
            var all = q.ToArray();
            var lastSecond = all.Where(f => now - f.Tick <= 1000).Select(f => f.Ms).ToArray();
            if (lastSecond.Length == 0) return null;
            double avg = lastSecond.Average();
            var sorted = all.Select(f => f.Ms).OrderByDescending(m => m).ToArray();
            int worst = Math.Max(1, sorted.Length / 100);
            double low = 1000.0 / sorted.Take(worst).Average();
            var recent = all.Skip(Math.Max(0, all.Length - 240)).Select(f => f.Ms).ToArray();
            return (1000.0 / avg, low, avg, recent);
        }
    }

    // Which app to show in "auto" mode: whatever has focus (the overlays never take focus).
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static string? ForegroundExe()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }
}
