using System.IO;
using System.Text.Json;

namespace AppLocker.Core;

public class PairedPhoneInfo
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string LastIpAddress { get; set; } = "";
    public string SharedSecretKey { get; set; } = "";
    public DateTime PairedAt { get; set; } = DateTime.UtcNow;
    public bool FingerprintSupported { get; set; } = true;
    public bool FaceSupported { get; set; } = true;
}


public class SavedVaultItem
{
    public string VaultId { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public bool HasPhoneBiometrics { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class LockerSettingsData
{
    public bool InexIslandAnimation { get; set; } = true;
    public bool PhoneBiometricsEnabled { get; set; } = false;
    public bool RequireSameWiFi { get; set; } = true;
    public bool RequireBluetooth { get; set; } = false;
    public string PreferredBiometric { get; set; } = "Any"; // Any, Fingerprint, Face
    public PairedPhoneInfo? PairedPhone { get; set; }
    public List<SavedVaultItem> SavedVaults { get; set; } = new();
}

public static class LockerSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppLocker");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static LockerSettingsData? _cache;
    private static DateTime _lastLoadedUtc = DateTime.MinValue;
    private static readonly object _lock = new();

    public static LockerSettingsData Current
    {
        get
        {
            lock (_lock)
            {
                if (File.Exists(SettingsFile))
                {
                    var writeTime = File.GetLastWriteTimeUtc(SettingsFile);
                    if (_cache != null && writeTime <= _lastLoadedUtc)
                    {
                        return _cache;
                    }
                    _cache = Load();
                    _lastLoadedUtc = writeTime;
                    return _cache;
                }
                if (_cache != null) return _cache;
                _cache = Load();
                _lastLoadedUtc = DateTime.UtcNow;
                return _cache;
            }
        }
    }

    public static LockerSettingsData Load()
    {
        DirHarden.RestrictToAdminsAndSystem(SettingsDir);
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                var data = JsonSerializer.Deserialize<LockerSettingsData>(json);
                if (data != null) return data;
            }
        }
        catch { }

        var def = new LockerSettingsData();
        Save(def);
        return def;
    }

    public static void Save(LockerSettingsData data)
    {
        lock (_lock)
        {
            _cache = data;
            try
            {
                Directory.CreateDirectory(SettingsDir);
                DirHarden.RestrictToAdminsAndSystem(SettingsDir); // settings.json holds the phone shared secret
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }
    }
}
