using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AppLocker.Core;

// Password-only portable vault.
//   * Chosen folder is encrypted IN PLACE (no clone, no zip).
//   * Every file becomes "<name>.locked" (AES-256-GCM, see Crypto.cs).
//   * vault.inex (inside the same folder) holds salt + the password-wrapped vault key,
//     so the folder can be carried on a pendrive and opened on ANY PC that has Locker Pro,
//     with just the vault password.
//   * Phone fingerprint is NOT used for vaults any more.
public class VaultMetadata
{
    public int Version { get; set; } = 2;
    public string VaultId { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string SaltBase64 { get; set; } = "";
    public string KeyEnvPwBase64 { get; set; } = "";
    public bool IsLocked { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastLockedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class PortableVaultService
{
    public const string MetaFileName = "vault.inex";
    public const string InfoFileName = "HOW_TO_UNLOCK.txt";
    public const string LockedExt = ".locked";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    public static bool IsVault(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return false;
        return File.Exists(Path.Combine(folderPath, MetaFileName));
    }

    public static bool IsVaultLocked(string folderPath)
    {
        if (!IsVault(folderPath)) return false;
        try
        {
            var meta = LoadMetadata(folderPath);
            return meta != null && meta.IsLocked;
        }
        catch
        {
            return false;
        }
    }

    public static VaultMetadata? LoadMetadata(string folderPath)
    {
        string metaPath = Path.Combine(folderPath, MetaFileName);
        if (!File.Exists(metaPath)) return null;
        string json = File.ReadAllText(metaPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<VaultMetadata>(json);
    }

    public static void SaveMetadata(string folderPath, VaultMetadata meta)
    {
        string metaPath = Path.Combine(folderPath, MetaFileName);
        string tmp = metaPath + ".tmp";
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, options), Encoding.UTF8);
        File.Move(tmp, metaPath, overwrite: true);
    }

    // -----------------------------------------------------------------------------------
    // LOCK A FOLDER INTO A VAULT (in place)
    // -----------------------------------------------------------------------------------
    public static async Task<VaultMetadata> CreateAndLockVaultAsync(
        string folderPath,
        string password,
        IProgress<(int current, int total, string currentFile)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("Folder not found: " + folderPath);

        folderPath = Path.GetFullPath(folderPath).TrimEnd('\\');

        if (!LockSafety.IsSafeVaultFolder(folderPath, out string why))
            throw new InvalidOperationException(why);

        if (IsVault(folderPath))
            throw new InvalidOperationException("This folder is already a vault.");

        if (string.IsNullOrEmpty(password) || password.Length < 4)
            throw new ArgumentException("Vault password must be at least 4 characters long.");

        var targetFiles = CollectPlainFiles(folderPath);
        if (targetFiles.Count == 0)
            throw new InvalidOperationException("No files found in the folder to encrypt.");

        byte[] vaultKey = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            byte[] salt = Crypto.NewSalt();
            byte[] pwKey = Crypto.DeriveKey(password, salt);
            byte[] keyEnvPw = EncryptKeyEnvelope(vaultKey, pwKey);
            CryptographicOperations.ZeroMemory(pwKey);

            var meta = new VaultMetadata
            {
                Version = 2,
                VaultId = Guid.NewGuid().ToString("N")[..16],
                FolderName = new DirectoryInfo(folderPath).Name,
                SaltBase64 = Convert.ToBase64String(salt),
                KeyEnvPwBase64 = Convert.ToBase64String(keyEnvPw),
                IsLocked = true,
                CreatedAtUtc = DateTime.UtcNow,
                LastLockedAtUtc = DateTime.UtcNow
            };

            // Save the key envelope BEFORE touching any file: if the PC crashes half way,
            // the password can still open whatever was already encrypted.
            SaveMetadata(folderPath, meta);
            WriteUnlockNotice(folderPath, meta.FolderName);

            var failed = await Task.Run(() => EncryptFiles(targetFiles, vaultKey, progress));

            meta.IsLocked = failed.Count == 0;
            meta.LastLockedAtUtc = DateTime.UtcNow;
            SaveMetadata(folderPath, meta);
            AddSavedVaultRecord(meta.VaultId, folderPath, meta.FolderName);

            if (failed.Count > 0)
                throw new IOException(FailMessage("lock", failed));

            return meta;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    // -----------------------------------------------------------------------------------
    // UNLOCK WITH PASSWORD
    // -----------------------------------------------------------------------------------
    public static async Task UnlockVaultWithPasswordAsync(
        string folderPath,
        string password,
        IProgress<(int current, int total, string currentFile)>? progress = null)
    {
        var meta = LoadMetadata(folderPath)
            ?? throw new InvalidOperationException("Not a valid Locker Pro vault folder (missing metadata).");

        byte[] vaultKey = OpenVaultKey(meta, password);
        try
        {
            var failed = await Task.Run(() => DecryptFiles(folderPath, vaultKey, progress));
            if (failed.Count > 0)
                throw new IOException(FailMessage("unlock", failed));

            meta.IsLocked = false;
            SaveMetadata(folderPath, meta);
            RemoveUnlockNotice(folderPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    // -----------------------------------------------------------------------------------
    // RELOCK AN UNLOCKED VAULT (new / changed files are picked up automatically)
    // -----------------------------------------------------------------------------------
    public static async Task RelockVaultAsync(
        string folderPath,
        string password,
        IProgress<(int current, int total, string currentFile)>? progress = null)
    {
        var meta = LoadMetadata(folderPath)
            ?? throw new InvalidOperationException("Not a valid vault folder.");

        byte[] vaultKey = OpenVaultKey(meta, password);
        try
        {
            var targetFiles = CollectPlainFiles(folderPath);

            // Crash safety: mark locked first. Unlock scans the disk for *.locked files, so a
            // half finished relock can always be opened again with the password.
            meta.IsLocked = true;
            meta.LastLockedAtUtc = DateTime.UtcNow;
            SaveMetadata(folderPath, meta);
            WriteUnlockNotice(folderPath, meta.FolderName);

            var failed = await Task.Run(() => EncryptFiles(targetFiles, vaultKey, progress));

            if (failed.Count > 0)
            {
                meta.IsLocked = false;
                SaveMetadata(folderPath, meta);
                throw new IOException(FailMessage("lock", failed));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    // -----------------------------------------------------------------------------------
    // INTERNAL HELPERS
    // -----------------------------------------------------------------------------------
    private static byte[] OpenVaultKey(VaultMetadata meta, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new UnauthorizedAccessException("Enter vault password.");

        byte[] salt = Convert.FromBase64String(meta.SaltBase64);
        byte[] keyEnvPw = Convert.FromBase64String(meta.KeyEnvPwBase64);
        byte[] pwKey = Crypto.DeriveKey(password, salt);
        try
        {
            return DecryptKeyEnvelope(keyEnvPw, pwKey);
        }
        catch (CryptographicException)
        {
            throw new UnauthorizedAccessException("Incorrect vault password!");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwKey);
        }
    }

    private static bool IsVaultInternalFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals(MetaFileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(MetaFileName + ".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals(InfoFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CollectPlainFiles(string folderPath)
    {
        return Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(f => !IsVaultInternalFile(f)
                     && !f.EndsWith(LockedExt, StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(LockedExt + ".tmp", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".unlocking.tmp", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // Returns the files that could NOT be encrypted (e.g. open in another program).
    private static List<string> EncryptFiles(
        List<string> files, byte[] vaultKey,
        IProgress<(int current, int total, string currentFile)>? progress)
    {
        var failed = new List<string>();
        int total = files.Count;
        int current = 0;

        foreach (var src in files)
        {
            current++;
            progress?.Report((current, total, Path.GetFileName(src)));

            string locked = src + LockedExt;
            string tmp = locked + ".tmp";
            try
            {
                Crypto.EncryptFile(src, tmp, vaultKey);
                File.Move(tmp, locked, overwrite: true);
                File.SetAttributes(src, FileAttributes.Normal);
                File.Delete(src);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                // original is still there; if the encrypted copy was already moved in, drop it
                // so a half-finished file never looks like the real one
                try { if (File.Exists(src) && File.Exists(locked)) File.Delete(locked); } catch { }
                failed.Add(src);
            }
        }
        return failed;
    }

    // Decrypts every *.locked file found on disk (does not depend on any file list).
    private static List<string> DecryptFiles(
        string folderPath, byte[] vaultKey,
        IProgress<(int current, int total, string currentFile)>? progress)
    {
        var failed = new List<string>();
        var lockedFiles = Directory.GetFiles(folderPath, "*" + LockedExt, SearchOption.AllDirectories)
            .Where(f => f.EndsWith(LockedExt, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int total = lockedFiles.Count;
        int current = 0;

        foreach (var locked in lockedFiles)
        {
            current++;
            string orig = locked.Substring(0, locked.Length - LockedExt.Length);
            progress?.Report((current, total, Path.GetFileName(orig)));

            string tmp = orig + ".unlocking.tmp";
            try
            {
                Crypto.DecryptFile(locked, tmp, vaultKey);
                File.Move(tmp, orig, overwrite: true);
                File.SetAttributes(locked, FileAttributes.Normal);
                File.Delete(locked);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("Not an AppLocker"))
            {
                // a user's own file that just happens to end with ".locked": leave it alone
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                failed.Add(locked);
            }
        }
        return failed;
    }

    private static string FailMessage(string what, List<string> failed)
    {
        var names = string.Join(", ", failed.Take(3).Select(f => Path.GetFileName(f)));
        string more = failed.Count > 3 ? $" (+{failed.Count - 3} more)" : "";
        return what == "lock"
            ? $"{failed.Count} file(s) could not be locked (open in another app?): {names}{more}. Close them and try again."
            : $"{failed.Count} file(s) could not be unlocked (corrupt or tampered): {names}{more}.";
    }

    private static byte[] EncryptKeyEnvelope(byte[] keyToEncrypt, byte[] masterKey)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[keyToEncrypt.Length];
        using var aes = new AesGcm(masterKey, TagSize);
        aes.Encrypt(nonce, keyToEncrypt, ciphertext, tag);
        byte[] result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    private static byte[] DecryptKeyEnvelope(byte[] combined, byte[] masterKey)
    {
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Envelope data corrupted or too short.");
        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        int cipherLen = combined.Length - NonceSize - TagSize;
        byte[] ciphertext = new byte[cipherLen];
        byte[] plain = new byte[cipherLen];

        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(combined, NonceSize + TagSize, ciphertext, 0, cipherLen);

        using var aes = new AesGcm(masterKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plain);
        return plain;
    }

    private static void WriteUnlockNotice(string folderPath, string folderName)
    {
        try
        {
            string noticePath = Path.Combine(folderPath, InfoFileName);
            string content = $@"========================================================================
SECURE PORTABLE VAULT: {folderName}
========================================================================
This folder is encrypted with AES-256-GCM by Locker Pro.
All files inside this folder are unreadable without the vault password.

HOW TO UNLOCK & READ FILES:
1. Open Locker Pro on any Windows PC.
2. Click on the 'Vault Locker' section.
3. Drag & drop this folder (or browse to it).
4. Enter your Vault Password.

If you forget the vault password the files cannot be recovered.
========================================================================
Generated by Locker Pro (https://github.com/inrwxyz)
";
            File.WriteAllText(noticePath, content, Encoding.UTF8);
        }
        catch { }
    }

    private static void RemoveUnlockNotice(string folderPath)
    {
        try
        {
            string noticePath = Path.Combine(folderPath, InfoFileName);
            if (File.Exists(noticePath)) File.Delete(noticePath);
        }
        catch { }
    }

    private static void AddSavedVaultRecord(string vaultId, string folderPath, string folderName)
    {
        try
        {
            var settings = LockerSettings.Load();
            settings.SavedVaults.RemoveAll(v => v.FolderPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase) || v.VaultId == vaultId);
            settings.SavedVaults.Insert(0, new SavedVaultItem
            {
                VaultId = vaultId,
                FolderPath = folderPath,
                FolderName = folderName,
                HasPhoneBiometrics = false,
                AddedAt = DateTime.UtcNow
            });
            LockerSettings.Save(settings);
        }
        catch { }
    }
}
