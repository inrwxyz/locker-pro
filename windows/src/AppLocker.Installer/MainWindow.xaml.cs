using System.Diagnostics;

using System.IO;

using System.IO.Compression;

using System.Reflection;

using System.Runtime.InteropServices;

using System.Windows;

using System.Windows.Input;

using Microsoft.Win32;



namespace AppLocker.Installer;



public partial class MainWindow : Window

{

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    private readonly string _targetDir = Path.Combine(

        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Locker");



    public MainWindow()

    {

        InitializeComponent();

        InstallPathText.Text = _targetDir;
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
        {
            Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Run(() => PerformInstall(true, true, true));
                    LaunchLocker();
                }
                finally
                {
                    Close();
                }
            };
        }

    }



    private void Window_MouseDown(object sender, MouseButtonEventArgs e)

    {

        if (e.ChangedButton == MouseButton.Left)

        {

            try { DragMove(); } catch { }

        }

    }



    private void Close_Click(object sender, RoutedEventArgs e) => Close();



    private async void InstallBtn_Click(object sender, RoutedEventArgs e)

    {

        InstallBtn.IsEnabled = false;

        OptionsPanel.Visibility = Visibility.Collapsed;

        ProgressPanel.Visibility = Visibility.Visible;



        bool addMenu = ChkContextMenu.IsChecked == true;

        bool autoStart = ChkAutoStart.IsChecked == true;

        bool desktopIcon = ChkDesktopShortcut.IsChecked == true;



        try

        {

            await Task.Run(() => PerformInstall(addMenu, autoStart, desktopIcon));



            ProgressStatusText.Text = "Installation Complete!";

            ProgressDetailText.Text = "Locker is ready to use.";

            InstallProgressBar.IsIndeterminate = false;

            InstallProgressBar.Value = 100;



            InstallBtn.Visibility = Visibility.Collapsed;

            FinishBtn.Visibility = Visibility.Visible;



            await Task.Delay(1200);

            LaunchLocker();

        }

        catch (Exception ex)

        {

            MessageBox.Show("Install error: " + ex.Message, "Locker Setup", MessageBoxButton.OK, MessageBoxImage.Error);

            Close();

        }

    }



    private void UpdateDetail(string text)

    {

        Dispatcher.Invoke(() => ProgressDetailText.Text = text);

    }



    private void PerformInstall(bool addMenu, bool autoStart, bool desktopIcon)

    {

        // 1. Stop existing processes
        UpdateDetail("Stopping existing Locker processes...");
        try
        {
            using var stopEvt = new EventWaitHandle(false, EventResetMode.ManualReset, "AppLockerWatchdogStop");
            stopEvt.Set();
        }
        catch { }
        try
        {
            var killPsi = new ProcessStartInfo("taskkill", "/F /IM Locker.exe /IM LockerGuard.exe /IM AppLocker.UI.exe /IM AppLocker.Guard.exe /T")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(killPsi)?.WaitForExit(3000);
        }
        catch { }
        try
        {
            foreach (var p in Process.GetProcessesByName("Locker")) { try { p.Kill(true); p.WaitForExit(1000); } catch { } }
            foreach (var p in Process.GetProcessesByName("LockerGuard")) { try { p.Kill(true); p.WaitForExit(1000); } catch { } }
            foreach (var p in Process.GetProcessesByName("AppLocker.UI")) { try { p.Kill(true); p.WaitForExit(1000); } catch { } }
            foreach (var p in Process.GetProcessesByName("AppLocker.Guard")) { try { p.Kill(true); p.WaitForExit(1000); } catch { } }
            foreach (var p in Process.GetProcessesByName("uninstall")) { try { p.Kill(true); p.WaitForExit(1000); } catch { } }
        }
        catch { }
        Thread.Sleep(600);

        try
        {
            if (Directory.Exists(_targetDir))
            {
                foreach (var old in Directory.GetFiles(_targetDir, "*.old"))
                {
                    try { File.Delete(old); } catch { }
                }
            }
        }
        catch { }

        // 2. Extract payload to %LocalAppData%\Locker

        // NEVER wipe an existing config: config.json holds the wrapped master key. Deleting it makes every
        // locked folder permanently undecryptable. Only a fresh install (no config.json) gets a clean folder.
        string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppLocker");
        bool keepConfig = File.Exists(Path.Combine(cfgDir, "config.json"));

        if (keepConfig)
        {
            // upgrade / reinstall: keep config.json, items.json, settings, vault and the hardened permissions as they are.
            // Only a stale pairing PIN is removed.
            try { File.Delete(Path.Combine(cfgDir, ".pairing.pin")); } catch { }
        }
        else
        {
            try
            {
                if (Directory.Exists(cfgDir))
                {
                    foreach (var f in Directory.GetFiles(cfgDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                    }
                    Directory.Delete(cfgDir, true);
                }
            }
            catch { }

            try
            {
                // Locker locks this folder down to SYSTEM + Administrators itself (DirHarden).
                // Do NOT grant Users here: config.json / lockout.json / settings.json live in it.
                Directory.CreateDirectory(cfgDir);
            }
            catch { }
        }

        UpdateDetail($"Extracting files to {_targetDir}...");

        Directory.CreateDirectory(_targetDir);



        var asm = Assembly.GetExecutingAssembly();

        using var stream = asm.GetManifestResourceStream("AppLocker.Installer.payload.zip")

            ?? throw new InvalidOperationException("Installer payload not found.");



        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)

        {

            string dest = Path.Combine(_targetDir, entry.FullName);

            string? dir = Path.GetDirectoryName(dest);

            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);



            if (!string.IsNullOrEmpty(entry.Name))

            {

                try

                {

                    if (File.Exists(dest)) File.Delete(dest);

                }

                catch

                {

                    try

                    {

                        string oldPath = dest + "." + Guid.NewGuid().ToString("N")[..6] + ".old";

                        File.Move(dest, oldPath);

                    }

                    catch { }

                }



                entry.ExtractToFile(dest, overwrite: true);

            }

        }



        // Purono install (LocalAppData\Locker) mushe felo, noile oikhane bodlano exe theke jabe.
        try
        {
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Locker");
            if (!legacy.Equals(_targetDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(legacy))
                Directory.Delete(legacy, true);
        }
        catch { }

        string lockerExe = Path.Combine(_targetDir, "Locker.exe");

        string uninstallExe = Path.Combine(_targetDir, "uninstall.exe");

        string iconPath = Path.Combine(_targetDir, "lock.ico");



        // 3. Clean up any context menu remnants (Right-click menu disabled)
        string[] contextKeys = {
            @"Software\Classes\Directory\shell\LockerLock",
            @"Software\Classes\exefile\shell\LockerLockApp",
            @"Software\Classes\lnkfile\shell\LockerLockApp",
            @"Software\Classes\SystemFileAssociations\.lnk\shell\LockerLockApp",
            @"Software\Classes\SystemFileAssociations\.exe\shell\LockerLockApp",
            @"Software\Classes\Directory\Background\shell\LockerLock",
            @"Software\Classes\Drive\shell\LockerLock",
            @"Software\Classes\*\shell\LockerLock"
        };
        foreach (var sub in contextKeys) {
            try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(sub, false); } catch { }
        }

        // 3b. Windows Firewall: let the phone reach Locker on TCP 58820 (pairing + approvals).
        // Without this a fresh PC silently drops the phone's connection ("Pairing failed ... after 6000ms").
        // Same rule name as the one added by hand earlier, so it is replaced, never duplicated.
        try
        {
            foreach (var args in new[]
            {
                "advfirewall firewall delete rule name=\"Locker Phone Link\"",
                "advfirewall firewall add rule name=\"Locker Phone Link\" dir=in action=allow protocol=TCP localport=58820 profile=any"
            })
            {
                var fw = new ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(fw)?.WaitForExit(5000);
            }
        }
        catch { }

        // 4. Setup Logon Scheduled Task

        if (autoStart)

        {

            UpdateDetail("Configuring startup scheduled task...");

            try

            {

                var psi = new ProcessStartInfo("schtasks", $"/create /tn \"LockerGuard\" /tr \"\\\"{lockerExe}\\\" --guard\" /sc onlogon /rl highest /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(3000);
            }
            catch { }

            try
            {
                var bootPsi = new ProcessStartInfo("schtasks", $"/create /tn \"LockerBoot\" /tr \"\\\"{lockerExe}\\\" --boot\" /sc onstart /ru SYSTEM /rl highest /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(bootPsi)?.WaitForExit(3000);

            }

            catch { }

        }



        // 5. Create Shortcuts

        string shortcutIcon = lockerExe + ",0";
        if (desktopIcon)
        {
            UpdateDetail("Creating Desktop shortcut...");
            try
            {
                string commonDesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Locker.lnk");
                try { if (File.Exists(commonDesktopLnk)) File.Delete(commonDesktopLnk); } catch { }

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopPath, "Locker.lnk"), lockerExe, _targetDir, shortcutIcon);
            }
            catch { }
        }

        // Start Menu shortcut
        try
        {
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Locker");
            Directory.CreateDirectory(startMenu);
            CreateShortcut(Path.Combine(startMenu, "Locker.lnk"), lockerExe, _targetDir, shortcutIcon);
        }
        catch { }

        // Force Windows Explorer to refresh shortcut/EXE icon cache
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            Process.Start(new ProcessStartInfo("ie4uinit.exe", "-show") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(1500);
        }
        catch { }



        // 6. Windows Uninstall Entry (Points directly to uninstall.exe!)

        try
        {
            void SetUninstall(RegistryKey baseKey)
            {
                using var ukey = baseKey.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Locker");
                if (ukey != null)
                {
                    ukey.SetValue("DisplayName", "Locker PRO");
                    ukey.SetValue("DisplayIcon", iconPath);
                    ukey.SetValue("DisplayVersion", "2.0");
                    ukey.SetValue("Publisher", "inrwxyz");
                    ukey.SetValue("InstallLocation", _targetDir);
                    ukey.SetValue("UninstallString", $"\"{uninstallExe}\"");
                    ukey.SetValue("QuietUninstallString", $"\"{uninstallExe}\"");
                    ukey.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    ukey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            try { SetUninstall(Registry.CurrentUser); } catch { }
            try { SetUninstall(Registry.LocalMachine); } catch { }
        }
        catch { }



        // 7. Start Guard Background Process & Open Locker UI

        UpdateDetail("Starting Locker...");

        try

        {

            Process.Start(new ProcessStartInfo(lockerExe, "--guard")

            {

                UseShellExecute = true,

                WorkingDirectory = _targetDir

            });

        }

        catch { }

    }



    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string iconPath)
    {
        try
        {
            try { if (File.Exists(shortcutPath)) File.Delete(shortcutPath); } catch { }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.IconLocation = iconPath;
            shortcut.Save();
        }
        catch { }
    }



    private void FinishBtn_Click(object sender, RoutedEventArgs e)

    {

        LaunchLocker();

    }



    private void LaunchLocker()

    {

        try

        {

            string lockerExe = Path.Combine(_targetDir, "Locker.exe");

            if (File.Exists(lockerExe))

            {

                Process.Start(new ProcessStartInfo(lockerExe)

                {

                    UseShellExecute = true,

                    WorkingDirectory = _targetDir

                });

            }

        }

        catch { }

        Close();

    }

}

