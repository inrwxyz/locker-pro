using System.Diagnostics;
using System.IO;
using System.Threading;
using AppLocker.Core;

// LockerGuard = watchdog process.
// If Locker.exe is terminated (End Task in Task Manager / crash / taskkill),
// it automatically and immediately restarts it.
// It also protects itself via ProcessProtector.Protect().

string? target = null;
string targetArgs = "--guard";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--target" && i + 1 < args.Length) target = args[i + 1].Trim('"');
    if (args[i] == "--args" && i + 1 < args.Length) targetArgs = args[i + 1];
}

if (string.IsNullOrEmpty(target))
{
    target = Path.Combine(AppContext.BaseDirectory, "Locker.exe");
}

using var mutex = new Mutex(true, @"Global\AppLockerWatchdogSingleton", out bool first);
if (!first) return;

if (!File.Exists(target)) return;

ProcessProtector.Protect(); // Resist End Task

using var stop = DirHarden.OpenOrCreateWatchdogStopEvent();
string targetName = Path.GetFileNameWithoutExtension(target);

while (true)
{
    // If deliberate stop event was signaled (e.g. during uninstall or upgrade), exit loop cleanly
    if (stop.WaitOne(1200)) break;

    try
    {
        var procs = Process.GetProcessesByName(targetName);
        bool running = procs.Length > 0;
        foreach (var proc in procs) proc.Dispose();
        if (!running)
        {
            Process.Start(new ProcessStartInfo(target, targetArgs)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target)!
            })?.Dispose();
        }
    }
    catch
    {
        // Suppress transient launch errors and keep watchdog alive
    }
}
