using System.Diagnostics;
using AppLocker.Core;

// Right-click menu er launcher: console nai, UAC nai (Guard cholle).
// Use: AppLocker.Launch.exe lock|lockapp|open "path"
// Guard cholle pipe e pathay, popup Guard dekhay. Na cholle Guard ke admin e chalu kore.
if (args.Length < 2) return;
string cmd = args[0];
string path = args[1];
if (cmd != "lock" && cmd != "lockapp" && cmd != "open") return;

if (GuardPipe.Send(cmd + "|" + path, 800) != null) return;

// Guard chalu nai: AppLocker.UI ke admin e chalu kore, same command diye
string? ui = FindUi(out bool dev);
if (ui == null) return;

try
{
    Process.Start(new ProcessStartInfo(ui, (dev ? "--dev " : "") + cmd + " \"" + path + "\"")
    {
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = Path.GetDirectoryName(ui)!
    });
}
catch { }

static string? FindUi(out bool dev)
{
    dev = false;
    string same = Path.Combine(AppContext.BaseDirectory, "AppLocker.UI.exe");
    if (File.Exists(same)) return same;

    // Dev layout: src\AppLocker.Launch\bin\Debug\net10.0-windows -> src\AppLocker.UI\bin\...
    string d = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        @"..\..\..\..\AppLocker.UI\bin\Debug\net10.0-windows\AppLocker.UI.exe"));
    if (File.Exists(d)) { dev = true; return d; }
    return null;
}
