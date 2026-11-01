using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using AppLocker.Core;

namespace AppLocker.UI;

// Locked app shuru hole thamiye (suspend) password chay.
// Thik hole chalu kore dey, bhul/cancel hole kill kore.
// App er shob window bondho hole abar lock (Relocked event).
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
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private readonly Func<string, Task<bool>> _askUnlock;
    private ManagementEventWatcher? _watcher;
    private readonly HashSet<string> _mismatch = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _sweepTimer;
    private int _sweeping;
    private readonly object _lock = new();

    // exe path -> unlock howar por je process gulo chalu ache
    private readonly Dictionary<string, HashSet<int>> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    // exe path -> password er opekkhay thaka (suspended) process gulo
    private readonly Dictionary<string, List<Process>> _waiting = new(StringComparer.OrdinalIgnoreCase);

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
            // WMI event miss hole ba app age thekei cholle: protita 2.5s e running process dhore
            _sweepTimer = new System.Threading.Timer(_ => Sweep(), null, 3000, 2500);
            return true;
        }
        catch (UnauthorizedAccessException) { error = "Admin lagbe"; }
        catch (ManagementException ex) { error = ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "Admin lagbe" : ex.Message; }
        catch (Exception ex) { error = ex.Message; }
        return false;
    }

    // Force relock and kill any running processes for this app
    public void Relock(string path)
    {
        string norm = Path.GetFullPath(path.Trim().Trim('"'));
        lock (_lock)
        {
            _unlocked.Remove(norm);
            if (_waiting.Remove(norm, out var waiting))
            {
                foreach (var p in waiting)
                {
                    try { p.Kill(true); } catch { }
                    finally { p.Dispose(); }
                }
            }
        }

        string name = Path.GetFileNameWithoutExtension(norm);
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? pPath = ImagePath(p.Id);
                    if (pPath != null && pPath.Equals(norm, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(true);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        Log($"FORCE RELOCKED & TERMINATED: {norm}");
        Relocked?.Invoke(norm);
    }

    // Check if an app currently has a visible GUI window
    public static bool AppHasVisibleWindow(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle))
                    {
                        string? path = ImagePath(p.Id);
                        if (path != null && path.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
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
        string? path = ImagePath(pid);
        if (path == null || !AppRegistry.IsLocked(path))
        {
            try
            {
                var same = pname.Length == 0
                    ? new List<string>()
                    : AppRegistry.List().Where(x =>
                        Path.GetFileName(x).Equals(pname, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count > 0)
                {
                    bool first;
                    lock (_mismatch) first = _mismatch.Add(pname + "|" + (path ?? "-"));
                    if (first)
                        Log($"MISMATCH name={pname} actual={path ?? "(process agei shesh, path pawa jayni)"} registry=[{string.Join(" ; ", same)}]");
                }
            }
            catch { }
            return;
        }
        Log($"LOCKED start {path} pid={pid}");

        Process p;
        try { p = Process.GetProcessById(pid); }
        catch { return; }

        bool ask = false;
        lock (_lock)
        {
            if (Known(pid)) return; // sweep + WMI dono ek-i process dhorle

            // App age thekei unlocked: verify that the app actually has an active window!
            if (_unlocked.TryGetValue(path, out var set))
            {
                if (AppHasVisibleWindow(path))
                {
                    if (set.Contains(pid)) return;
                    Log($"ALLOWED (child of active app) {path} pid={pid}");
                    Track(p, path, set);
                    return;
                }
                else
                {
                    // No visible window existed: app was closed earlier, background task lingered!
                    Log($"RELOCKING {path}: background lingering pid without active window. Prompting password.");
                    _unlocked.Remove(path);
                }
            }

            // Thamai. Thamano na gele Plan B: kill kore dei, kintu password chay, unlock hole relaunch
            bool suspendFailed = false;
            if (!Suspend(p))
            {
                Log($"SUSPEND FAILED, Plan B (kill + ask password): {path} pid={pid}");
                try { p.Kill(true); } catch { }
                suspendFailed = true;
            }

            if (!_waiting.TryGetValue(path, out var list))
            {
                _waiting[path] = list = new List<Process>();
                ask = true; // ei path er prothom process: popup ei dekhabe
            }
            if (!suspendFailed)
            {
                list.Add(p);
            }
        }

        if (!ask) return;

        bool ok = false;
        try { ok = await _askUnlock(path); } catch { }

        List<Process> procs;
        lock (_lock)
        {
            procs = _waiting.TryGetValue(path, out var l) ? l : new List<Process>();
            _waiting.Remove(path);
            if (ok) _unlocked[path] = new HashSet<int>();
        }

        if (ok)
        {
            if (procs.Count > 0)
            {
                foreach (var proc in procs)
                {
                    lock (_lock) { Track(proc, path, _unlocked[path]); }
                    Resume(proc);
                }
            }
            else
            {
                // Plan B: Suspend fail howay process agei kill hoyechilo. Ekhon unlock hoyeche, tai relaunch
                try
                {
                    Log($"Plan B: Relaunching {path} after unlock");
                    var np = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    if (np != null)
                    {
                        lock (_lock) { Track(np, path, _unlocked[path]); }
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

    // Locked app already cholche (background, tray, ba WMI miss): thamiye password chay
    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            // 1. Relock any apps in _unlocked whose windows have all closed
            List<string>? toRelock = null;
            lock (_lock)
            {
                foreach (var kvp in _unlocked.ToList())
                {
                    string path = kvp.Key;
                    // If no visible window exists for this app, relock it!
                    if (!AppHasVisibleWindow(path))
                    {
                        _unlocked.Remove(path);
                        toRelock ??= new List<string>();
                        toRelock.Add(path);
                        Log($"SWEEP: All windows closed, relocked: {path}");
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

            // 2. Detect any newly started or running locked apps
            var locked = AppRegistry.List();
            if (locked.Count == 0) return;

            var names = locked.Select(x => Path.GetFileNameWithoutExtension(x))
                              .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(n); }
                catch { continue; }

                foreach (var p in ps)
                {
                    try
                    {
                        if (Known(p.Id)) continue;
                        string? path = ImagePath(p.Id);
                        if (path == null || !AppRegistry.IsLocked(path)) continue;
                        Log($"SWEEP: already running {path} pid={p.Id}");
                        _ = HandleStart(p.Id, n);
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
                // If all tracked pids exited OR no visible window remains, relock
                if (set.Count == 0 || !AppHasVisibleWindow(path))
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
