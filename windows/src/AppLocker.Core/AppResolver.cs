using System.Text.RegularExpressions;

namespace AppLocker.Core;

// Shortcut (.lnk) theke asol .exe ber kore. Discord er moto Squirrel launcher (Update.exe) o bojhe.
public static class AppResolver
{
    public static string ResolveShortcut(string lnk)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return lnk;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            string args = sc.Arguments;
            if (string.IsNullOrEmpty(target)) return lnk;
            return FixLauncher(target, args);
        }
        catch
        {
            return lnk;
        }
    }

    // Update.exe --processStart Discord.exe  =>  ...\Discord\app-<notun version>\Discord.exe
    public static string FixLauncher(string target, string? args)
    {
        try
        {
            // chrome_proxy.exe / msedge_proxy.exe (Chrome/Edge app shortcut, jemon YouTube):
            // eta chalu hoyei bondho hoye jay, tai gate kora jay na. Asol browser exe lock kori.
            string file = Path.GetFileName(target);
            string? pdir = Path.GetDirectoryName(target);
            if (pdir != null && file.EndsWith("_proxy.exe", StringComparison.OrdinalIgnoreCase))
            {
                string real = Path.Combine(pdir, file[..^"_proxy.exe".Length] + ".exe");
                if (File.Exists(real)) return real;
            }

            // Squirrel root stub (Discord): <root>\X.exe  =>  <root>\app-<notun version>\X.exe
            // Root er X.exe ekta launcher, asol app app-* folder e. Launcher gate kora jay na.
            if (pdir != null)
            {
                var inApp = Directory.GetDirectories(pdir, "app-*")
                    .Select(d => new { d, v = ParseVersion(Path.GetFileName(d)[4..]) })
                    .OrderByDescending(x => x.v)
                    .Select(x => Path.Combine(x.d, file))
                    .FirstOrDefault(File.Exists);
                if (inApp != null) return inApp;
            }

            if (!Path.GetFileName(target).Equals("Update.exe", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(args)) return target;

            var m = Regex.Match(args, "--processStart(?:AndWait)?\\s+\"?([^\"\\s]+\\.exe)\"?", RegexOptions.IgnoreCase);
            string? dir = Path.GetDirectoryName(target);
            if (!m.Success || dir == null) return target;

            var best = Directory.GetDirectories(dir, "app-*")
                .Select(d => new { d, v = ParseVersion(Path.GetFileName(d)[4..]) })
                .OrderByDescending(x => x.v)
                .Select(x => Path.Combine(x.d, m.Groups[1].Value))
                .FirstOrDefault(File.Exists);
            return best ?? target;
        }
        catch
        {
            return target;
        }
    }

    private static Version ParseVersion(string s) => Version.TryParse(s, out var v) ? v : new Version(0, 0);
}
