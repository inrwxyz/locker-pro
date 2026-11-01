using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AppLocker.Core;

// Complete clean uninstaller: asks for Master Password / Recovery Key,
// decrypts & restores all locked folders, clears app locks, wipes registry menus, stops Guard,
// removes all shortcuts, scheduled tasks, and completely removes Locker.
public static class Uninstaller
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_FLUSH = 0x1000;

    public static (bool success, string message) FullUninstall(string passwordOrRecovery)
    {
        if (!MasterConfig.IsSetup())
        {
            return PurgeSystem(0, passwordOrRecovery);
        }

        byte[]? key = MasterConfig.GetKeyWithPassword(passwordOrRecovery)
                      ?? MasterConfig.GetKeyWithRecovery(passwordOrRecovery);
        if (key == null)
        {
            return (false, "Master password ba recovery key bhul.");
        }

        int foldersUnlocked = 0;
        try
        {
            // 1. Decrypt and restore all locked folders
            var folders = LockRegistry.List();
            foreach (var folder in folders)
            {
                try
                {
                    if (Directory.Exists(folder) && FolderLocker.IsLocked(folder))
                    {
                        if (FolderLocker.Unlock(folder, key)) foldersUnlocked++;
                    }
                    else
                    {
                        LockRegistry.Remove(folder);
                    }
                }
                catch { }
            }

            // 2. Clear all locked apps
            var apps = AppRegistry.List();
            foreach (var app in apps)
            {
                try { AppRegistry.Remove(app); } catch { }
            }

            return PurgeSystem(foldersUnlocked, passwordOrRecovery);
        }
        finally
        {
            Array.Clear(key);
        }
    }

    private static (bool success, string message) PurgeSystem(int foldersUnlocked, string passwordOrRecovery)
    {
        // 3. Remove context menu keys from BOTH HKCU and HKLM
        string[] contextKeys =
        {
            @"Software\Classes\Directory\shell\LockerLock",
            @"Software\Classes\exefile\shell\LockerLockApp",
            @"Software\Classes\lnkfile\shell\LockerLockApp",
            @"Software\Classes\SystemFileAssociations\.lnk\shell\LockerLockApp",
            @"Software\Classes\SystemFileAssociations\.exe\shell\LockerLockApp",
            @"Software\Classes\Directory\Background\shell\LockerLock",
            @"Software\Classes\Drive\shell\LockerLock",
            @"Software\Classes\*\shell\LockerLock",
            @"Software\Classes\Directory\shell\AppLockerLock",
            @"Software\Classes\exefile\shell\AppLockerLockApp",
            @"Software\Classes\lnkfile\shell\AppLockerLockApp"
        };

        foreach (var sub in contextKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(sub, false); } catch { }
        }

        // 4. Remove Windows Uninstall Registry entries in both 32-bit and 64-bit views
        try
        {
            using var cu32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            cu32.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Locker", false);
        }
        catch { }
        try
        {
            using var cu64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            cu64.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Locker", false);
        }
        catch { }
        try
        {
            using var lm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            lm32.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Locker", false);
        }
        catch { }
        try
        {
            using var lm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            lm64.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Locker", false);
        }
        catch { }

        // 4b. Remove the Windows Firewall rule the installer added for the phone link
        try
        {
            var fw = new ProcessStartInfo("netsh", "advfirewall firewall delete rule name=\"Locker Phone Link\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(fw)?.WaitForExit(5000);
        }
        catch { }

        // 5. Delete Scheduled Tasks "LockerGuard" and "LockerBoot"
        try
        {
            var psi = new ProcessStartInfo("schtasks", "/delete /tn \"LockerGuard\" /f")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(500);
        }
        catch { }

        try
        {
            var bootPsi = new ProcessStartInfo("schtasks", "/delete /tn \"LockerBoot\" /f")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(bootPsi)?.WaitForExit(500);
        }
        catch { }

        // 6. Delete Desktop shortcuts from User Desktop and Public Desktop
        string[] desktopDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            @"C:\Users\Public\Desktop",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        foreach (var d in desktopDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(d))
                {
                    foreach (var lnk in Directory.GetFiles(d, "*Locker*.lnk"))
                    {
                        try { File.Delete(lnk); } catch { }
                    }
                }
            }
            catch { }
        }

        // 7. Delete Start Menu shortcuts
        string[] startDirs =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Locker"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Locker")
        };
        foreach (var sm in startDirs)
        {
            try
            {
                if (Directory.Exists(sm))
                {
                    Directory.Delete(sm, true);
                }
            }
            catch { }
        }

        // 8. Signal Guard to quit and kill running processes
        try
        {
            using var stopEvt = new EventWaitHandle(false, EventResetMode.ManualReset, "AppLockerWatchdogStop");
            stopEvt.Set();
        }
        catch { }
        try { GuardPipe.Send("quit|" + passwordOrRecovery, 300); } catch { }
        try
        {
            var killPsi = new ProcessStartInfo("taskkill", "/F /IM Locker.exe /IM LockerGuard.exe /IM AppLocker.UI.exe /IM AppLocker.Guard.exe /T")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(killPsi)?.WaitForExit(600);
        }
        catch { }

        string[] procsToKill = { "Locker", "LockerGuard", "AppLocker.UI", "AppLocker.Guard" };
        foreach (var pName in procsToKill)
        {
            foreach (var p in Process.GetProcessesByName(pName))
            {
                try { p.Kill(true); } catch { }
            }
        }

        // 9. Delete ProgramData config folder
        try
        {
            if (Directory.Exists(MasterConfig.ConfigDir))
            {
                foreach (var f in Directory.GetFiles(MasterConfig.ConfigDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                        File.Delete(f);
                    }
                    catch { }
                }
                Directory.Delete(MasterConfig.ConfigDir, true);
            }
        }
        catch { }

        // 10. Pre-delete non-locked files in %LocalAppData%\Locker
        string localAppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Locker");
        string programFilesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Locker");
        string cfgDir = MasterConfig.ConfigDir;

        try
        {
            if (Directory.Exists(localAppDir))
            {
                foreach (var f in Directory.GetFiles(localAppDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                        File.Delete(f);
                    }
                    catch { }
                }
                foreach (var sub in Directory.GetDirectories(localAppDir))
                {
                    try { Directory.Delete(sub, true); } catch { }
                }
                try { Directory.Delete(localAppDir, true); } catch { }
            }
        }
        catch { }

        // 11. Flush Shell cache so Explorer and Windows Settings update immediately
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }

        // 12. Robust background detached batch cleanup script using ping sleep to delete entire installation folder after current process exits
        try
        {
            string batchPath = Path.Combine(Path.GetTempPath(), "locker_cleanup.cmd");
            string batchContent = $@"@echo off
ping 127.0.0.1 -n 2 >nul
taskkill /F /IM Locker.exe /IM LockerGuard.exe /IM uninstall.exe /IM AppLocker.UI.exe /IM AppLocker.Guard.exe >nul 2>&1
for /l %%i in (1,1,25) do (
    del /f /q /s ""{localAppDir}\*"" >nul 2>&1
    rmdir /s /q ""{localAppDir}"" >nul 2>&1
    del /f /q /s ""{programFilesDir}\*"" >nul 2>&1
    rmdir /s /q ""{programFilesDir}"" >nul 2>&1
    del /f /q /s ""{cfgDir}\*"" >nul 2>&1
    rmdir /s /q ""{cfgDir}"" >nul 2>&1
    if not exist ""{localAppDir}"" goto done
    ping 127.0.0.1 -n 2 >nul
)
:done
del ""%~f0"" >nul 2>&1
";
            File.WriteAllText(batchPath, batchContent);

            var selfDelPsi = new ProcessStartInfo("cmd.exe", $"/c \"{batchPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(selfDelPsi);
        }
        catch { }

        return (true, "Locker system theke successfully uninstall kora hoyeche.");
    }
}
