using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppLocker.Core;

// Kon kon app (exe) locked, tar list. Folder er list items.json e, app er list apps.json e.
public static class AppRegistry
{
    private static readonly object Gate = new();
    private static List<string> _cache = new();
    private static DateTime _stamp;
    private static string? _cachedPath;

    private static string FilePath => Path.Combine(MasterConfig.ConfigDir, "apps.json");

    // File bodlale nije reload kore, tai Guard ke restart korte hoy na
    public static List<string> List()
    {
        lock (Gate)
        {
            string p = FilePath;
            if (!File.Exists(p))
            {
                _cache = new();
                _cachedPath = p;
                _stamp = default;
                return new List<string>(_cache);
            }

            DateTime t = File.GetLastWriteTimeUtc(p);
            if (_cachedPath != p || t != _stamp)
            {
                try { _cache = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(p)) ?? new(); }
                catch { _cache = new(); }
                _stamp = t;
                _cachedPath = p;
            }
            return new List<string>(_cache);
        }
    }

    // Version folder bodlale (Discord app-1.0.x, Store app er version) o jate lock thake
    public static string Norm(string p)
    {
        p = p.Replace('/', '\\');
        p = Regex.Replace(p, @"\\app-\d+(\.\d+)*\\", @"\app-*\", RegexOptions.IgnoreCase);
        p = Regex.Replace(p, @"\\([^\\]+?)_\d+(\.\d+)+_([^_\\]*)__([a-z0-9]+)\\",
            @"\${1}_*_${3}__${4}\", RegexOptions.IgnoreCase);
        return p;
    }

    public static bool IsLocked(string exePath)
    {
        return FindLockedMatch(exePath) != null;
    }

    public static string? FindLockedMatch(string exePath)
    {
        var l = List();
        if (l.Count == 0) return null;
        string n = Norm(exePath);
        foreach (var x in l)
        {
            if (Norm(x).Equals(n, StringComparison.OrdinalIgnoreCase))
                return x;
        }
        if (MatchesLockedBinary(exePath))
        {
            try
            {
                var fi = new FileInfo(exePath);
                string? mine = HashOf(exePath);
                if (mine != null)
                {
                    foreach (var locked in l)
                    {
                        var lf = new FileInfo(locked);
                        if (lf.Exists && lf.Length == fi.Length)
                        {
                            string? other = HashOf(locked);
                            if (other != null && other.Equals(mine, StringComparison.OrdinalIgnoreCase))
                                return locked;
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    public static bool IsBlocked(string exePath)
    {
        string file = Path.GetFileName(exePath);
        return Blocked.Contains(file, StringComparer.OrdinalIgnoreCase) ||
               file.StartsWith("AppLocker.", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Locker.", StringComparison.OrdinalIgnoreCase);
    }

    // Eigulo lock korle Windows/AppLocker nijei bondho hoye jabe
    private static readonly string[] Blocked =
    {
        "explorer.exe", "svchost.exe", "winlogon.exe", "csrss.exe", "services.exe",
        "lsass.exe", "dwm.exe", "wininit.exe", "smss.exe", "applicationframehost.exe",
        "runtimebroker.exe", "sihost.exe", "taskhostw.exe", "ctfmon.exe"
    };

    // Locked app er exe onno jaygay copy ba rename kore chalale-o dhora: file size + SHA256 mile gele locked.
    private static readonly Dictionary<string, (long Len, DateTime Stamp, string Hash)> HashCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool MatchesLockedBinary(string exePath)
    {
        try
        {
            var l = List();
            if (l.Count == 0) return false;

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) && exePath.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return false;

            var fi = new FileInfo(exePath);
            if (!fi.Exists) return false;

            string? mine = null;
            foreach (var locked in l)
            {
                var lf = new FileInfo(locked);
                if (!lf.Exists || lf.Length != fi.Length) continue;   // size na mille hash-er dorkar nei

                mine ??= HashOf(exePath);
                if (mine == null) return false;
                string? other = HashOf(locked);
                if (other != null && other.Equals(mine, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string? HashOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            lock (Gate)
            {
                if (HashCache.TryGetValue(path, out var c) && c.Len == fi.Length && c.Stamp == fi.LastWriteTimeUtc)
                    return c.Hash;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
            lock (Gate) HashCache[path] = (fi.Length, fi.LastWriteTimeUtc, h);
            return h;
        }
        catch { return null; }
    }

    public static void Add(string exePath)
    {
        var l = List();
        string full = AppResolver.FixLauncher(Path.GetFullPath(exePath), null);
        string file = Path.GetFileName(full);
        if (IsBlocked(full))
            throw new InvalidOperationException("Ei system/AppLocker program lock kora jabe na: " + file);
        if (!l.Contains(full, StringComparer.OrdinalIgnoreCase)) l.Add(full);
        Save(l);
    }

    public static void Remove(string exePath)
    {
        var l = List();
        string full = Path.GetFullPath(exePath);
        l.RemoveAll(x => x.Equals(full, StringComparison.OrdinalIgnoreCase));
        Save(l);
    }

    private static void Save(List<string> l)
    {
        Directory.CreateDirectory(MasterConfig.ConfigDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(l));
        lock (Gate) { _stamp = default; _cachedPath = null; }
    }
}
