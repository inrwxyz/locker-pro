using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppLocker.Core;

// Ek key ke arek key diye AES-GCM encrypt/decrypt: nonce(12) + tag(16) + cipher
public static class KeyWrap
{
    public static byte[] Wrap(byte[] data, byte[] key)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[data.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return nonce.Concat(tag).Concat(cipher).ToArray();
    }

    // Bhul key hole CryptographicException chhure
    public static byte[] Unwrap(byte[] blob, byte[] key)
    {
        byte[] nonce = blob[..12];
        byte[] tag = blob[12..28];
        byte[] cipher = blob[28..];
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

// biometric.v2.json: the PC half of the fingerprint unlock.
//   Wrapped  = RSA-OAEP(phone public key, U)   -> can only be opened by the phone's Keystore key, and that key
//                                                 needs a live fingerprint (and dies if a new finger is added)
//   KeyBlob  = AES-GCM(master key, U)
// U itself is never stored on the PC. Reading this file (even as admin) gives nothing without the phone + finger.
public class BioVaultV2
{
    public int Version { get; set; } = 2;
    public string DeviceId { get; set; } = "";
    public string Wrapped { get; set; } = "";
    public string KeyBlob { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class MasterData
{
    public string PasswordSalt { get; set; } = "";
    public string WrappedByPassword { get; set; } = "";
    public string RecoverySalt { get; set; } = "";
    public string WrappedByRecovery { get; set; } = "";
}

public static class MasterConfig
{
    // Test er shomoy ei path bodle neya jay
    public static string ConfigDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppLocker");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static bool IsSetup() => File.Exists(ConfigPath);

    // Prothom bar setup. Recovery key return kore, user ke ekbar dekhate hobe.
    public static string Setup(string password)
    {
        if (IsSetup()) throw new InvalidOperationException("Already setup.");
        Directory.CreateDirectory(ConfigDir);

        byte[] master = RandomNumberGenerator.GetBytes(32);

        byte[] pwSalt = Crypto.NewSalt();
        byte[] pwKey = Crypto.DeriveKey(password, pwSalt);

        string recoveryText = FormatRecovery(RandomNumberGenerator.GetBytes(20));
        byte[] recSalt = Crypto.NewSalt();
        byte[] recKey = Crypto.DeriveKey(recoveryText, recSalt);

        var data = new MasterData
        {
            PasswordSalt = Convert.ToBase64String(pwSalt),
            WrappedByPassword = Convert.ToBase64String(KeyWrap.Wrap(master, pwKey)),
            RecoverySalt = Convert.ToBase64String(recSalt),
            WrappedByRecovery = Convert.ToBase64String(KeyWrap.Wrap(master, recKey))
        };
        Save(data);

        CryptographicOperations.ZeroMemory(master);
        return recoveryText;
    }

    public static string BiometricVaultPath => Path.Combine(ConfigDir, "biometric.vault");
    public static string BiometricVaultV2Path => Path.Combine(ConfigDir, "biometric.v2.json");

    public static bool HasBiometricVaultV2() => File.Exists(BiometricVaultV2Path);

    public static BioVaultV2? LoadBiometricVaultV2()
    {
        try
        {
            if (!File.Exists(BiometricVaultV2Path)) return null;
            var v = JsonSerializer.Deserialize<BioVaultV2>(File.ReadAllText(BiometricVaultV2Path));
            return (v == null || v.Version != 2 || v.Wrapped.Length == 0 || v.KeyBlob.Length == 0) ? null : v;
        }
        catch { return null; }
    }

    // masterKey + phone public key (X.509 SPKI) -> new random U, store only RSA(U) and AES(master, U).
    public static void SaveBiometricVaultV2(byte[] masterKey, byte[] phonePublicSpki, string deviceId)
    {
        byte[] u = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(phonePublicSpki, out _);
            if (rsa.KeySize < 2048) throw new CryptographicException("Phone key too small.");
            byte[] wrapped = rsa.Encrypt(u, RSAEncryptionPadding.OaepSHA1);

            var v = new BioVaultV2
            {
                DeviceId = deviceId,
                Wrapped = Convert.ToBase64String(wrapped),
                KeyBlob = Convert.ToBase64String(KeyWrap.Wrap(masterKey, u)),
                CreatedUtc = DateTime.UtcNow
            };

            Directory.CreateDirectory(ConfigDir);
            string tmp = BiometricVaultV2Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(v));
            File.Move(tmp, BiometricVaultV2Path, overwrite: true);
            try { if (File.Exists(BiometricVaultPath)) File.Delete(BiometricVaultPath); } catch { } // old insecure vault
        }
        finally { CryptographicOperations.ZeroMemory(u); }
    }

    // U (released by the phone after a fingerprint) -> master key, or null.
    public static byte[]? OpenBiometricVaultV2(byte[] u)
    {
        var v = LoadBiometricVaultV2();
        if (v == null) return null;
        try { return KeyWrap.Unwrap(Convert.FromBase64String(v.KeyBlob), u); }
        catch { return null; }
    }

    [Obsolete("Insecure legacy vault (key sat next to the data on the PC). Use SaveBiometricVaultV2.")]
    public static void SaveBiometricVault(byte[] masterKey, string sharedSecret)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
            byte[] wrapped = KeyWrap.Wrap(masterKey, keyBytes);
            File.WriteAllBytes(BiometricVaultPath, wrapped);
        }
        catch { }
    }

