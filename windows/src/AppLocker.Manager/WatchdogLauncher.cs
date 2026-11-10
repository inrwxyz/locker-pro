using System.Diagnostics;
using System.IO;
using System.Threading;
using AppLocker.Core;

namespace AppLocker.Manager;

/// <summary>
/// Keeps the watchdog process (LockerGuard.exe) alive and running.
/// If LockerGuard is killed, this re-launches it.
/// If Locker.exe is killed, LockerGuard immediately re-launches Locker.exe.
/// </summary>
public static class WatchdogLauncher
{
    public const string StopEventName = DirHarden.WatchdogStopEventName;
    private static System.Threading.Timer? _timer;

    public static void Start()
    {
        try
        {
            using var stop = DirHarden.OpenOrCreateWatchdogStopEvent();
            stop.Reset();
        }
        catch { }

        Ensure();

        // Check every 3 seconds on thread pool
        _timer = new System.Threading.Timer(_ => Ensure(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public static void Stop()
    {
        try
        {
            _timer?.Dispose();
            _timer = null;
        }
        catch { }
    }

    private static void Ensure()
    {
        try
        {
            var guards = Process.GetProcessesByName("LockerGuard");
            bool running = guards.Length > 0;
            foreach (var g in guards) g.Dispose();
            if (running) return;

            string? exe = FindGuard();
            if (exe == null || !File.Exists(exe)) return;

            string lockerExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "Locker.exe");

            Process.Start(new ProcessStartInfo(exe, $"--target \"{lockerExe}\" --args \"--guard\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            });
        }
        catch { }
    }

    private static string? FindGuard()
    {
        string same = Path.Combine(AppContext.BaseDirectory, "LockerGuard.exe");
        if (File.Exists(same)) return same;

        // Dev layout fallback
        string devRelease = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\AppLocker.Guard\bin\Release\net10.0-windows\LockerGuard.exe"));
        if (File.Exists(devRelease)) return devRelease;

        string devDebug = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\AppLocker.Guard\bin\Debug\net10.0-windows\LockerGuard.exe"));
        if (File.Exists(devDebug)) return devDebug;

        return null;
    }

    public static void SignalStop()
    {
        DirHarden.SignalWatchdogStop();
    }
}
