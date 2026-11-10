using System.Security.Cryptography;
using System.Text.Json;

namespace AppLocker.Core;

public record FolderProgress(int FilesDone, int TotalFiles, long BytesDone, long TotalBytes, string CurrentFile)
{
    public double Percent =>
        TotalBytes > 0
            ? Math.Clamp((BytesDone * 100.0) / TotalBytes, 0, 100)
            : TotalFiles > 0 ? Math.Clamp((FilesDone * 100.0) / TotalFiles, 0, 100) : 100;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB"
    };
}

public class LockMeta
{
    public string WrappedDataKey { get; set; } = "";
    public bool IsOpen { get; set; }   // true = file gulo ekhon normal (temporary unlock)
}

// Kon kon folder AppLocker er under e, tar list (Manager eta dekhabe)
public static class LockRegistry
{
    private static string Path_ => Path.Combine(MasterConfig.ConfigDir, "items.json");
    public static string FilePath => Path_;

    public static List<string> List()
    {
        if (!File.Exists(Path_)) return new();
        return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path_)) ?? new();
    }

    public static void Add(string folder)
    {
        var l = List();
        string full = Path.GetFullPath(folder);
        if (!l.Contains(full, StringComparer.OrdinalIgnoreCase)) l.Add(full);
        Save(l);
    }

    public static void Remove(string folder)
    {
        var l = List();
        string full = Path.GetFullPath(folder);
        l.RemoveAll(x => x.Equals(full, StringComparison.OrdinalIgnoreCase));
        Save(l);
    }

    private static void Save(List<string> l)
    {
        Directory.CreateDirectory(MasterConfig.ConfigDir);
        File.WriteAllText(Path_, JsonSerializer.Serialize(l));
    }
}

public static class FolderLocker
{
    public const string MetaName = ".aplk-meta";
    public const string Ext = ".aplk";
    private const string WorkTmpSuffix = ".aplk-work.tmp";

