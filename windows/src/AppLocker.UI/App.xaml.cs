using System.Windows;
using AppLocker.Core;

namespace AppLocker.UI;

public partial class App : Application
{
    private Mutex? _mutex;
    private GuardHost? _host;
    private bool _primary;
    private bool _dev;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Args: [open|lock|lockapp "path"] [--config C:\dir] [--dev]
        // --dev: End Task protection + watchdog bondho (development er jonno)
        _dev = e.Args.Contains("--dev");
        string? cmd = null; // "open|path", "lock|path", "lockapp|path"
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            string a = e.Args[i];
            if (a == "--config") MasterConfig.ConfigDir = e.Args[i + 1].Trim('"');
            if (a == "open" || a == "lock" || a == "lockapp") cmd = a + "|" + e.Args[i + 1].Trim('"');
        }

        // Ek-i Guard chalu thakbe
        _mutex = new Mutex(true, "AppLockerGuardSingleton", out bool first);
        if (!first)
        {
            if (cmd != null) GuardPipe.Send(cmd);
            Shutdown();
            return;
        }
        _primary = true;

        _host = new GuardHost();
        _host.Start();
        new GuardServer(_host).Start();

        if (!_dev)
        {
            ProcessProtector.Protect();   // End Task e Access denied
            WatchdogLauncher.Start();     // kew kill korle watchdog abar chalu kore
        }

        if (cmd != null) _host.Handle(cmd);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Stop();

        // Shudhu primary + sothik quit hole watchdog ke thamai
        if (_primary && !_dev)
        {
            WatchdogLauncher.Stop();
            WatchdogLauncher.SignalStop();
        }
        base.OnExit(e);
    }
}
