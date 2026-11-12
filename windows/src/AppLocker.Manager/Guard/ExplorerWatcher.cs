using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace AppLocker.Manager.Guard;

public record TabRecord(long Hwnd, string TargetFolder, string NavigatedAwayTo, object? ComWindow);

// Explorer e kono window/tab locked folder e dhukle dhore.
// Dhukle oi window ke parent e pathay ar onOpened(lockedFolder) call kore.
// Password thik hole Restore() diye oi-i tab ke abar folder e ferot pathay (notun tab khule na).
public class ExplorerWatcher
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_BROWSER_BACKWARD = 1;

    private readonly Func<string, string?> _lockedFolderFor;
    private readonly Action<string> _onOpened;
    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly Dictionary<string, List<TabRecord>> _pendingTabs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _canceledFolders =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastActiveSubpathByFolder =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _explorerHwndByFolder =
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
            dynamic? shell = GetShell();
            string norm = folder.TrimEnd('\\');

            // 1. If an Explorer window is ALREADY inside this folder, simply focus it and track it
            if (shell != null)
            {
                try
                {
                    foreach (dynamic w in shell.Windows())
                    {
                        try
                        {
                            string exe = w.FullName;
                            if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (TryGetLocalPath(w.LocationURL as string, out string curPath) &&
                                (curPath.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                 curPath.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase)))
                            {
                                long curHwnd = w.HWND;
                                if (curHwnd != 0) SetForegroundWindow(new IntPtr(curHwnd));
                                _lastActiveSubpathByFolder[norm] = curPath;
                                _explorerHwndByFolder[norm] = curHwnd;
                                ok = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            if (!ok && _pendingTabs.Remove(folder, out var list) && list != null && (object?)shell != null)
            {
                foreach (var tab in list)
                {
                    try
                    {
                        string dest = Directory.Exists(tab.TargetFolder) ? tab.TargetFolder : folder;

                        // 1. Prefer the exact COM WebBrowser tab reference we intercepted so Win 11 multi-tab
                        //    windows don't navigate a wrong/background tab sharing the same outer HWND.
                        if (tab.ComWindow != null && TryNavigateTabAndVerify((dynamic)tab.ComWindow, dest, tab.Hwnd))
                        {
                            _lastActiveSubpathByFolder[norm] = dest;
                            _explorerHwndByFolder[norm] = tab.Hwnd;
                            ok = true;
                            break;
                        }

                        // 2. Fallback to matching HWND inside ShellWindows
                        foreach (dynamic w in shell.Windows())
                        {
                            try
                            {
                                string exe = w.FullName;
                                if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                                long curHwnd = w.HWND;
                                if (curHwnd == tab.Hwnd && TryNavigateTabAndVerify(w, dest, curHwnd))
                                {
                                    _lastActiveSubpathByFolder[norm] = dest;
                                    _explorerHwndByFolder[norm] = curHwnd;
                                    ok = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (ok) break;
                    }
                    catch { }
                }
            }

            if (!ok && shell != null)
            {
                try
                {
                    string parent = SafeParent(folder);
#pragma warning disable CS8602 // shell is a COM object used through dynamic; null was checked above
                    foreach (dynamic w in shell.Windows())
#pragma warning restore CS8602
                    {
                        try
                        {
                            string exe = w.FullName;
                            if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            if (TryGetLocalPath(w.LocationURL as string, out string curPath) &&
                                (curPath.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                                 curPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                            {
                                long curHwnd = w.HWND;
                                if (TryNavigateTabAndVerify(w, folder, curHwnd))
                                {
                                    _lastActiveSubpathByFolder[norm] = folder;
                                    _explorerHwndByFolder[norm] = curHwnd;
                                    ok = true;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            if (!ok) fallback();
        });
        _wake.Set();
    }

    private static bool TryNavigateTabAndVerify(dynamic win, string dest, long hwnd)
    {
        try
        {
            string normDest = dest.TrimEnd('\\');
            if (TryGetLocalPath(win.LocationURL as string, out string already) &&
                already.Equals(normDest, StringComparison.OrdinalIgnoreCase))
            {
                if (hwnd != 0) SetForegroundWindow(new IntPtr(hwnd));
                return true;
            }

            win.Navigate2(dest);
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(35);
                if (TryGetLocalPath(win.LocationURL as string, out string nowPath) &&
                    (nowPath.Equals(normDest, StringComparison.OrdinalIgnoreCase) ||
                     nowPath.StartsWith(normDest + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    if (hwnd != 0) SetForegroundWindow(new IntPtr(hwnd));
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Tracks active subfolder paths and Explorer HWNDs so Win 11 background tabs can be detected
    /// via UI Automation even when ShellWindows only reports the foreground tab's LocationURL.
    /// </summary>
    public void NoteOpenFolderPath(string rootFolder, string activePath)
    {
        string key = rootFolder.TrimEnd('\\');
        _lastActiveSubpathByFolder[key] = activePath.TrimEnd('\\');
    }

    public bool IsFolderTabOpen(string folder)
    {
        string norm = folder.TrimEnd('\\');
        string rootName = Path.GetFileName(norm);
        string? subName = null;
        if (_lastActiveSubpathByFolder.TryGetValue(norm, out string? lastSub))
        {
            string fn = Path.GetFileName(lastSub.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(fn)) subName = fn;
        }

        try
        {
            var tabCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

            // Fast check on the Explorer window where this folder was opened
            if (_explorerHwndByFolder.TryGetValue(norm, out long knownHwnd) &&
                knownHwnd != 0 && IsWindow(new IntPtr(knownHwnd)))
            {
                try
                {
                    var el = AutomationElement.FromHandle(new IntPtr(knownHwnd));
                    if (el != null && WindowHasMatchingTab(el, tabCond, rootName, subName))
                        return true;
                }
                catch { }
            }

            // Fallback: inspect any top-level Explorer CabinetWClass window
            var winCond = new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass");
            var wins = AutomationElement.RootElement.FindAll(TreeScope.Children, winCond);
            foreach (AutomationElement w in wins)
            {
                try
                {
                    if (WindowHasMatchingTab(w, tabCond, rootName, subName))
                        return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static bool WindowHasMatchingTab(AutomationElement winEl, Condition tabCond, string rootName, string? subName)
    {
        var tabs = winEl.FindAll(TreeScope.Descendants, tabCond);
        foreach (AutomationElement t in tabs)
        {
            string? title = t.Current.Name;
            if (string.IsNullOrWhiteSpace(title)) continue;
            string clean = title.Trim();
            if (clean.Equals(rootName, StringComparison.OrdinalIgnoreCase)) return true;
            if (subName != null && clean.Equals(subName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void ForgetFolderTracking(string folder)
    {
        string norm = folder.TrimEnd('\\');
        _lastActiveSubpathByFolder.TryRemove(norm, out _);
        _explorerHwndByFolder.TryRemove(norm, out _);
    }

    public void NavigateAway(string folder)
    {
        _work.Enqueue(() =>
        {
            try
            {
                string parent = SafeParent(folder);
                dynamic? shell = GetShell();
                if (shell == null) return;

                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        string exe = w.FullName;
                        if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!TryGetLocalPath(w.LocationURL as string, out string path)) continue;
                        if (path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                        {
                            NavigateWindowAway(w, parent);
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

    private dynamic? _shell;

    private dynamic? GetShell()
    {
        if (_shell != null) return _shell;
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application")!;
            _shell = Activator.CreateInstance(t);
        }
        catch { }
        return _shell;
    }

    private static string SafeParent(string folder)
    {
        string? parent = Directory.GetParent(folder)?.FullName;
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return parent;
    }

    private static bool TryGetLocalPath(string? url, out string path)
    {
        path = "";
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        path = uri.LocalPath.TrimEnd('\\');
        return !string.IsNullOrEmpty(path);
    }

    private static void NavigateWindowAway(dynamic win, string parent)
    {
        try
        {
            long hwndVal = win.HWND;
            if (hwndVal != 0)
            {
                IntPtr hWnd = new IntPtr(hwndVal);
                PostMessage(hWnd, WM_APPCOMMAND, hWnd, (IntPtr)(APPCOMMAND_BROWSER_BACKWARD << 16));
                for (int i = 0; i < 5; i++)
                {
                    Thread.Sleep(35);
                    if (TryGetLocalPath(win.LocationURL as string, out string nowPath) &&
                        !nowPath.Equals(parent, StringComparison.OrdinalIgnoreCase) &&
                        !nowPath.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
        }
        catch { }

        try
        {
            win.GoBack();
            return;
        }
        catch { }

        try
        {
            win.Navigate2(parent);
            return;
        }
        catch { }

        try { win.Navigate2(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)); }
        catch { }
    }

    private void Loop()
    {
        dynamic? shell = GetShell();
        if (shell == null) return;

        while (!_stop)
        {
            try
            {
                while (_work.TryDequeue(out var job)) job();
                Poll(shell);
            }
            catch { }
            bool idle = _pendingTabs.Count == 0 && _canceledFolders.Count == 0;
            _wake.WaitOne(idle ? 600 : 220);
        }
    }

    private void Poll(dynamic shell)
    {
        var wins = new List<(object win, string path)>();
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool enumerated = false;

        try
        {
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    string exe = w.FullName;
                    if (!exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!TryGetLocalPath(w.LocationURL as string, out string path)) continue;
                    wins.Add((w, path));
                    activePaths.Add(path);
                }
                catch { }
            }
            enumerated = true;
        }
        catch { }

        if (enumerated)
        {
            ActiveFoldersChanged?.Invoke(activePaths);
        }

        var interceptedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (win, path) in wins)
        {
            try
            {
                string? locked = _lockedFolderFor(path);
                if (locked == null) continue;

                string parent = SafeParent(locked);

                // If user canceled prompt for this folder, ensure window navigates away but DO NOT re-prompt
                // within 1.5 seconds while it is actively leaving the folder
                if (_canceledFolders.TryGetValue(locked, out var canceledAt))
                {
                    if ((DateTime.UtcNow - canceledAt).TotalMilliseconds < 1500)
                    {
                        NavigateWindowAway((dynamic)win, parent);
                        continue;
                    }
                    else
                    {
                        _canceledFolders.TryRemove(locked, out _);
                    }
                }

                // This window is inside a locked folder!
                long hwnd = ((dynamic)win).HWND;

                // 1. Immediately navigate away so files cannot be seen or clicked
                NavigateWindowAway((dynamic)win, parent);

                if (!_pendingTabs.TryGetValue(locked, out var list))
                    _pendingTabs[locked] = list = new();

                if (!list.Any(t => t.Hwnd == hwnd && t.TargetFolder.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new TabRecord(hwnd, path, parent, win));
                }

                _explorerHwndByFolder[locked.TrimEnd('\\')] = hwnd;
                _lastActiveSubpathByFolder[locked.TrimEnd('\\')] = path;
                interceptedFolders.Add(locked);
            }
            catch { }
        }

        foreach (var locked in interceptedFolders)
        {
            _onOpened(locked);
        }

        // Clean up canceled folders as soon as the user has completely navigated away from them
        // and at least 1.0 second has passed since cancel
        var now = DateTime.UtcNow;
        foreach (var kvp in _canceledFolders.ToArray())
        {
            string f = kvp.Key;
            DateTime canceledAt = kvp.Value;
            if ((now - canceledAt).TotalMilliseconds < 1000) continue;

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