    private static readonly EnumerationOptions AllFilesOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        MetaName, "Thumbs.db", "ehthumbs.db", "ehthumbs_vista.db"
    };

    // Folder AppLocker er under e ache kina (locked ba temporarily open)
    public static bool IsLocked(string folder) =>
        File.Exists(Path.Combine(folder, MetaName)) || TryRestoreMeta(folder);

    // .aplk-meta er backup (ProgramData, shudhu admin/SYSTEM): keu meta file muche dileo wrapped key haray na.
    private static string BackupPath(string folder)
    {
        string full = Path.GetFullPath(folder).TrimEnd('\\').ToLowerInvariant();
        string h = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..24];
        return Path.Combine(MasterConfig.ConfigDir, "meta-backup", h + ".json");
    }

    private static bool TryRestoreMeta(string folder)
    {
        try
        {
            string bp = BackupPath(folder);
            if (!File.Exists(bp) || !Directory.Exists(folder)) return false;
            if (!Directory.EnumerateFiles(folder, "*" + Ext, AllFilesOptions).Any()) return false;

            var meta = JsonSerializer.Deserialize<LockMeta>(File.ReadAllText(bp));
            if (meta == null || string.IsNullOrEmpty(meta.WrappedDataKey)) return false;
            meta.IsOpen = false;
            WriteMeta(folder, meta);
            return true;
        }
        catch { return false; }
    }

    public static bool IsOpen(string folder) =>
        IsLocked(folder) && ReadMeta(folder).IsOpen;

    // Prothom bar lock: encrypt + ACL deny
    public static void Lock(string folder, byte[] masterKey, IProgress<FolderProgress>? progress = null)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (IsLocked(folder)) throw new InvalidOperationException("Already locked.");

        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        var meta = new LockMeta
        {
            WrappedDataKey = Convert.ToBase64String(KeyWrap.Wrap(dataKey, masterKey)),
            IsOpen = true
        };
        WriteMeta(folder, meta);
        LockRegistry.Add(folder);

        try
        {
            SealInternal(folder, dataKey, progress);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // Temporary unlock: ACL shoriye file gulo decrypt kore normal kore dey.
    public static bool Open(string folder, byte[] masterKey, IProgress<FolderProgress>? progress = null)
    {
        byte[]? dataKey = GetDataKey(folder, masterKey);
        if (dataKey == null) return false;

        try
        {
            AclGuard.Unprotect(folder);
            var failed = DecryptAll(folder, dataKey, progress);
            if (failed.Count > 0)
            {
                try { AclGuard.Protect(folder); } catch { }
                throw BuildDecryptFailure(failed);
            }

            var meta = ReadMeta(folder);
            meta.IsOpen = true;
            WriteMeta(folder, meta);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // Abar lock: folder e ja ache (notun file shoho) shob encrypt + ACL deny.
    // null = bhul key. Nahole je file gulo encrypt hoyni tar list (khali = shob thik).
    public static List<string>? Seal(string folder, byte[] masterKey, IProgress<FolderProgress>? progress = null)
    {
        byte[]? dataKey = GetDataKey(folder, masterKey);
        if (dataKey == null) return null;

        try
        {
            return SealInternal(folder, dataKey, progress);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // Lock permanently shoriye dey (Manager er "Remove lock")
    public static bool Unlock(string folder, byte[] masterKey, IProgress<FolderProgress>? progress = null)
    {
        byte[]? dataKey = GetDataKey(folder, masterKey);
        if (dataKey == null) return false;

        try
        {
            AclGuard.Unprotect(folder);
            var failed = DecryptAll(folder, dataKey, progress);
            if (failed.Count > 0)
            {
                var meta = ReadMeta(folder);
                if (!meta.IsOpen)
                {
                    try { AclGuard.Protect(folder); } catch { }
                }
                throw BuildDecryptFailure(failed);
            }

            string metaPath = Path.Combine(folder, MetaName);
            if (File.Exists(metaPath))
            {
                NormalizeAttributes(metaPath);
                RetryIo(() => File.Delete(metaPath));
            }
            try { File.Delete(BackupPath(folder)); } catch { }
            LockRegistry.Remove(folder);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // ---------- bhitorer kaj ----------

    private static List<string> SealInternal(string folder, byte[] dataKey, IProgress<FolderProgress>? progress)
    {
        // Remove any lingering Deny ACL first so Locker itself can create/replace .aplk files cleanly
        try { AclGuard.Unprotect(folder); } catch { }

        var files = Directory.EnumerateFiles(folder, "*", AllFilesOptions)
            .Where(f => !f.EndsWith(Ext, StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(WorkTmpSuffix, StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(Ext + ".tmp", StringComparison.OrdinalIgnoreCase)
                     && !SkipFileNames.Contains(Path.GetFileName(f)))
            .ToList();

        var failures = RunParallelFileCrypto(files, progress, (file, onBytes) =>
        {
            string encPath = file + Ext;
            string tmpPath = encPath + WorkTmpSuffix;
            try
            {
                Crypto.EncryptFile(file, tmpPath, dataKey, onBytes);
                if (File.Exists(encPath)) NormalizeAttributes(encPath);
                RetryIo(() => File.Move(tmpPath, encPath, overwrite: true));
                NormalizeAttributes(file);
                RetryIo(() => File.Delete(file));
            }
            catch
            {
                CleanupTemp(tmpPath);
                throw;
            }
        });

        var failed = failures.Select(x => x.Path).ToList();
        var meta = ReadMeta(folder);
        meta.IsOpen = failed.Count > 0;
        WriteMeta(folder, meta);

        AclGuard.Protect(folder);
        return failed;
    }

    private static List<(string Path, Exception Error)> DecryptAll(string folder, byte[] dataKey, IProgress<FolderProgress>? progress)
    {
        var files = Directory.EnumerateFiles(folder, "*" + Ext, AllFilesOptions)
            .Where(f => f.EndsWith(Ext, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return RunParallelFileCrypto(files, progress, (enc, onBytes) =>
        {
            string original = enc[..^Ext.Length];
            string tmpPath = original + WorkTmpSuffix;
            try
            {
                Crypto.DecryptFile(enc, tmpPath, dataKey, onBytes);
                if (File.Exists(original)) NormalizeAttributes(original);
                RetryIo(() => File.Move(tmpPath, original, overwrite: true));
                NormalizeAttributes(enc);
                RetryIo(() => File.Delete(enc));
            }
            catch
            {
                CleanupTemp(tmpPath);
                throw;
            }
        });
    }

    private static List<(string Path, Exception Error)> RunParallelFileCrypto(
        List<string> files,
        IProgress<FolderProgress>? progress,
        Action<string, Action<long>> work)
    {
        var failed = new List<(string Path, Exception Error)>();
        int totalFiles = files.Count;
        long totalBytes = 0;
        foreach (var f in files)
        {
            try { totalBytes += new FileInfo(f).Length; } catch { }
        }

        if (totalFiles == 0)
        {
            progress?.Report(new FolderProgress(0, 0, 0, 0, ""));
            return failed;
        }

        long bytesDone = 0;
        int filesDone = 0;
        long lastTick = 0;

        void Report(long b, int d, string name, bool force)
        {
            if (progress == null) return;
            long now = Environment.TickCount64;
            long prev = Volatile.Read(ref lastTick);
            if (!force && now - prev < 60) return;
            if (!force && Interlocked.CompareExchange(ref lastTick, now, prev) != prev) return;
            progress.Report(new FolderProgress(d, totalFiles, Math.Min(b, totalBytes > 0 ? totalBytes : b), totalBytes, name));
        }

        Report(0, 0, Path.GetFileName(files[0]), force: true);

        int maxDegree = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = maxDegree }, file =>
        {
            string name = Path.GetFileName(file);
            try
            {
                work(file, delta =>
                {
                    long b = Interlocked.Add(ref bytesDone, delta);
                    Report(b, Volatile.Read(ref filesDone), name, force: false);
                });
                int d = Interlocked.Increment(ref filesDone);
                Report(Volatile.Read(ref bytesDone), d, name, force: d == totalFiles);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref filesDone);
                lock (failed) failed.Add((file, ex));
            }
        });

        Report(totalBytes, totalFiles, "", force: true);
        return failed;
    }

    private static Exception BuildDecryptFailure(List<(string Path, Exception Error)> failed)
    {
        var first = failed[0];
        if (first.Error is CryptographicException or InvalidDataException)
            return first.Error;

        string names = string.Join(", ", failed.Take(2).Select(x => Path.GetFileName(x.Path)));
        string more = failed.Count > 2 ? $" (+{failed.Count - 2} more)" : "";
        return new IOException(
            $"{failed.Count} file(s) could not be unlocked (open in another app or Windows preview?): {names}{more}. Close them and try again.",
            first.Error);
    }

    private static void NormalizeAttributes(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch { }
    }

    private static void CleanupTemp(string tmpPath)
    {
        try
        {
            if (File.Exists(tmpPath))
            {
                NormalizeAttributes(tmpPath);
                File.Delete(tmpPath);
            }
        }
        catch { }
    }

    private static void RetryIo(Action action, int attempts = 6, int delayMs = 150)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < attempts - 1)
            {
                Thread.Sleep(delayMs * (i + 1));
            }
        }
    }

    private static byte[]? GetDataKey(string folder, byte[] masterKey)
    {
        var meta = ReadMeta(folder);
        try { return KeyWrap.Unwrap(Convert.FromBase64String(meta.WrappedDataKey), masterKey); }
        catch (CryptographicException) { return null; }
    }

    private static LockMeta ReadMeta(string folder)
    {
        string p = Path.Combine(folder, MetaName);
        if (!File.Exists(p)) throw new FileNotFoundException("Folder is not locked.");
        return JsonSerializer.Deserialize<LockMeta>(File.ReadAllText(p))!;
    }

    // Hidden file e seedha likhle error hoy, tai age attribute normal kori
    private static void WriteMeta(string folder, LockMeta meta)
    {
        string p = Path.Combine(folder, MetaName);
        if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
        File.WriteAllText(p, JsonSerializer.Serialize(meta));
        File.SetAttributes(p, FileAttributes.Hidden);

        try
        {
            string bp = BackupPath(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(bp)!);
            File.WriteAllText(bp, JsonSerializer.Serialize(meta));
        }
        catch { }
    }
}