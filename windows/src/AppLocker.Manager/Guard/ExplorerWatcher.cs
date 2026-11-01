using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace AppLocker.Manager.Guard;

public record TabRecord(long Hwnd, string TargetFolder, string NavigatedAwayTo);

// Explorer e kono window/tab locked folder e dhukle dhore.
// Dhukle oi window ke parent e pathay ar onOpened(lockedFolder) call kore.
// Password thik hole Restore() diye oi-i tab ke abar folder e ferot pathay (notun tab khule na).
public class ExplorerWatcher
{
    private readonly Func<string, string?> _lockedFolderFor;
    private readonly Action<string> _onOpened;
    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly Dictionary<string, List<TabRecord>> _pendingTabs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _canceledFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _wake = new(false);

    public event Action<HashSet<string>>? ActiveFoldersChanged;

    public ExplorerWatcher(Func<string, string?> lockedFolderFor, Action<string> onOpened)
    {
        _lockedFolderFor = lockedFolderFor;
        _onOpened = onOpened;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ExplorerWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    public void Cancel(string folder)
    {
        _canceledFolders[folder] = DateTime.UtcNow;
        Forget(folder);
        NavigateAway(folder);
    }

    public void Restore(string folder, Action fallback)
    {
        _work.Enqueue(() =>
        {
            _canceledFolders.TryRemove(folder, out _);
            bool ok = false;
            dynamic? shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application")!;
                shell = Activator.CreateInstance(t);
            }
            catch { }

            if (_pendingTabs.Remove(folder, out var list) && list != null && (object?)shell != null)
            {
                foreach (var tab in list)
                {
                    if (shell == null) break;
                    foreach (dynamic w in shell.Windows())
                    {
                        try
                        {
                            string exe = w.FullName;
                            if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            long curHwnd = w.HWND;
                            if (curHwnd == tab.Hwnd)
                            {
                                bool forwarded = false;
                                try
                                {
                                    w.GoForward();
                                    string nu = w.LocationURL;
                                    if (!string.IsNullOrEmpty(nu))
                                    {
                                        string np = new Uri(nu).LocalPath.TrimEnd('\\');
                                        if (np.Equals(tab.TargetFolder, StringComparison.OrdinalIgnoreCase))
                                        {
                                            forwarded = true;
                                        }
                                    }
                                }
                                catch { }

                                if (!forwarded)
                                {
                                    w.Navigate2(tab.TargetFolder);
                                }
                                ok = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (!ok && shell != null)
            {
#pragma warning disable CS8602 // shell is a COM object used through dynamic; null was checked above
                foreach (dynamic w in shell.Windows())
#pragma warning restore CS8602
                {
                    try
                    {
                        string exe = w.FullName;
                        if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        w.Navigate2(folder);
                        ok = true;
                        break;
                    }
                    catch { }
                }
            }

            if (!ok) fallback();
        });
        _wake.Set();
    }

    public void NavigateAway(string folder)
    {
        _work.Enqueue(() =>
        {
            try
            {
                string? parent = Directory.GetParent(folder)?.FullName;
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    parent = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                dynamic shell;
                var t = Type.GetTypeFromProgID("Shell.Application")!;
                shell = Activator.CreateInstance(t)!;
                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        string exe = w.FullName;
                        if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string url = w.LocationURL;
                        if (string.IsNullOrEmpty(url)) continue;
                        string path = new Uri(url).LocalPath.TrimEnd('\\');
                        if (path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                        {
                            bool rolledBack = false;
                            try
                            {
                                w.GoBack();
                                rolledBack = true;
                            }
                            catch { }

                            if (!rolledBack)
                            {
                                try { w.Navigate2(parent); }
                                catch
                                {
                                    try { w.Navigate2(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)); }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        });
        _wake.Set();
    }

    public void Forget(string folder)
    {
        _work.Enqueue(() => { _pendingTabs.Remove(folder); });
        _wake.Set();
    }

    private void Loop()
    {
        dynamic shell;
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application")!;
            shell = Activator.CreateInstance(t)!;
        }
        catch { return; }

        while (!_stop)
        {
            try
            {
                while (_work.TryDequeue(out var job)) job();
                // Idle skip: if no locked folders tracked and no pending tabs, poll less often
                bool hasWork = _pendingTabs.Count > 0 || _canceledFolders.Count > 0;
                if (hasWork)
                    Poll(shell);
                else
                    Poll(shell); // Always poll for ActiveFoldersChanged; just sleep longer when idle
            }
            catch { }
            // Sleep: shorter when active tabs/cancel pending, slightly longer when idle
            bool idle = _pendingTabs.Count == 0 && _canceledFolders.Count == 0;
            _wake.WaitOne(idle ? 600 : 220);
        }
    }

    private void Poll(dynamic shell)
    {
        var wins = new List<(object win, string path)>();
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (dynamic w in shell.Windows())
        {
            try
            {
                string exe = w.FullName;
                if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string url = w.LocationURL;
                if (string.IsNullOrEmpty(url)) continue;
                string path = new Uri(url).LocalPath.TrimEnd('\\');
                wins.Add((w, path));
                activePaths.Add(path);
            }
            catch { }
        }

        ActiveFoldersChanged?.Invoke(activePaths);

        var interceptedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (win, path) in wins)
        {
            try
            {
                string? locked = _lockedFolderFor(path);
                if (locked == null) continue;

                string? parent = Directory.GetParent(locked)?.FullName;
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                    parent = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                // If user canceled prompt for this folder, ensure window navigates away but DO NOT re-prompt
                if (_canceledFolders.ContainsKey(locked))
                {
                    bool rolledBack = false;
                    try
                    {
                        ((dynamic)win).GoBack();
                        rolledBack = true;
                    }
                    catch { }

                    if (!rolledBack)
                    {
                        try { ((dynamic)win).Navigate2(parent); }
                        catch
                        {
                            try { ((dynamic)win).Navigate2(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)); }
                            catch { }
                        }
                    }
                    continue;
                }

                // This window is inside a locked folder!
                long hwnd = ((dynamic)win).HWND;

                // 1. Immediately navigate away so files cannot be seen or clicked
                bool navigatedAway = false;
                try
                {
                    ((dynamic)win).GoBack();
                    navigatedAway = true;
                }
                catch { }

                if (!navigatedAway)
                {
                    try { ((dynamic)win).Navigate2(parent); }
                    catch
                    {
                        try { ((dynamic)win).Navigate2(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)); }
                        catch { }
                    }
                }

                if (!_pendingTabs.TryGetValue(locked, out var list))
                    _pendingTabs[locked] = list = new();

                if (!list.Any(t => t.Hwnd == hwnd))
                {
                    list.Add(new TabRecord(hwnd, path, parent));
                }

                interceptedFolders.Add(locked);
            }
            catch { }
        }

        foreach (var locked in interceptedFolders)
        {
            _onOpened(locked);
        }

        // Clean up canceled folders only after the user has completely navigated away from them
        // and at least 1.5 seconds have passed since cancel
        var now = DateTime.UtcNow;
        foreach (var kvp in _canceledFolders.ToArray())
        {
            string f = kvp.Key;
            DateTime canceledAt = kvp.Value;
            if ((now - canceledAt).TotalMilliseconds < 1500) continue;

            bool stillInside = activePaths.Any(p =>
                p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));

            if (!stillInside)
            {
                _canceledFolders.TryRemove(f, out _);
            }
        }
    }
}
