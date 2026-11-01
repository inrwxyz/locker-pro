using System.Security.Cryptography;
using System.Text.Json;

namespace AppLocker.Core;

public class LockMeta
{
    public string WrappedDataKey { get; set; } = "";
    public bool IsOpen { get; set; }   // true = file gulo ekhon normal (temporary unlock)
}

// Kon kon folder AppLocker er under e, tar list (Manager eta dekhabe)
public static class LockRegistry
{
    private static string Path_ => Path.Combine(MasterConfig.ConfigDir, "items.json");

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
            if (!Directory.EnumerateFiles(folder, "*" + Ext, SearchOption.AllDirectories).Any()) return false;

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
    public static void Lock(string folder, byte[] masterKey)
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

        SealInternal(folder, dataKey);
        CryptographicOperations.ZeroMemory(dataKey);
    }

    // Temporary unlock: ACL shoriye file gulo decrypt kore normal kore dey.
    public static bool Open(string folder, byte[] masterKey)
    {
        byte[]? dataKey = GetDataKey(folder, masterKey);
        if (dataKey == null) return false;

        AclGuard.Unprotect(folder);
        DecryptAll(folder, dataKey);

        var meta = ReadMeta(folder);
        meta.IsOpen = true;
        WriteMeta(folder, meta);

        CryptographicOperations.ZeroMemory(dataKey);
        return true;
    }

    // Abar lock: folder e ja ache (notun file shoho) shob encrypt + ACL deny.
    // null = bhul key. Nahole je file gulo encrypt hoyni tar list (khali = shob thik).
    public static List<string>? Seal(string folder, byte[] masterKey)
    {
        byte[]? dataKey = GetDataKey(folder, masterKey);
        if (dataKey == null) return null;

        var failed = SealInternal(folder, dataKey);
        CryptographicOperations.ZeroMemory(dataKey);
        return failed;
    }

    // Lock permanently shoriye dey (Manager er "Remove lock")
    public static bool Unlock(string folder, byte[] masterKey)
    {
        byte[]? dataKey = GetDataKey(folder, masterKey);
        if (dataKey == null) return false;

        AclGuard.Unprotect(folder);
        DecryptAll(folder, dataKey);
        File.Delete(Path.Combine(folder, MetaName));
        try { File.Delete(BackupPath(folder)); } catch { }
        LockRegistry.Remove(folder);

        CryptographicOperations.ZeroMemory(dataKey);
        return true;
    }

    // ---------- bhitorer kaj ----------

    private static List<string> SealInternal(string folder, byte[] dataKey)
    {
        var failed = new List<string>();
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(Ext) && !Path.GetFileName(f).Equals(MetaName)).ToList();

        foreach (var file in files)
        {
            string encPath = file + Ext;
            string tmpPath = encPath + ".tmp";
            try
            {
                Crypto.EncryptFile(file, tmpPath, dataKey);
                File.Move(tmpPath, encPath, overwrite: true);
                File.Delete(file);
            }
            catch (Exception)
            {
                // file onno program e khola thakle ekhane ashe
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                failed.Add(file);
            }
        }

        var meta = ReadMeta(folder);
        meta.IsOpen = failed.Count > 0;
        WriteMeta(folder, meta);

        AclGuard.Protect(folder);
        return failed;
    }

    private static void DecryptAll(string folder, byte[] dataKey)
    {
        var files = Directory.GetFiles(folder, "*" + Ext, SearchOption.AllDirectories).ToList();
        foreach (var enc in files)
        {
            string original = enc.Substring(0, enc.Length - Ext.Length);
            string tmp = original + ".tmp";
            Crypto.DecryptFile(enc, tmp, dataKey);
            File.Move(tmp, original, overwrite: true);
            File.Delete(enc);
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
        if (!File.Exists(p)) throw new FileNotFoundException("Folder locked na.");
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