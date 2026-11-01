using System.Collections.Concurrent;
using System.IO;

namespace AppLocker.UI;

public record TabRecord(long Hwnd, string TargetFolder, string NavigatedAwayTo);

// Explorer e kono window/tab locked folder e dhukle dhore.
// Dhukle oi window ke parent e pathay ar onOpened(lockedFolder) call kore.
// Password thik hole Restore() diye oi-i tab ke abar folder e ferot pathay (notun tab khule na).
public class ExplorerWatcher
{
    private readonly Func<string, string?> _lockedFolderFor; // path -> locked folder, na hole null
    private readonly Action<string> _onOpened;
    private readonly Thread _thread;
    private volatile bool _stop;
    private Dictionary<string, int> _prev = new(StringComparer.OrdinalIgnoreCase);

    // Je tab gulo ke shoriye diyechi, tader HWND o target path mone rakhi
    private readonly Dictionary<string, List<TabRecord>> _pendingTabs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _wake = new(false);

    // Explorer window gulo kon kon path e ache ta jananor event
    public event Action<HashSet<string>>? ActiveFoldersChanged;

    public ExplorerWatcher(Func<string, string?> lockedFolderFor, Action<string> onOpened)
    {
        _lockedFolderFor = lockedFolderFor;
        _onOpened = onOpened;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ExplorerWatcher" };
        _thread.SetApartmentState(ApartmentState.STA); // Shell COM er jonno lagbe
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    // Unlock hoyeche: shoriye deya tab gulo ke abar folder e ferot pathai (same tab navigation).
    // Notun tab khula bondho korte live Explorer instance query kore navigate kore.
    public void Restore(string folder, Action fallback)
    {
        _work.Enqueue(() =>
        {
            bool ok = false;
            dynamic? shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application")!;
                shell = Activator.CreateInstance(t);
            }
            catch { }

            if (_pendingTabs.Remove(folder, out var list) && shell != null)
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

            // Kono direct HWND match na pele, jodi kono Explorer window open thake,
            // tokhon oi window er current tab e navigate korbo (notun tab create na kore)
            if (!ok && shell != null)
            {
                foreach (dynamic w in shell.Windows())
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

            // Jodi Explorer ekdom running na thake, shudhu tokhon fallback
            if (!ok) fallback();
        });
        _wake.Set();
    }

    // Lock now chapa hole: oi folder e thaka Explorer window ke pichone/parent e pathai
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
                                string nu = w.LocationURL;
                                if (!string.IsNullOrEmpty(nu))
                                {
                                    string np = new Uri(nu).LocalPath.TrimEnd('\\');
                                    if (!np.Equals(folder, StringComparison.OrdinalIgnoreCase) &&
                                        !np.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                                    {
                                        rolledBack = true;
                                    }
                                }
                            }
                            catch { }

                            if (!rolledBack)
                            {
                                w.Navigate2(parent);
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

    // Unlock hoyni (cancel/bhul): shoriye deya tab er record bhule jai
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
                Poll(shell);
            }
            catch { }
            _wake.WaitOne(350);
        }
    }

    private void Poll(dynamic shell)
    {
        var wins = new List<(object win, string path)>();
        var cur = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                cur[path] = cur.GetValueOrDefault(path) + 1;
                activePaths.Add(path);
            }
            catch { }
        }

        ActiveFoldersChanged?.Invoke(activePaths);

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, count) in cur)
        {
            // Ei path e age kom window chilo, ekhon beshi = notun kore dhukeche
            if (count <= _prev.GetValueOrDefault(path)) continue;

            string? locked = _lockedFolderFor(path);
            if (locked == null || !done.Add(locked)) continue;

            // Oi path e thaka window gulo parent e pathai, ar mone rakhi
            string? parent = Directory.GetParent(locked)?.FullName;
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                parent = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            if (!_pendingTabs.TryGetValue(locked, out var list))
                _pendingTabs[locked] = list = new();

            foreach (var (win, p) in wins)
            {
                if (!p.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    long hwnd = ((dynamic)win).HWND;
                    bool rolledBack = false;
                    try
                    {
                        ((dynamic)win).GoBack();
                        string nu = ((dynamic)win).LocationURL;
                        if (!string.IsNullOrEmpty(nu))
                        {
                            string np = new Uri(nu).LocalPath.TrimEnd('\\');
                            if (!np.Equals(locked, StringComparison.OrdinalIgnoreCase) &&
                                !np.StartsWith(locked + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                rolledBack = true;
                            }
                        }
                    }
                    catch { }

                    if (!rolledBack)
                    {
                        ((dynamic)win).Navigate2(parent);
                    }
                    list.Add(new TabRecord(hwnd, p, parent));
                }
                catch { }
            }
            _onOpened(locked);
        }

        _prev = cur;
    }
}
