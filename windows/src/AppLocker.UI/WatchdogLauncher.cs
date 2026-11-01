using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace AppLocker.UI;

// UI cholar somoy watchdog (AppLocker.Guard.exe) ke jibito rakhe.
public static class WatchdogLauncher
{
    public const string StopEventName = "AppLockerWatchdogStop";
    private static DispatcherTimer? _timer;

    public static void Start()
    {
        Ensure();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Ensure();
        _timer.Start();
    }

    public static void Stop() => _timer?.Stop();

    private static void Ensure()
    {
        try
        {
            if (Process.GetProcessesByName("AppLocker.Guard").Length > 0) return;
            string? exe = FindGuard();
            if (exe == null) return;

            string ui = Path.Combine(AppContext.BaseDirectory, "AppLocker.UI.exe");
            Process.Start(new ProcessStartInfo(exe, "--target \"" + ui + "\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            });
        }
        catch { }
    }

    private static string? FindGuard()
    {
        string same = Path.Combine(AppContext.BaseDirectory, "AppLocker.Guard.exe");
        if (File.Exists(same)) return same;

        // Dev layout: src\AppLocker.UI\bin\Debug\net10.0-windows -> src\AppLocker.Guard\bin\...
        string dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\AppLocker.Guard\bin\Debug\net10.0-windows\AppLocker.Guard.exe"));
        return File.Exists(dev) ? dev : null;
    }

    // Sothik (password diye) quit hole watchdog ke bole dey: ar restart korish na
    public static void SignalStop()
    {
        try
        {
            using var e = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
            e.Set();
        }
        catch { }
    }
}
