using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AppLocker.Core;

namespace AppLocker.Manager.Guard;

public class GuardHost
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr dwItem2);
    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;

    private LockSession? _session;
    private ExplorerWatcher? _explorer;
    private AppGuard? _apps;
    private LockChipWindow? _chip;
    private bool _busy;
    private readonly Dictionary<string, RenameWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _folderUnlockTimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _missingCounts =
        new(StringComparer.OrdinalIgnoreCase);

    private bool HasOpen => _session != null && _session.OpenFolders.Count > 0;

    public void Start()
    {
        foreach (var f in LockRegistry.List()) EnsureWatcher(f);

        _explorer = new ExplorerWatcher(
            p =>
            {
                try { return Application.Current.Dispatcher.Invoke(() => LockedFolderFor(p)); }
                catch { return null; }
            },
            f =>
            {
                try { Application.Current.Dispatcher.InvokeAsync(() => { Handle("open|" + f); }); }
                catch { }
            });
        _explorer.ActiveFoldersChanged += OnActiveFoldersChanged;
        _explorer.Start();

        _apps = new AppGuard(AskApp);
        _apps.Relocked += OnAppRelocked;
        if (!_apps.Start(out string err))
            Application.Current.Dispatcher.InvokeAsync(() => ShowNotice("\uE783", "App lock off", err));
    }

    public void Stop()
    {
        _explorer?.Stop();
        _apps?.Dispose();
        try { _chip?.Close(); } catch { }
        _chip = null;
        try { _session?.SealAll(); } catch { }
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        _folderUnlockTimes.Clear();
        _missingCounts.Clear();
    }

    public string? LockedFolderFor(string path)
    {
        foreach (var raw in LockRegistry.List())
        {
            string folder = raw.TrimEnd('\\');
            bool inside = path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                          path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
            if (!inside) continue;
            if (!FolderLocker.IsLocked(folder)) return null;
            if (_session != null &&
                _session.OpenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                return null;
            return folder;
        }
        return null;
    }

    public string Handle(string line)
    {
        int i = line.IndexOf('|');
        string cmd = i < 0 ? line : line[..i];
        string arg = i < 0 ? "" : line[(i + 1)..];

        switch (cmd)
        {
            case "open": return Request(arg);
            case "lock": return RequestLock(arg, true);
            case "lockapp": return RequestLock(arg, false);
            case "relock":
                _apps?.Relock(arg);
                return "ok";
            case "watch": EnsureWatcher(arg); return "ok";
            case "quit":
                if (!MasterConfig.IsSetup())
                {
                    AppLocker.Manager.WatchdogLauncher.SignalStop();
                    Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                    return "bye";
                }
                if (LockoutTracker.SecondsRemaining("pipe") > 0) return "denied";
                byte[]? qk = MasterConfig.GetKeyWithPassword(arg);
                if (qk == null) { LockoutTracker.RecordFail("pipe"); return "denied"; }
                Array.Clear(qk);
                LockoutTracker.RecordSuccess("pipe");
                AppLocker.Manager.WatchdogLauncher.SignalStop();
                Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                return "bye";
            default: return "unknown";
        }
    }

    private void EnsureWatcher(string raw)
    {
        try
        {
            string folder = Path.GetFullPath(raw.Trim().Trim('"')).TrimEnd('\\');
            if (_watchers.ContainsKey(folder)) return;
            if (!Directory.Exists(folder) || !FolderLocker.IsLocked(folder)) return;
            _watchers[folder] = new RenameWatcher(folder);
        }
        catch { }
    }

    private string Request(string raw)
    {
        if (!MasterConfig.IsSetup()) return "not-setup";

        string folder;
        try { folder = Path.GetFullPath(raw.Trim().Trim('"')).TrimEnd('\\'); }
        catch { return "not-locked"; }

        if (!Directory.Exists(folder) || !FolderLocker.IsLocked(folder)) return "not-locked";
        if (_busy) return "busy";
        if (_session != null &&
            _session.OpenFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return "already-open";

        _busy = true;
        _ = RunOpenFlow(folder);
        return "accepted";
    }

    private string RequestLock(string raw, bool isFolder)
    {
        if (!MasterConfig.IsSetup())
        {
            ShowNotice("\uE783", "Setup lagbe", "Age Locker khule ekbar password set koro");
            return "not-setup";
        }

        string path;
        try { path = Path.GetFullPath(raw.Trim().Trim('"')).TrimEnd('\\'); }
        catch { return "bad-path"; }

        if (!isFolder && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            path = AppResolver.ResolveShortcut(path);

        string? why = ValidateLock(path, isFolder);
        if (why != null)
        {
            ShowNotice("\uE783", "Lock hobe na", why);
            return "denied";
        }

        if (!isFolder)
        {
            // Adding an app is instant: no password needed to lock an app!
            try
            {
                AppRegistry.Add(path);
                _apps?.Relock(path);
                string appName = Path.GetFileNameWithoutExtension(path);
                InexIslandBridge.NotifyLock(appName);
                ShowNotice("\uE72E", "App Locked", appName);
                return "ok";
            }
            catch (Exception ex)
            {
                ShowNotice("\uE783", "Lock fail", ex.Message);
                return "error";
            }
        }

        if (_busy) return "busy";
        _busy = true;
        _ = RunLockFlow(path, isFolder);
        return "ok";
    }

    private static string? ValidateLock(string path, bool isFolder)
    {
        if (isFolder)
        {
            if (!Directory.Exists(path)) return "Folder pawa jayni";
            if (FolderLocker.IsLocked(path)) return "Already locked";
            if (!LockSafety.IsSafeFolder(path, out string why)) return why;
            return null;
        }
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return "Eta .exe na";
        if (AppRegistry.IsBlocked(path)) return "System/Locker program lock hobe na";
        if (AppRegistry.IsLocked(path)) return "Already locked";
        return null;
    }

    private async Task RunLockFlow(string path, bool isFolder)
    {
        string name = Path.GetFileName(path);
        GuardPopup? win = null;
        try
        {
            win = new GuardPopup(path, isLock: true) { Topmost = true };
            win.SetPrompt("Lock", "Lock korte password dao", isLock: true);
            byte[]? key = await win.AskAsync();
            if (key == null) return;

            win.ShowBusy("Locking...");
            await Task.Delay(250);

            if (isFolder)
            {
                await Task.Run(() => FolderLocker.Lock(path, key));
                EnsureWatcher(path);
            }
            Array.Clear(key);
            InexIslandBridge.NotifyLock(name, win.AuthType);
            await win.ShowResult("\uE72E", "Locked", name);
        }
        catch (Exception ex)
        {
            if (win != null)
            {
                try { await win.ShowResult("\uE783", "Error", ex.Message); } catch { }
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunOpenFlow(string folder)
    {
        string name = Path.GetFileName(folder);
        bool restored = false;
        GuardPopup? win = null;
        try
        {
            EnsureWatcher(folder);

            win = new GuardPopup(folder) { Topmost = true };
            byte[]? key = await win.AskAsync();
            if (key == null)
            {
                _explorer?.Cancel(folder);
                return;
            }

            win.ShowBusy("Opening...");

            _session = new LockSession(key);
            bool ok = await Task.Run(() => _session.OpenFolder(folder));
            if (!ok)
            {
                await win.ShowResult("\uE783", "Open hoyni", name);
                _explorer?.Cancel(folder);
                return;
            }

            // Flush Explorer shell cache so decrypted files appear instantly without showing .aplk
            try
            {
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, folder, IntPtr.Zero);
            }
            catch { }

            await Task.Delay(150);

            // Navigate the SAME Explorer tab directly into the decrypted folder
            _explorer?.Restore(folder, () => OpenInExplorer(folder));
            restored = true;

            // Set grace period timestamp and reset missing count
            _folderUnlockTimes[folder] = DateTime.UtcNow;
            _missingCounts[folder] = 0;

            InexIslandBridge.NotifyUnlock(win.AuthType, name);

            await win.ShowResult("\uE73E", "Unlocked", name, 650);

            // Display or update the single corner LockChipWindow
            if (_chip == null || !_chip.IsLoaded)
            {
                _chip = new LockChipWindow();
                _chip.SetFolder(Label());
                _chip.LockClicked += OnLockNow;
                _chip.Closed += (_, _) => { _chip = null; };
                _chip.Show();
            }
            else
            {
                _chip.SetFolder(Label());
            }
        }
        catch (Exception ex)
        {
            if (win != null)
            {
                try { await win.ShowResult("\uE783", "Error", ex.Message); } catch { }
            }
            _explorer?.Cancel(folder);
        }
        finally
        {
            if (!restored)
            {
                _explorer?.Cancel(folder);
            }
            _busy = false;
        }
    }

    private async void OnLockNow()
    {
        if (_busy || _session == null) return;
        _busy = true;
        var chip = _chip;
        chip?.ShowBusy("Locking...");
        try
        {
            var openFolders = _session?.OpenFolders.ToList() ?? new List<string>();
            if (_explorer != null && _session != null)
            {
                foreach (var f in openFolders)
                {
                    _explorer.NavigateAway(f);
                }
            }
            await Task.Delay(250);

            if (_session != null)
            {
                await Task.Run(() => _session.SealAll());
            }

            _session = null;
            _folderUnlockTimes.Clear();
            _missingCounts.Clear();

            // tray "Lock Now": tell the island once (it used to ignore this event, and N folders would
            // otherwise flash N toasts back to back)
            if (openFolders.Count == 1)
                InexIslandBridge.NotifyLock(Path.GetFileName(openFolders[0].TrimEnd('\\')));
            else if (openFolders.Count > 1)
                InexIslandBridge.NotifyLock($"{openFolders.Count} folders");

            chip?.ShowLockedAndClose();
            _chip = null;
        }
        catch { }
        finally
        {
            _busy = false;
        }
    }

    private string Label()
    {
        var open = _session?.OpenFolders;
        if (open == null || open.Count == 0) return "Locker";
        return open.Count == 1 ? Path.GetFileName(open.First().TrimEnd('\\')) : $"{open.Count} folders";
    }

    private async Task<bool> AskApp(string exePath)
    {
        var op = Application.Current.Dispatcher.InvokeAsync(() => AskAppUi(exePath));
        return await await op.Task;
    }

    private async Task<bool> AskAppUi(string exePath)
    {
        while (_busy) await Task.Delay(200);
        _busy = true;

        string name = Path.GetFileNameWithoutExtension(exePath);
        GuardPopup? win = null;
        try
        {
            win = new GuardPopup(exePath) { Topmost = true };
            byte[]? key = await win.AskAsync();
            if (key == null) return false;

            Array.Clear(key);
            win.ShowBusy("Verifying...");
            await Task.Delay(300);
            InexIslandBridge.NotifyUnlock(win.AuthType, name);
            await win.ShowResult("\uE73E", "Unlocked", name, 700);
            return true;
        }
        catch
        {
            if (win != null)
            {
                try { await win.ShowResult("\uE783", "Error", name); } catch { }
            }
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnAppRelocked(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        InexIslandBridge.NotifyLock(name);
        try
        {
            Application.Current.Dispatcher.InvokeAsync(() => ShowNotice("\uE72E", name, "Locked again"));
        }
        catch { }
    }

    private async void ShowNotice(string glyph, string title, string sub)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var w = new GuardPopup("") { Topmost = true };
            w.ShowNoticeMode(glyph, title, sub);
            w.Show();
            await Task.Delay(1400);
            await w.CloseAsync();
        }
        catch { }
        finally { _busy = false; }
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
                { UseShellExecute = true });
        }
        catch { }
    }

    private void OnActiveFoldersChanged(HashSet<string> activePaths)
    {
        try
        {
            if (_session == null || _session.OpenFolders.Count == 0) return;

            var toRelock = new List<string>();
            DateTime now = DateTime.UtcNow;

            foreach (var folder in _session.OpenFolders.ToList())
            {
                string norm = folder.TrimEnd('\\');
                bool stillOpen = activePaths.Any(p =>
                    p.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase));

                if (stillOpen)
                {
                    _missingCounts[folder] = 0;
                }
                else
                {
                    // Grace period check: don't relock if folder was opened within the last 6 seconds
                    if (_folderUnlockTimes.TryGetValue(folder, out var openedAt) &&
                        (now - openedAt) < TimeSpan.FromSeconds(6))
                    {
                        continue;
                    }

                    int count = _missingCounts.TryGetValue(folder, out var c) ? c + 1 : 1;
                    _missingCounts[folder] = count;

                    // Only relock after missing for 4 consecutive polling ticks (~2-3s)
                    if (count >= 4)
                    {
                        toRelock.Add(folder);
                    }
                }
            }

            if (toRelock.Count > 0)
            {
                Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    foreach (var f in toRelock)
                    {
                        _missingCounts.Remove(f);
                        _folderUnlockTimes.Remove(f);
                        try { _session?.SealFolder(f); } catch { }
                        InexIslandBridge.NotifyLock(Path.GetFileName(f.TrimEnd('\\')));
                    }
                    if (!HasOpen)
                    {
                        _session = null;
                        if (_chip != null)
                        {
                            await _chip.FadeOutAndClose();
                            _chip = null;
                        }
                    }
                    else
                    {
                        _chip?.SetFolder(Label());
                    }
                });
            }
        }
        catch { }
    }
}
