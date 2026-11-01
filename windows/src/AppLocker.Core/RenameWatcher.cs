namespace AppLocker.Core;

// Locked folder gulo rename hole purono naam e ferot ane.
// Ekhon test version: ekta folder er parent watch kore.
public class RenameWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _lockedPath;

    public event Action<string>? Log;

    public RenameWatcher(string lockedFolder)
    {
        _lockedPath = Path.GetFullPath(lockedFolder).TrimEnd('\\');
        string parent = Path.GetDirectoryName(_lockedPath)!;

        _watcher = new FileSystemWatcher(parent)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Renamed += OnRenamed;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Je folder lock kora, seta rename hoyeche?
        if (!e.OldFullPath.TrimEnd('\\').Equals(_lockedPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Log?.Invoke($"Rename dhora poreche: {e.OldName} -> {e.Name}. Ferot nichhi...");
            Directory.Move(e.FullPath, _lockedPath);
            Log?.Invoke("Purono naam e ferot ana hoyeche.");
        }
        catch (Exception ex)
        {
            Log?.Invoke("Ferot ana jayni: " + ex.Message);
        }
    }

    public void Dispose() => _watcher.Dispose();
}