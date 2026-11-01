using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using AppLocker.Core;

namespace AppLocker.Manager.Guard;

// Locked app shuru hole thamiye (suspend) password chay.
// Thik hole chalu kore dey, bhul/cancel hole kill kore.
// Kono hardcode chara 100% native process-tree & window lifecycle detection:
// 1. App-er visible window thakle notun tabs/subprocesses allowed.
// 2. Tray app (Discord/Spotify) background-e internal worker spawn korle (ancestor in unlocked) allowed.
// 3. User window close korar por shell theke abar app launch korle (Chrome, etc.) songe songe password chaibe.
public class AppGuard : IDisposable
{
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private readonly Func<string, Task<bool>> _askUnlock;
    private ManagementEventWatcher? _watcher;
    private System.Threading.Timer? _sweepTimer;
    private int _sweeping;
    private readonly object _lock = new();

    // Canonical locked path -> PIDs
    private readonly Dictionary<string, HashSet<int>> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    // Canonical locked path -> waiting processes
    private readonly Dictionary<string, List<Process>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hadWindow = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Relocked;

    public AppGuard(Func<string, Task<bool>> askUnlock) => _askUnlock = askUnlock;

    public bool Start(out string error)
    {
        error = "";
        try
        {
            _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _watcher.EventArrived += OnStart;
            _watcher.Start();
            Log("Watcher started");
            _sweepTimer = new System.Threading.Timer(_ => Sweep(), null, 0, 700);
            return true;
        }
        catch (UnauthorizedAccessException) { error = "Admin lagbe"; }
        catch (ManagementException ex) { error = ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "Admin lagbe" : ex.Message; }
        catch (Exception ex) { error = ex.Message; }
        return false;
    }

    // Force relock and terminate running processes
    public void Relock(string path)
    {
        string canon = AppRegistry.FindLockedMatch(path) ?? Path.GetFullPath(path.Trim().Trim('"'));
        lock (_lock)
        {
            _unlocked.Remove(canon);
            _hadWindow.Remove(canon);
            if (_waiting.Remove(canon, out var waiting))
            {
                foreach (var p in waiting)
                {
                    try { p.Kill(true); } catch { }
                    finally { p.Dispose(); }
                }
            }
        }
        KillAllProcesses(canon);
        Log($"FORCE RELOCKED & TERMINATED: {canon}");
        Relocked?.Invoke(canon);
    }

