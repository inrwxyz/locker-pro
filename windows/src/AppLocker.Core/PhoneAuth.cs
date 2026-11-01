using System.Security.Cryptography;
using System.Text;

namespace AppLocker.Core;

// Phone <-> PC message authentication.
//
// Transport independent: Wi-Fi (TCP) uses it today, a Bluetooth transport can reuse the
// exact same Sign/Verify + nonce functions without any change.
//
// Rules this class enforces:
//   * the shared secret is NEVER sent after pairing - only HMAC-SHA256 proofs are sent
//   * an approval is only accepted for a request the PC itself issued (single-use nonce)
//   * unsolicited signed messages (ping) need a fresh timestamp + a never-seen nonce
//   * all MAC comparisons are constant time
public static class PhoneAuth
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(10);
    private const int MaxClockSkewSeconds = 300;
    private const int MaxTrackedNonces = 2048;

    private sealed class Pending
    {
        public string Target = "";
        public string ActionType = "";
        public DateTime ExpiresUtc;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Pending> PendingRequests = new();
    private static readonly Dictionary<string, DateTime> SeenNonces = new();

    public static string NewNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsValidNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length < 16 || nonce.Length > 64) return false;
        foreach (char c in nonce)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    // Canonical form: purpose + "\n" + "len:value|len:value|..."
    // Length prefixes make field boundaries unambiguous (a '|' inside a folder name cannot
    // shift fields). The Android side (PhoneAuth.java) builds the exact same bytes.
    private static byte[] Canonical(string purpose, string[] fields)
    {
        var sb = new StringBuilder(purpose).Append('\n');
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append('|');
            string f = fields[i] ?? "";
            sb.Append(f.Length).Append(':').Append(f);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string Sign(string secret, string purpose, params string[] fields)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Canonical(purpose, fields))).ToLowerInvariant();
    }

    public static bool Verify(string? secret, string? mac, string purpose, params string[] fields)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(mac)) return false;
        byte[] want = Encoding.ASCII.GetBytes(Sign(secret, purpose, fields));
        byte[] got = Encoding.ASCII.GetBytes(mac.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(want, got); // false when lengths differ
    }

    // ---- requests the PC issues (PC -> phone auth_request) ---------------------------------

    public static string BeginRequest(string target, string actionType)
    {
        string nonce = NewNonce();
        lock (Gate)
        {
            PurgeLocked();
            PendingRequests[nonce] = new Pending
            {
                Target = target,
                ActionType = actionType,
                ExpiresUtc = DateTime.UtcNow + RequestLifetime
            };
        }
        return nonce;
    }

    // Single use: returns true only the first time, only for a nonce this PC issued, only if
    // target + action match what was asked.
    public static bool TryConsume(string nonce, string target, string actionType)
    {
        lock (Gate)
        {
            PurgeLocked();
            if (!PendingRequests.TryGetValue(nonce, out var p)) return false;
            if (!string.Equals(p.Target, target, StringComparison.Ordinal)) return false;
            if (!string.Equals(p.ActionType, actionType, StringComparison.OrdinalIgnoreCase)) return false;
            PendingRequests.Remove(nonce);
            return true;
        }
    }

    public static void Cancel(string nonce)
    {
        lock (Gate) { PendingRequests.Remove(nonce); }
    }

    // ---- unsolicited signed messages (phone -> PC ping) ------------------------------------

    // Call only AFTER the MAC verified, so unauthenticated callers cannot fill the cache.
    public static bool CheckFreshAndRemember(string nonce, long unixSeconds)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > MaxClockSkewSeconds) return false;
        lock (Gate)
        {
            PurgeLocked();
            if (SeenNonces.ContainsKey(nonce)) return false;
            if (SeenNonces.Count >= MaxTrackedNonces) return false;
            SeenNonces[nonce] = DateTime.UtcNow + ReplayWindow;
            return true;
        }
    }

    private static void PurgeLocked()
    {
        var now = DateTime.UtcNow;
        List<string>? dead = null;
        foreach (var kv in PendingRequests)
            if (kv.Value.ExpiresUtc <= now) (dead ??= new()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) PendingRequests.Remove(k);

        dead = null;
        foreach (var kv in SeenNonces)
            if (kv.Value <= now) (dead ??= new()).Add(kv.Key);
        if (dead != null) foreach (var k in dead) SeenNonces.Remove(k);
    }
}
