using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace AppLocker.Manager.Guard;

public record TabRecord(long Hwnd, string TargetFolder, string NavigatedAwayTo, object? ComWindow);
public record DialogRecord(long Hwnd, string TargetFolder, string NavigatedAwayTo);

// Explorer e kono window/tab ba Windows File Picker (#32770) locked folder e dhukle dhore.
// Dhukle oi window ke parent e pathay ar onOpened(lockedFolder) call kore.
// Password thik hole Restore() diye oi-i tab/dialog ke abar folder e ferot pathay.
public class ExplorerWatcher
{
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private const uint GA_ROOT = 2;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_BROWSER_BACKWARD = 1;
    private const int IDCANCEL = 2;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_ALERTABLE = 0x0002;
    private const uint PM_REMOVE = 0x0001;

    private readonly Func<string, string?> _lockedFolderFor;
    private readonly Action<string> _onOpened;
    private readonly Thread _thread;
    private volatile bool _stop;
    private WinEventDelegate? _winEventProc;

    private readonly Dictionary<string, List<TabRecord>> _pendingTabs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DialogRecord>> _pendingDialogs =
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

            if (!ok && _pendingDialogs.Remove(folder, out var dlgList) && dlgList != null)
            {
                foreach (var dlg in dlgList)
                {
                    IntPtr hwnd = new IntPtr(dlg.Hwnd);
                    if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) continue;
                    SetForegroundWindow(hwnd);
                    TryClickDialogNavForward(hwnd);
                    _lastActiveSubpathByFolder[norm] = Directory.Exists(dlg.TargetFolder) ? dlg.TargetFolder : folder;
                    ok = true;
                    break;
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
                        if (IsInsideOrEqual(path, folder))
                        {
                            NavigateWindowAway(w, folder, parent);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                string parent = SafeParent(folder);
                foreach (var (hwnd, path) in EnumerateOpenFileDialogs())
                {
                    if (IsInsideOrEqual(path, folder))
                    {
                        NavigateDialogAway(hwnd, folder, parent);
                    }
                }
            }
            catch { }
        });
        _wake.Set();
    }

    public void Forget(string folder)
    {
        _work.Enqueue(() =>
        {
            _pendingTabs.Remove(folder);
            _pendingDialogs.Remove(folder);
        });
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

    private static bool IsInsideOrEqual(string path, string folder)
    {
        string p = path.TrimEnd('\\');
        string f = folder.TrimEnd('\\');
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : "";
    }

    private static string GetWindowTitleText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : "";
    }

    private static bool IsWatchRelevantClass(string cls) =>
        cls is "CabinetWClass" or "ExploreWClass" or "#32770" or "DirectUIHWND" or "UIItemsView" or "ToolbarWindow32" or "Breadcrumb Parent";

    private static bool IsForegroundExplorerOrDialog()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        string cls = GetWindowClassName(fg);
        return cls is "CabinetWClass" or "ExploreWClass" or "#32770";
    }

    private static void NavigateWindowAway(dynamic win, string lockedFolder, string parent)
    {
        // Navigate directly to parent first so locked files don't stay visible waiting on async WM_APPCOMMAND.
        try
        {
            win.Navigate2(parent);
            for (int i = 0; i < 4; i++)
            {
                if (TryGetLocalPath(win.LocationURL as string, out string nowPath) &&
                    !IsInsideOrEqual(nowPath, lockedFolder))
                {
                    return;
                }
                Thread.Sleep(15);
            }
        }
        catch { }

        try
        {
            long hwndVal = win.HWND;
            if (hwndVal != 0)
            {
                IntPtr hWnd = new IntPtr(hwndVal);
                PostMessage(hWnd, WM_APPCOMMAND, hWnd, (IntPtr)(APPCOMMAND_BROWSER_BACKWARD << 16));
                for (int i = 0; i < 4; i++)
                {
                    Thread.Sleep(25);
                    if (TryGetLocalPath(win.LocationURL as string, out string nowPath) &&
                        !IsInsideOrEqual(nowPath, lockedFolder))
                    {
                        return;
                    }
                }
            }
        }
        catch { }

        try { win.GoBack(); } catch { }

        try
        {
            if (TryGetLocalPath(win.LocationURL as string, out string nowPath) &&
                IsInsideOrEqual(nowPath, lockedFolder))
            {
                win.Navigate2(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            }
        }
        catch { }
    }

    private static List<(IntPtr Hwnd, string Path)> EnumerateOpenFileDialogs()
    {
        var results = new List<(IntPtr, string)>();
        uint selfPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindowClassName(hwnd) != "#32770") return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == selfPid)
            {
                string title = GetWindowTitleText(hwnd);
                if (title.StartsWith("Select folder", StringComparison.OrdinalIgnoreCase) ||
                    title.StartsWith("Select Vault", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (TryExtractFileDialogPath(hwnd, out string path))
            {
                results.Add((hwnd, path));
            }
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static bool TryExtractFileDialogPath(IntPtr dlgHwnd, out string path)
    {
        path = "";
        try
        {
            var winEl = AutomationElement.FromHandle(dlgHwnd);
            if (winEl == null) return false;

            var addrEl = winEl.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"));
            if (addrEl == null) return false;

            string? rawName = addrEl.Current.Name;
            if (string.IsNullOrWhiteSpace(rawName)) return false;

            string candidate = rawName.Trim();
            int colonIdx = candidate.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx >= 0)
                candidate = candidate[(colonIdx + 2)..].Trim();

            if (!Path.IsPathRooted(candidate)) return false;
            path = candidate.TrimEnd('\\');
            return !string.IsNullOrEmpty(path);
        }
        catch
        {
            return false;
        }
    }

    private static void NavigateDialogAway(IntPtr dlgHwnd, string lockedFolder, string parent)
    {
        try
        {
            var winEl = AutomationElement.FromHandle(dlgHwnd);
            if (winEl != null)
            {
                var toolbars = winEl.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "ToolbarWindow32"));

                IntPtr upHwnd = IntPtr.Zero;
                foreach (AutomationElement tb in toolbars)
                {
                    if (string.Equals(tb.Current.Name, "Up band", StringComparison.OrdinalIgnoreCase))
                    {
                        upHwnd = new IntPtr(tb.Current.NativeWindowHandle);
                        break;
                    }
                }

                if (upHwnd != IntPtr.Zero)
                {
                    for (int step = 0; step < 4; step++)
                    {
                        ClickToolbarPoint(upHwnd, 12, 12);
                        Thread.Sleep(45);
                        if (TryExtractFileDialogPath(dlgHwnd, out string cur) &&
                            !IsInsideOrEqual(cur, lockedFolder))
                        {
                            return;
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback: try Back command or cancel dialog only if it refuses to leave the locked directory
        try
        {
            PostMessage(dlgHwnd, WM_APPCOMMAND, dlgHwnd, (IntPtr)(APPCOMMAND_BROWSER_BACKWARD << 16));
            Thread.Sleep(50);
            if (TryExtractFileDialogPath(dlgHwnd, out string cur) &&
                !IsInsideOrEqual(cur, lockedFolder))
            {
                return;
            }
        }
        catch { }

        try
        {
            if (TryExtractFileDialogPath(dlgHwnd, out string still) && IsInsideOrEqual(still, lockedFolder))
            {
                PostMessage(dlgHwnd, WM_COMMAND, new IntPtr(IDCANCEL), IntPtr.Zero);
            }
        }
        catch { }
    }

    private static void TryClickDialogNavForward(IntPtr dlgHwnd)
    {
        try
        {
            var winEl = AutomationElement.FromHandle(dlgHwnd);
            if (winEl == null) return;
            var toolbars = winEl.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "ToolbarWindow32"));
            foreach (AutomationElement tb in toolbars)
            {
                if (string.Equals(tb.Current.Name, "Navigation buttons", StringComparison.OrdinalIgnoreCase))
                {
                    var rect = tb.Current.BoundingRectangle;
                    int fx = rect.Width > 10 ? (int)Math.Round(rect.Width * 0.72) : 46;
                    int fy = rect.Height > 6 ? (int)Math.Round(rect.Height * 0.5) : 12;
                    ClickToolbarPoint(new IntPtr(tb.Current.NativeWindowHandle), fx, fy);
                    return;
                }
            }
        }
        catch { }
    }

    private static void ClickToolbarPoint(IntPtr hwnd, int x, int y)
    {
        IntPtr lParam = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        PostMessage(hwnd, WM_LBUTTONDOWN, new IntPtr(1), lParam);
        PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    private void Loop()
    {
        dynamic? shell = GetShell();
        if (shell == null) return;

        _winEventProc = OnWinEvent;
        IntPtr hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        IntPtr hookShow = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        IntPtr hookObj = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        try
        {
            while (!_stop)
            {
                try
                {
                    while (_work.TryDequeue(out var job)) job();
                    Poll(shell);
                }
                catch { }

                bool active = _pendingTabs.Count > 0
                    || _pendingDialogs.Count > 0
                    || _canceledFolders.Count > 0
                    || IsForegroundExplorerOrDialog();
                uint waitMs = active ? 45u : 300u;

                IntPtr[] handles = { _wake.SafeWaitHandle.DangerousGetHandle() };
                uint res = MsgWaitForMultipleObjectsEx(1, handles, waitMs, QS_ALLINPUT, MWMO_ALERTABLE);
                if (res == 1)
                {
                    while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
            }
        }
        finally
        {
            if (hookFg != IntPtr.Zero) UnhookWinEvent(hookFg);
            if (hookShow != IntPtr.Zero) UnhookWinEvent(hookShow);
            if (hookObj != IntPtr.Zero) UnhookWinEvent(hookObj);
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (eventType == EVENT_SYSTEM_FOREGROUND)
            {
                if (IsWatchRelevantClass(GetWindowClassName(hwnd)))
                    _wake.Set();
                return;
            }
            string cls = GetWindowClassName(hwnd);
            if (IsWatchRelevantClass(cls))
            {
                _wake.Set();
                return;
            }
            IntPtr root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero && root != hwnd && IsWatchRelevantClass(GetWindowClassName(root)))
                _wake.Set();
        }
        catch { }
    }

    private void Poll(dynamic shell)
    {
        var wins = new List<(object win, string path)>();
        var dialogs = new List<(IntPtr hwnd, string path)>();
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

        try
        {
            dialogs = EnumerateOpenFileDialogs();
            foreach (var (_, path) in dialogs)
            {
                activePaths.Add(path);
            }
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

                if (IsRecentlyCanceled(locked))
                {
                    NavigateWindowAway((dynamic)win, locked, parent);
                    continue;
                }

                long hwnd = ((dynamic)win).HWND;
                NavigateWindowAway((dynamic)win, locked, parent);

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

        foreach (var (dlgHwnd, path) in dialogs)
        {
            try
            {
                string? locked = _lockedFolderFor(path);
                if (locked == null) continue;

                string parent = SafeParent(locked);

                if (IsRecentlyCanceled(locked))
                {
                    NavigateDialogAway(dlgHwnd, locked, parent);
                    continue;
                }

                NavigateDialogAway(dlgHwnd, locked, parent);

                if (!_pendingDialogs.TryGetValue(locked, out var dlist))
                    _pendingDialogs[locked] = dlist = new();

                long hwndVal = dlgHwnd.ToInt64();
                if (!dlist.Any(d => d.Hwnd == hwndVal && d.TargetFolder.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    dlist.Add(new DialogRecord(hwndVal, path, parent));
                }

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

            bool stillInside = activePaths.Any(p => IsInsideOrEqual(p, f));
            if (!stillInside)
            {
                _canceledFolders.TryRemove(f, out _);
            }
        }
    }

    private bool IsRecentlyCanceled(string locked)
    {
        if (!_canceledFolders.TryGetValue(locked, out var canceledAt)) return false;
        if ((DateTime.UtcNow - canceledAt).TotalMilliseconds < 1500) return true;
        _canceledFolders.TryRemove(locked, out _);
        return false;
    }
}