    // Check if an app currently has an active GUI window (visible or minimized on taskbar)
    public static bool AppHasVisibleWindow(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0) return false;
        var pids = new HashSet<uint>();
        foreach (var p in procs)
        {
            try
            {
                string? pPath = ImagePath(p.Id);
                if (pPath != null && (pPath.Equals(exePath, StringComparison.OrdinalIgnoreCase) || AppRegistry.IsLocked(pPath)))
                {
                    pids.Add((uint)p.Id);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (pids.Count == 0) return false;

        bool hasVisible = false;
        try
        {
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint wPid);
                if (pids.Contains(wPid))
                {
                    // Minimized to taskbar is an active window, not closed!
                    if (IsIconic(hWnd))
                    {
                        hasVisible = true;
                        return false;
                    }
                    if (GetWindowRect(hWnd, out RECT r))
                    {
                        int w = r.Right - r.Left;
                        int h = r.Bottom - r.Top;
                        if (w > 80 && h > 80)
                        {
                            hasVisible = true;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return hasVisible;
    }

    public static void KillAllProcesses(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? pPath = ImagePath(p.Id);
                    if (pPath != null && (pPath.Equals(exePath, StringComparison.OrdinalIgnoreCase) || AppRegistry.IsLocked(pPath)))
                    {
                        p.Kill(true);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private static bool PathRunning(string exePath)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(exePath);
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? pPath = ImagePath(p.Id);
                    if (pPath != null && (pPath.Equals(exePath, StringComparison.OrdinalIgnoreCase) || AppRegistry.IsLocked(pPath)))
                    {
                        return true;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    private static int ParentPidOf(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return 0;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : 0;
        }
        catch { return 0; }
        finally { CloseHandle(h); }
    }

    // Process tar parent/ancestor ki unlocked session er kono process?
    private static bool HasTrackedAncestor(HashSet<int> tracked, int pid)
    {
        int cur = ParentPidOf(pid);
        for (int depth = 0; depth < 8 && cur > 4; depth++)
        {
            if (tracked.Contains(cur)) return true;
            cur = ParentPidOf(cur);
        }
        return false;
    }

    // Debug log: C:\ProgramData\AppLocker\guard.log
    private static readonly object LogGate = new();
    private static void Log(string msg)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(MasterConfig.ConfigDir);
                File.AppendAllText(Path.Combine(MasterConfig.ConfigDir, "guard.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + msg + Environment.NewLine);
            }
        }
        catch { }
    }

    private void OnStart(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
            string pname = Convert.ToString(e.NewEvent["ProcessName"]) ?? "";
            _ = HandleStart(pid, pname);
        }
        catch { }
    }

    private async Task HandleStart(int pid, string pname)
    {
        string? rawPath = ImagePath(pid);
        if (rawPath == null) return;

        string? canon = AppRegistry.FindLockedMatch(rawPath);
        if (canon == null) return; // Not a locked app

        Process p;
        try { p = Process.GetProcessById(pid); }
        catch { return; }

        bool ask = false;
        lock (_lock)
        {
            if (Known(pid)) return; // already tracked

            // App age thekei unlocked thakle check koro:
            if (_unlocked.TryGetValue(canon, out var set))
            {
                // Case 1: App tar active visible window screen-e ache (jemon Chrome khola ache, notun tab/renderer khullo)
                if (AppHasVisibleWindow(canon))
                {
                    Log($"ALLOWED (active window open) {canon} pid={pid}");
                    Track(p, canon, set);
                    return;
                }

                // Case 2: App tar kono window nei, kintu eta app tar-i nijer internal worker process
                // (jemon Discord tray-te run korche ebong nijer internal worker/updater spawn korlo)
                if (HasTrackedAncestor(set, pid))
                {
                    Log($"ALLOWED (internal child of unlocked app) {canon} pid={pid}");
                    Track(p, canon, set);
                    return;
                }

                // Case 3: App tar kono window nei, ebong user baire theke (shortcut/explorer) app ta abar open korlo!
                // (jemon Chrome close kore user desktop shortcut click kore abar open korlo)
                Log($"REOPENING CLOSED APP {canon}: No active window existed. Re-prompting password.");
                _unlocked.Remove(canon);
            }

            Log($"LOCKED start {canon} (raw={rawPath}) pid={pid}");

            bool suspendFailed = false;
            if (!Suspend(p))
            {
                Log($"SUSPEND FAILED, Plan B (kill + ask password): {canon} pid={pid}");
                try { p.Kill(true); } catch { }
                suspendFailed = true;
            }

            if (!_waiting.TryGetValue(canon, out var list))
            {
                _waiting[canon] = list = new List<Process>();
                ask = true; // First process of this launch: prompt user
            }

            if (!suspendFailed)
            {
                list.Add(p);
            }
        }

        if (!ask) return;

        bool ok = false;
        try { ok = await _askUnlock(canon); } catch { }

        List<Process> procs;
        lock (_lock)
        {
            procs = _waiting.TryGetValue(canon, out var l) ? l : new List<Process>();
            _waiting.Remove(canon);
            if (ok)
            {
                _unlocked[canon] = new HashSet<int>();
            }
        }

        if (ok)
        {
            if (procs.Count > 0)
            {
                foreach (var proc in procs)
                {
                    lock (_lock) { Track(proc, canon, _unlocked[canon]); }
                    Resume(proc);
                }
            }
            else
            {
                // Plan B relaunch
                try
                {
                    Log($"Plan B: Relaunching {canon} after unlock");
                    var np = Process.Start(new ProcessStartInfo(canon) { UseShellExecute = true });
                    if (np != null)
                    {
                        lock (_lock) { Track(np, canon, _unlocked[canon]); }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Plan B relaunch error: {ex.Message}");
                }
            }
        }
        else
        {
            foreach (var proc in procs)
            {
                try { proc.Kill(true); } catch { }
                finally { proc.Dispose(); }
            }
        }
    }

    private bool Known(int pid)
    {
        lock (_lock)
        {
            foreach (var set in _unlocked.Values)
                if (set.Contains(pid)) return true;
            foreach (var list in _waiting.Values)
                foreach (var w in list)
                    if (w.Id == pid) return true;
        }
        return false;
    }

    // Background process supervisor
    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            List<string>? toRelock = null;

            lock (_lock)
            {
                foreach (var kvp in _unlocked.ToList())
                {
                    string path = kvp.Key;
                    bool running = PathRunning(path);
                    bool hasWindow = running && AppHasVisibleWindow(path);

                    if (hasWindow)
                    {
                        _hadWindow.Add(path);
                    }
                    else if (!running || _hadWindow.Contains(path))
                    {
                        // App bondho hoyeche! (Process exit hoyeche BA app-er shob visible window close hoyeche)
                        _unlocked.Remove(path);
                        _hadWindow.Remove(path);
                        toRelock ??= new List<string>();
                        toRelock.Add(path);
                        KillAllProcesses(path);
                        Log($"SWEEP: App closed/windows exited, relocked & cleaned: {path}");
                    }
                }
            }

            if (toRelock != null)
            {
                foreach (var path in toRelock)
                {
                    Relocked?.Invoke(path);
                }
            }

            // 2. Scan running processes:
            // Shudhu matro jodi kono app er ACTIVE VISIBLE WINDOW screen-e thake kintu app unlock kora nei:
            // (Windowless / Tray background process-e kokhono spontaneous popup asbena!)
            var locked = AppRegistry.List();
            if (locked.Count == 0) return;

            var wanted = new HashSet<string>(
                locked.Select(x => Path.GetFileNameWithoutExtension(x)),
                StringComparer.OrdinalIgnoreCase);

            Process[]? allProcs = null;
            try { allProcs = Process.GetProcesses(); }
            catch { }

            if (allProcs != null)
            {
                foreach (var p in allProcs)
                {
                    try
                    {
                        if (!wanted.Contains(p.ProcessName)) continue;
                        if (Known(p.Id)) continue;
                        string? rawPath = ImagePath(p.Id);
                        if (rawPath == null) continue;
                        string? canon = AppRegistry.FindLockedMatch(rawPath);
                        if (canon == null) continue;

                        lock (_lock)
                        {
                            // App jodi agei unlock thake: simply track, NO POPUP!
                            if (_unlocked.TryGetValue(canon, out var set))
                            {
                                Track(p, canon, set);
                                continue;
                            }
                        }

                        // Windowless background / tray process-e kokhono spontaneous popup dekhabe na!
                        if (!AppHasVisibleWindow(canon)) continue;

                        Log($"SWEEP: active locked window detected for {canon} pid={p.Id}");
                        _ = HandleStart(p.Id, p.ProcessName);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _sweeping, 0); }
    }

    private void Track(Process p, string path, HashSet<int> set)
    {
        int pid = p.Id;
        set.Add(pid);
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => OnExit(path, pid);
        }
        catch
        {
            set.Remove(pid);
        }
    }

    private void OnExit(string path, int pid)
    {
        bool relock = false;

        lock (_lock)
        {
            if (_unlocked.TryGetValue(path, out var set))
            {
                set.Remove(pid);
                // Shob process bondho hole relock
                if (!PathRunning(path))
                {
                    _unlocked.Remove(path);
                    relock = true;
                }
            }
        }

        if (relock)
        {
            Log($"ON_EXIT relocked: {path}");
            Relocked?.Invoke(path);
        }
    }

    private static bool Suspend(Process p)
    {
        try { return NtSuspendProcess(p.Handle) == 0; } catch { return false; }
    }

    private static void Resume(Process p)
    {
        try { NtResumeProcess(p.Handle); } catch { }
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public void Dispose()
    {
        try { _sweepTimer?.Dispose(); } catch { }
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { }
        lock (_lock)
        {
            foreach (var list in _waiting.Values)
                foreach (var p in list)
                    try { p.Kill(true); } catch { }
            _waiting.Clear();
        }
    }
}
