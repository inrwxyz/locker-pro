using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AppLocker.Uninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string currentDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tempRoot = Path.Combine(Path.GetTempPath(), "LockerUninstaller");

        // If running from %LocalAppData%\Locker, copy to %TEMP% so the Locker directory can be 100% wiped!
        if (!currentDir.Equals(tempRoot, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    try { Directory.Delete(tempRoot, true); } catch { }
                }
                Directory.CreateDirectory(tempRoot);

                CopyFolder(currentDir, tempRoot);

                string tempExe = Path.Combine(tempRoot, "uninstall.exe");
                if (File.Exists(tempExe))
                {
                    string argsStr = string.Join(" ", e.Args.Select(a => $"\"{a}\""));
                    var psi = new ProcessStartInfo(tempExe, argsStr)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = tempRoot
                    };
                    Process.Start(psi);
                    Shutdown();
                    return;
                }
            }
            catch { }
        }

        string? initialPw = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--pw" && i + 1 < e.Args.Length)
            {
                initialPw = e.Args[i + 1];
            }
        }

        var mainWin = new MainWindow(initialPw);
        mainWin.Show();
    }

    private static void CopyFolder(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true); } catch { }
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            try { CopyFolder(dir, Path.Combine(dest, Path.GetFileName(dir))); } catch { }
        }
    }
}
