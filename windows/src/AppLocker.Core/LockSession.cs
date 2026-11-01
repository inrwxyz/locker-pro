namespace AppLocker.Core;

// Kon kon folder ekhon Open (unlocked) ache, seta mone rakhe.
// "Lock now" = SealAll() chaple shob Open folder abar lock.
public class LockSession
{
    private readonly byte[] _masterKey;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Opened;
    public event Action<string>? Sealed;
    public event Action<string, List<string>>? SealFailed; // folder, je file gulo encrypt hoyni

    public LockSession(byte[] masterKey) => _masterKey = masterKey;

    public IReadOnlyCollection<string> OpenFolders => _open.ToList();

    // Password/finger sothik hole Guard eta call korbe
    public bool OpenFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        if (!FolderLocker.IsLocked(full)) return false;
        if (!FolderLocker.Open(full, _masterKey)) return false;
        _open.Add(full);
        Opened?.Invoke(full);
        return true;
    }

    // Ekta folder abar lock (Island er "Lock now")
    public bool SealFolder(string folder)
    {
        string full = Path.GetFullPath(folder);
        var failed = FolderLocker.Seal(full, _masterKey);
        if (failed == null) return false;

        if (failed.Count == 0)
        {
            _open.Remove(full);
            Sealed?.Invoke(full);
        }
        else
        {
            SealFailed?.Invoke(full, failed);
        }
        return failed.Count == 0;
    }

    // Shob Open folder ekshathe lock
    public void SealAll()
    {
        foreach (var f in _open.ToList()) SealFolder(f);
    }
}