    [Obsolete("Insecure legacy vault. Use PhoneCompanionService.RequestVaultKeyAsync.")]
    public static byte[]? GetBiometricVaultKey(string sharedSecret)
    {
        try
        {
            if (!File.Exists(BiometricVaultPath)) return null;
            byte[] wrapped = File.ReadAllBytes(BiometricVaultPath);
            byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
            return KeyWrap.Unwrap(wrapped, keyBytes);
        }
        catch
        {
            return null;
        }
    }

    public static void DeleteBiometricVault()
    {
        try { if (File.Exists(BiometricVaultPath)) File.Delete(BiometricVaultPath); } catch { }
        try { if (File.Exists(BiometricVaultV2Path)) File.Delete(BiometricVaultV2Path); } catch { }
    }

    // Sothik password hole master key, bhul hole null
    public static byte[]? GetKeyWithPassword(string password)
    {
        var d = Load();
        byte[] pwKey = Crypto.DeriveKey(password, Convert.FromBase64String(d.PasswordSalt));
        try
        {
            byte[] key = KeyWrap.Unwrap(Convert.FromBase64String(d.WrappedByPassword), pwKey);
            // the old biometric.vault (master key wrapped with a secret stored on the same PC) is gone for good
            try { if (File.Exists(BiometricVaultPath)) File.Delete(BiometricVaultPath); } catch { }
            return key;
        }
        catch (CryptographicException) { return null; }
    }

    public static byte[]? GetKeyWithRecovery(string recoveryText)
    {
        var d = Load();
        byte[] recKey = Crypto.DeriveKey(Normalize(recoveryText), Convert.FromBase64String(d.RecoverySalt));
        try { return KeyWrap.Unwrap(Convert.FromBase64String(d.WrappedByRecovery), recKey); }
        catch (CryptographicException) { return null; }
    }

    // Password change: shob folder er jonno ekbar-i change hoy
    public static bool ChangePassword(string oldPassword, string newPassword)
    {
        byte[]? master = GetKeyWithPassword(oldPassword);
        if (master == null) return false;

        var d = Load();
        byte[] newSalt = Crypto.NewSalt();
        byte[] newKey = Crypto.DeriveKey(newPassword, newSalt);
        d.PasswordSalt = Convert.ToBase64String(newSalt);
        d.WrappedByPassword = Convert.ToBase64String(KeyWrap.Wrap(master, newKey));
        Save(d);

        CryptographicOperations.ZeroMemory(master);
        return true;
    }

    // Password bhule gele: recovery key diye notun password set
    public static bool ResetPasswordWithRecovery(string recoveryText, string newPassword)
    {
        byte[]? master = GetKeyWithRecovery(recoveryText);
        if (master == null) return false;

        var d = Load();
        byte[] newSalt = Crypto.NewSalt();
        byte[] newKey = Crypto.DeriveKey(newPassword, newSalt);
        d.PasswordSalt = Convert.ToBase64String(newSalt);
        d.WrappedByPassword = Convert.ToBase64String(KeyWrap.Wrap(master, newKey));
        Save(d);

        CryptographicOperations.ZeroMemory(master);
        return true;
    }

    public static MasterData Load()
    {
        if (!File.Exists(ConfigPath)) throw new FileNotFoundException("AppLocker setup hoyni.");
        return JsonSerializer.Deserialize<MasterData>(File.ReadAllText(ConfigPath))!;
    }

    private static void Save(MasterData d)
    {
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(d));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private static string FormatRecovery(byte[] raw)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder();
        foreach (byte b in raw) sb.Append(alphabet[b % alphabet.Length]);
        string s = sb.ToString();
        return string.Join("-", Enumerable.Range(0, s.Length / 4).Select(i => s.Substring(i * 4, 4)));
    }

    private static string Normalize(string s) => s.Trim().ToUpperInvariant();
}
