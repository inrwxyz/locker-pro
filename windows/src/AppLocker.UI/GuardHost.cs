using System.IO;
using System.Windows;
using AppLocker.Core;

namespace AppLocker.UI;

// Guard er mathar kaj: popup (status shoho) + LockSession jore dey.
public class GuardHost
{
    private LockSession? _session;
    private MainWindow? _chip; // folder open thakle choto chip (Lock now)
    private ExplorerWatcher? _explorer;
    private AppGuard? _apps;
    private bool _busy;
    private readonly Dictionary<string, RenameWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    private bool HasOpen => _session != null && _session.OpenFolders.Count > 0;

    public void Start()
    {
        foreach (var f in LockRegistry.List()) EnsureWatcher(f);

        // Explorer e locked folder e dhukle auto popup
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

        // Locked app shuru hole popup
        _apps = new AppGuard(AskApp);
        _apps.Relocked += OnAppRelocked;
        if (!_apps.Start(out string err))
            Application.Current.Dispatcher.InvokeAsync(() => ShowNotice("\uE783", "App lock off", err));
    }

    // Guard bondho hole je folder gulo open ache shob lock kore dey
    public void Stop()
    {
        _explorer?.Stop();
        _apps?.Dispose();
        try { _session?.SealAll(); } catch { }
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }

    // path ta kono locked folder er bhitore hole oi folder return kore.
    // Folder ekhon open (unlocked) thakle null (tokhon dhukte dibe).
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

    // Pipe/Explorer theke ashe (UI thread e)
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
            case "watch": EnsureWatcher(arg); return "ok";
            case "quit":
                if (!MasterConfig.IsSetup())
                {
                    Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                    return "bye";
                }
                // Password chhara bondho hobe na (arg = password)
                if (LockoutTracker.SecondsRemaining() > 0) return "denied";
                byte[]? qk = MasterConfig.GetKeyWithPassword(arg);
                if (qk == null) { LockoutTracker.RecordFail(); return "denied"; }
                Array.Clear(qk);
                LockoutTracker.RecordSuccess();
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

    // Shob check kore turontoi reply dey, asol kaj (popup) background e cholbe
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

    // ---------- Right-click theke lock (popup e password) ----------

    private string RequestLock(string raw, bool isFolder)
    {
        if (!MasterConfig.IsSetup())
        {
            ShowNotice("\uE783", "Setup lagbe", "Age Shell diye ekbar password set koro");
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
        MainWindow? win = null;
        try
        {
            win = new MainWindow(path) { Topmost = true };
            win.SetPrompt("Lock", "Lock korte password dao");
            byte[]? key = await win.AskAsync();
            if (key == null) return; // cancel

            win.ShowBusy("Locking...");
            await Task.Delay(300);

            if (isFolder)
            {
                await Task.Run(() => FolderLocker.Lock(path, key));
                EnsureWatcher(path);
            }
            else
            {
                AppRegistry.Add(path);
            }
            Array.Clear(key);
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
        MainWindow? win = null;
        try
        {
            EnsureWatcher(folder);

            win = new MainWindow(folder) { Topmost = true };
            byte[]? key = await win.AskAsync();
            if (key == null) return; // cancel: popup nijei fade out hoy

            win.ShowBusy("Verifying...");
            await Task.Delay(600);

            _session ??= new LockSession(key);
            bool ok = await Task.Run(() => _session.OpenFolder(folder));
            if (!ok)
            {
                await win.ShowResult("\uE783", "Open hoyni", name);
                return;
            }

            if (LockerSettings.Current.InexIslandAnimation)
            {
                InexIslandBridge.NotifyUnlock("password", name);
            }

            // Je tab e dhukechilo oi tab e ferot pathao, na pele notun window
            _explorer?.Restore(folder, () => OpenInExplorer(folder));
            restored = true;

            if (_chip == null)
            {
                // popup choto chip e poriborton hoy, Lock now button shoho
                _chip = win;
                win.LockNowClicked += OnLockNow;
                win.ShowChip(Label());
            }
            else
            {
                // chip already ache: ei popup e tick dekhiye bondho
                await win.ShowResult("\uE73E", "Unlocked", name, 900);
                RefreshChip();
            }
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
            if (!restored) _explorer?.Forget(folder);
            _busy = false;
        }
    }

    // ---------- App lock ----------

    // AppGuard (alada thread) theke dake, tai UI thread e pathiye dei
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
        MainWindow? win = null;
        try
        {
            win = new MainWindow(exePath) { Topmost = true };
            byte[]? key = await win.AskAsync();
            if (key == null) return false; // cancel

            Array.Clear(key);
            win.ShowBusy("Verifying...");
            await Task.Delay(500);
            if (LockerSettings.Current.InexIslandAnimation)
            {
                InexIslandBridge.NotifyUnlock("password", name);
            }
            await win.ShowResult("\uE73E", "Unlocked", name, 1000);
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

    // App er shob process bondho: ektu dekhai
    private void OnAppRelocked(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            Application.Current.Dispatcher.InvokeAsync(() => ShowNotice("\uE72E", name, "Locked again"));
        }
        catch { }
    }

    // Choto notice: popup ashe, ektu thake, fade hoy. Byasto thakle dekhabe na.
    private async void ShowNotice(string glyph, string title, string sub)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var w = new MainWindow("") { Topmost = true };
            w.ShowNoticeMode(glyph, title, sub);
            w.Show();
            await Task.Delay(1400);
            await w.CloseAsync();
        }
        catch { }
        finally { _busy = false; }
    }

    // ---------- Folder helper ----------

    // Fallback: folder ta notun Explorer window te khule dey
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
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_chip == null || _session == null || _session.OpenFolders.Count == 0) return;
                bool isAnyActive = false;
                foreach (var folder in _session.OpenFolders)
                {
                    string norm = folder.TrimEnd('\\');
                    if (activePaths.Any(p => p.Equals(norm, StringComparison.OrdinalIgnoreCase) ||
                                             p.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        isAnyActive = true;
                        break;
                    }
                }
                _chip.SetChipVisible(isAnyActive);
            });
        }
        catch { }
    }

    private async void OnLockNow()
    {
        if (_busy || _chip == null || _session == null) return;
        _busy = true;
        var chip = _chip;
        string label = Label();
        try
        {
            chip.ShowBusy("Locking...");

            // Explorer ke parent folder e shoriye dei jate lock er shomoy file handle stuck na hoy
            if (_explorer != null && _session != null)
            {
                foreach (var f in _session.OpenFolders)
                {
                    _explorer.NavigateAway(f);
                }
            }
            await Task.Delay(350);

            var session = _session; if (session != null) await Task.Run(() => session.SealAll());

            if (!HasOpen)
            {
                _chip = null;
                _session = null;
                chip.LockNowClicked -= OnLockNow;
                await chip.ShowResult("\uE72E", "Locked", label);
            }
            else
            {
                // kono file onno program e khola, abar try korte dey
                chip.SetStatus("\uE783", "Lock hoyni", "File khola ache");
                await Task.Delay(1800);
                chip.ShowChip(Label());
            }
        }
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

    // Open folder thakle chip e "Lock now" dekhay
    private void RefreshChip()
    {
        if (_chip == null || !HasOpen) return;
        _chip.ShowChip(Label());
    }
}
