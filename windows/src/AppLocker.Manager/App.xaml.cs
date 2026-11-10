using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AppLocker.Core;
using AppLocker.Manager.Guard;
using Microsoft.Win32;

namespace AppLocker.Manager;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private static Mutex? _guardMutex;
    private static Mutex? _uiMutex;
    private GuardHost? _guardHost;
    private GuardServer? _guardServer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string[] args = e.Args;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
                MasterConfig.ConfigDir = args[i + 1].Trim('"');
        }

        // Mode 1: Context menu invoker (lock "path" / lockapp "path" / open "path")
        if (args.Length >= 2 && (args[0] == "lock" || args[0] == "lockapp" || args[0] == "open"))
        {
            HandleContextCommand(args[0], args[1]);
            Shutdown();
            return;
        }

        // Mode 2: Dedicated Boot Service (--boot)
        if (args.Any(a => a.Equals("--boot", StringComparison.OrdinalIgnoreCase)))
        {
            StartBootMode();
            return;
        }

        // Mode 3: Dedicated Background Guard Service (--guard)
        if (args.Any(a => a.Equals("--guard", StringComparison.OrdinalIgnoreCase)))
        {
            StartGuardMode();
            return;
        }

        // Mode 4: Uninstall launcher (--uninstall)
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            string uninstallerExe = Path.Combine(AppContext.BaseDirectory, "uninstall.exe");
            if (File.Exists(uninstallerExe))
            {
                Process.Start(new ProcessStartInfo(uninstallerExe) { UseShellExecute = true, Verb = "runas" });
            }
            Shutdown();
            return;
        }

        // Mode 5: Main UI Manager
        // Enforce single UI instance so rapid double-clicks / installer finish never spawn two password gates
        bool createdNewUi = true;
        try
        {
            _uiMutex = new Mutex(true, @"Local\LockerUI_SingleInstance", out createdNewUi);
        }
        catch { }

        int curPid = Environment.ProcessId;
        var allLockerProcs = Process.GetProcessesByName("Locker");
        var existingUi = allLockerProcs.FirstOrDefault(p => p.Id != curPid && p.MainWindowHandle != IntPtr.Zero);

        if (!createdNewUi || existingUi != null)
        {
            if (existingUi != null)
            {
                try
                {
                    ShowWindow(existingUi.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(existingUi.MainWindowHandle);
                }
                catch { }
            }
            foreach (var p in allLockerProcs) p.Dispose();
            Shutdown();
            return;
        }
        foreach (var p in allLockerProcs) p.Dispose();

        // Protect process from Task Manager termination and launch mutual watchdog
        ProcessProtector.Protect();
        WatchdogLauncher.Start();

        // Start background tasks asynchronously so UI launch is instant and non-blocking
        Task.Run(() =>
        {
            EnsureGuardRunning();
            RemoveContextMenuRemnants();
            PhoneCompanionService.StartListener();
        });

        // UI gate: setup hoye gele Locker kholar jonno proti bar Master Password lagbe.
        if (MasterConfig.IsSetup())
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var gate = new PasswordPromptDialog("Enter Master Password to open Locker:", isLock: false, target: "Locker");
            gate.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            gate.Topmost = true;
            bool? passed = gate.ShowDialog();
            if (passed != true)
            {
                Shutdown();
                return;
            }
            if (gate.MasterKey != null)
            {
                PhoneCompanionService.TryAutoEnroll(gate.MasterKey); // links the fingerprint in background if missing
                Array.Clear(gate.MasterKey);
            }
        }

        var mainWin = new MainWindow();
        this.MainWindow = mainWin;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        mainWin.Show();
    }

    private static Mutex? _bootMutex;

    private void StartBootMode()
    {
        try
        {
            bool createdNew;
            _bootMutex = new Mutex(true, @"Global\LockerBoot_SingleInstance", out createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }
        }
        catch
        {
            Shutdown();
            return;
        }

        ProcessProtector.Protect();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        PhoneCompanionService.StartListener();

        // Monitor for user login: when an interactive explorer.exe process appears,
        // wait a few seconds and exit cleanly so user-session --guard takes over.
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2000);
                try
                {
                    var explorers = Process.GetProcessesByName("explorer");
                    bool anyExplorer = explorers.Length > 0;
                    foreach (var exp in explorers) exp.Dispose();
                    if (anyExplorer)
                    {
                        await Task.Delay(5000);
                        PhoneCompanionService.StopListener();
                        Environment.Exit(0);
                    }
                }
                catch { }
            }
        });
    }

    private void StartGuardMode()
    {
        try
        {
            bool createdNew;
            _guardMutex = new Mutex(true, @"Local\LockerGuard_SingleInstance", out createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }
        }
        catch { }

        try
        {
            // Terminate boot service task if it was running so guard takes over
            Process.Start(new ProcessStartInfo("schtasks", "/end /tn LockerBoot")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit(500);
        }
        catch { }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Protect process from Task Manager termination and launch mutual watchdog
        ProcessProtector.Protect();
        WatchdogLauncher.Start();

        _guardHost = new GuardHost();
        _guardHost.Start();

        _guardServer = new GuardServer(_guardHost.Handle);
        _guardServer.Start();

        PhoneCompanionService.StartListener();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WatchdogLauncher.Stop();
        PhoneCompanionService.StopListener();
        _guardHost?.Stop();
        _guardServer?.Stop();

        try { _bootMutex?.ReleaseMutex(); } catch { }
        try { _guardMutex?.ReleaseMutex(); } catch { }
        try { _uiMutex?.ReleaseMutex(); } catch { }

        base.OnExit(e);
    }

    private static void HandleContextCommand(string cmd, string path)
    {
        if (GuardPipe.Send(cmd + "|" + path, 1000) != null) return;

        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "Locker.exe");
        try
        {
            Process.Start(new ProcessStartInfo(currentExe, "--guard")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(currentExe)!
            });
            Thread.Sleep(500);
            GuardPipe.Send(cmd + "|" + path, 1500);
        }
        catch { }
    }

    private static void EnsureGuardRunning()
    {
        try
        {
            if (GuardPipe.Send("watch|check", 300) == null)
            {
                string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "Locker.exe");
                if (File.Exists(currentExe))
                {
                    Process.Start(new ProcessStartInfo(currentExe, "--guard")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = AppContext.BaseDirectory
                    });
                }
            }
        }
        catch { }
    }

    private static void RemoveContextMenuRemnants()
    {
        string[] contextKeys =
        {
            @"Software\Classes\Directory\shell\LockerLock",
            @"Software\Classes\exefile\shell\LockerLockApp",
            @"Software\Classes\lnkfile\shell\LockerLockApp",
            @"Software\Classes\SystemFileAssociations\.lnk\shell\LockerLockApp",
            @"Software\Classes\SystemFileAssociations\.exe\shell\LockerLockApp",
            @"Software\Classes\Directory\Background\shell\LockerLock",
            @"Software\Classes\Drive\shell\LockerLock",
            @"Software\Classes\*\shell\LockerLock"
        };

        foreach (var sub in contextKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
        }
    }
}
