using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AppLocker.Core;

namespace AppLocker.Uninstaller;

public partial class MainWindow : Window
{
    private readonly string? _initialPw;

    public MainWindow(string? initialPw = null)
    {
        InitializeComponent();
        _initialPw = initialPw;

        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(_initialPw))
            {
                await PerformUninstallAsync(_initialPw);
            }
            else if (!MasterConfig.IsSetup())
            {
                PwBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                PwBox.Focus();
            }
        };
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "LockerUninstaller");
            string curDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (curDir.Equals(tempDir, StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 2 >nul & rmdir /s /q \"{tempDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
        }
        catch { }

        Close();
        Application.Current.Shutdown();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        string pw = PwBox.Password;
        if (MasterConfig.IsSetup())
        {
            if (string.IsNullOrEmpty(pw))
            {
                ErrorText.Text = "Enter Master Password.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            // Verify password instantly before starting long cleanup
            byte[]? key = AuthGate.TryPassword(pw, out int lockWait, allowRecovery: true);
            if (key == null)
            {
                ErrorText.Text = lockWait > 0
                    ? "Too many incorrect attempts. Try again in " + LockoutTracker.Format(lockWait) + "."
                    : "Incorrect Master Password or Recovery Key.";
                ErrorText.Visibility = Visibility.Visible;
                PwBox.SelectAll();
                PwBox.Focus();
                return;
            }
            Array.Clear(key);
        }

        await PerformUninstallAsync(pw);
    }

    private async Task PerformUninstallAsync(string pw)
    {
        // 1. Immediately switch to progress UI
        PanelPrompt.Visibility = Visibility.Collapsed;
        PanelProgress.Visibility = Visibility.Visible;
        ProgressStatusText.Text = "Closing applications & stopping guards...";

        // 2. Kill all Locker processes immediately
        KillAllLockerProcesses();

        // 3. Run full uninstall in background
        ProgressStatusText.Text = "Restoring folders & deleting files from disk...";
        await Task.Run(() => Core.Uninstaller.FullUninstall(pw));

        // 4. Show completed UI with Close button
        PanelProgress.Visibility = Visibility.Collapsed;
        PanelFinished.Visibility = Visibility.Visible;
    }

    private static void KillAllLockerProcesses()
    {
        DirHarden.SignalWatchdogStop();
        string[] procsToKill = { "Locker", "LockerGuard", "AppLocker.UI", "AppLocker.Guard" };
        foreach (var pName in procsToKill)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(pName))
                {
                    try { p.Kill(true); } catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
        try
        {
            var psi = new ProcessStartInfo("taskkill", "/F /IM Locker.exe /IM LockerGuard.exe /IM AppLocker.UI.exe /IM AppLocker.Guard.exe /T")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(600);
        }
        catch { }
    }
}
