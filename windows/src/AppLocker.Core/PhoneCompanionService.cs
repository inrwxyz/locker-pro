using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppLocker.Core;

// Phone <-> PC companion protocol (v2, QR-authenticated).
//
//   pair_qr            phone -> PC   scans PC's on-screen QR (contains IP, secret, session).
//                                    Sends HMAC proof over (session, deviceId). Secret never crosses network.
//   ping               phone -> PC   optional HMAC proof. Never returns the secret. Only an authenticated ping updates the phone IP.
//   auth_request       PC -> phone   HMAC signed, carries a fresh nonce
//   auth_reply         phone -> PC   HMAC over (status,type,action,target,nonce) on the same socket
//   biometric_approved phone -> PC   backup push: HMAC + the nonce of a request this PC issued (single use)
//
// All MACs live in PhoneAuth, which is transport independent.
public static class PhoneCompanionService
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    public const int DefaultPort = 58820;
    private const int PhonePort = 58821;

    private const int MaxLineChars = 4096;
    private const int ClientIoTimeoutMs = 8000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);

    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static PairingSession? _currentPairingSession;
    private static DateTime _pendingSessionExpiresUtc = DateTime.MinValue;
    private static Action<PairedPhoneInfo>? _onPairedCallback;
    public static event Action<string, string>? OnBiometricApproved;
    public static event Action? OnPhoneUnpaired;
    public static string LastAuthType { get; set; } = "fingerprint";
    public static string LastVaultToken { get; set; } = "";

    private static readonly string PairSessionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppLocker", ".pairing.pin");

    public sealed class PairingSession
    {
        public string SessionId { get; set; } = "";
        public string Secret { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public int Port { get; set; } = DefaultPort;
        public string PcName { get; set; } = "";
        public string QrPayload { get; set; } = "";
        public byte[] QrPngBytes { get; set; } = Array.Empty<byte>();
    }

    public static bool IsListening => _listener != null;

    // Start background local service to listen for phone connections (WiFi) with automatic retry
    public static void StartListener()
    {
        if (_listener != null) return;
        DirHarden.RestrictToAdminsAndSystem(MasterConfig.ConfigDir);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var listener = new TcpListener(IPAddress.Any, DefaultPort);
                    listener.Start();
                    _listener = listener;
                    await AcceptLoop(token);
                    return;
                }
                catch
                {
                    try { _listener?.Stop(); } catch { }
                    _listener = null;
                    if (attempt == 9) return;
                    await Task.Delay(1500, token);
                }
            }
        }, token);
    }

    public static void StopListener()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch { }
        finally
        {
            _listener = null;
            _cts = null;
        }
    }

    public static PairingSession GeneratePairingSession(Action<PairedPhoneInfo> onPaired)
    {
        _onPairedCallback = onPaired;
        byte[] secretBytes = RandomNumberGenerator.GetBytes(32);
        string secret = Convert.ToBase64String(secretBytes);
        string sessionId = Guid.NewGuid().ToString("N")[..16];
        string ip = GetLocalIpAddress();
        string pcName = Environment.MachineName;

        var payloadObj = new
        {
            action = "locker_pair",
            ip = ip,
            port = DefaultPort,
            secret = secret,
            pcName = pcName,
            session = sessionId
        };
        string qrPayload = JsonSerializer.Serialize(payloadObj);

        using var qrGenerator = new QRCoder.QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(qrPayload, QRCoder.QRCodeGenerator.ECCLevel.L);
        var qrCode = new QRCoder.PngByteQRCode(qrCodeData);
        byte[] qrPng = qrCode.GetGraphic(20, false);

        var session = new PairingSession
        {
            SessionId = sessionId,
            Secret = secret,
            IpAddress = ip,
            Port = DefaultPort,
            PcName = pcName,
            QrPayload = qrPayload,
            QrPngBytes = qrPng
        };

        _currentPairingSession = session;
        _pendingSessionExpiresUtc = DateTime.UtcNow + SessionLifetime;

        try
        {
            var dir = Path.GetDirectoryName(PairSessionFile)!;
            Directory.CreateDirectory(dir);
            DirHarden.RestrictToAdminsAndSystem(dir);
            string content = $"{sessionId}|{secret}|{_pendingSessionExpiresUtc.Ticks}";
            File.WriteAllText(PairSessionFile, content);
        }
        catch { }

        StartListener();
        return session;
    }

    public static void CancelPairing()
    {
        _currentPairingSession = null;
        _pendingSessionExpiresUtc = DateTime.MinValue;
        _onPairedCallback = null;
        try { if (File.Exists(PairSessionFile)) File.Delete(PairSessionFile); } catch { }
    }

    // Local IP address helper (QR code / pairing screen)
    public static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    // ---------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------

    // Only LAN / loopback / link-local / CGNAT (Tailscale) peers may talk to the companion port.
    private static bool IsLanAddress(IPAddress? a)
    {
        if (a == null) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && (b[1] & 0xC0) == 64);
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC; // fe80::/10, fc00::/7
        return false;
    }

    // Checks whether an IPv4 address belongs to the same Wi-Fi/LAN subnet as one of this PC's active NICs.
    private static bool IsOnSameWiFiSubnet(IPAddress? a)
    {
        if (a == null) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true; // allow local ADB / loopback testing
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;

        byte[] target = a.GetAddressBytes();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    byte[] local = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    bool match = true;
                    for (int i = 0; i < 4; i++)
                    {
                        if ((local[i] & mask[i]) != (target[i] & mask[i])) { match = false; break; }
                    }
                    if (match) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool PassesSameWiFiPolicy(IPAddress? remote)
    {
        var settings = LockerSettings.Current;
        if (!settings.RequireSameWiFi) return true;
        return IsOnSameWiFiSubnet(remote);
    }

    private static string RemoteIp(TcpClient client)
    {
        var addr = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (addr == null) return "";
        string ip = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
        if (ip.StartsWith("::ffff:")) ip = ip.Substring(7);
        return ip;
    }

    private static string Str(JsonElement root, string name, string fallback = "", int max = 512)
    {
        if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
        {
            string s = p.GetString() ?? fallback;
            return s.Length > max ? fallback : s; // too long -> treat as missing (MAC will not verify)
        }
        return fallback;
    }

    private static string CleanAuthType(string t)
        => t is "fingerprint" or "face" or "password" or "device_credential" ? t : "fingerprint";

    private static async Task<string?> ReadLineBoundedAsync(StreamReader r, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (sb.Length < max)
        {
            int n = await r.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (buf[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(buf[0]);
        }
        return null; // line too long
    }

    private static void RaiseApproved(string target, string authType, string actionType)
    {
        if (actionType.Equals("enroll", StringComparison.OrdinalIgnoreCase)) return; // key setup, not an unlock
        LastAuthType = authType;
        OnBiometricApproved?.Invoke(target, authType);
        if (actionType.Equals("lock", StringComparison.OrdinalIgnoreCase))
            InexIslandBridge.NotifyLock(target, authType);
        else
            InexIslandBridge.NotifyUnlock(authType, target);
    }

    private static async Task Reply(StreamWriter writer, object body, int delayMs = 0)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(body));
        await writer.FlushAsync();
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    // ---------------------------------------------------------------------------------------
    // server side (phone -> PC)
    // ---------------------------------------------------------------------------------------

    private static async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var remoteAddr = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (!IsLanAddress(remoteAddr))
                {
                    client.Close(); // never talk to the public internet
                    continue;
                }
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch
            {
                break;
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Utf8NoBom))
        using (var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true })
        {
            try
            {
                using var io = CancellationTokenSource.CreateLinkedTokenSource(ct);
                io.CancelAfter(ClientIoTimeoutMs);

                string? line = await ReadLineBoundedAsync(reader, MaxLineChars, io.Token);
                if (string.IsNullOrWhiteSpace(line)) return;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                string action = Str(root, "action", "", 32);

                if (action == "pair_qr" || action == "pair") await HandlePairQrAsync(client, root, writer);
                else if (action == "ping") await HandlePingAsync(client, root, writer);
                else if (action == "biometric_approved") await HandleApprovedAsync(client, root, writer);
                else if (action == "unpair") await HandleUnpairAsync(root, writer);
            }
            catch { }
        }
    }

    private static async Task HandlePairQrAsync(TcpClient client, JsonElement root, StreamWriter writer)
    {
        string session = Str(root, "session", "", 64);
        string deviceName = Str(root, "deviceName", "Android Phone", 64);
        string deviceId = Str(root, "deviceId", "", 64);
        string mac = Str(root, "mac", "", 128);
        if (root.TryGetProperty("vault_token", out var vtElem)) LastVaultToken = vtElem.GetString() ?? "";
        if (deviceId.Length == 0) deviceId = Guid.NewGuid().ToString("N");

        var current = _currentPairingSession;
        string? validSecret = null;
        string? validSessionId = null;

        if (current != null && DateTime.UtcNow < _pendingSessionExpiresUtc && current.SessionId == session)
        {
            validSecret = current.Secret;
            validSessionId = current.SessionId;
        }
        else
        {
            try
            {
                if (File.Exists(PairSessionFile))
                {
                    var parts = File.ReadAllText(PairSessionFile).Trim().Split('|');
                    if (parts.Length == 3 && parts[0] == session && long.TryParse(parts[2], out long expiryTicks)
                        && DateTime.UtcNow.Ticks < expiryTicks)
                    {
                        validSessionId = parts[0];
                        validSecret = parts[1];
                    }
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(validSecret) || string.IsNullOrEmpty(validSessionId))
        {
            await Reply(writer, new { status = "invalid_session" }, 200);
            return;
        }

        // Verify HMAC signature computed with the secret shown in the QR code
        bool verified = PhoneAuth.Verify(validSecret, mac, "pair_qr", validSessionId, deviceId);
        if (!verified)
        {
            await Reply(writer, new { status = "unauthorized" }, 200);
            return;
        }

        var info = new PairedPhoneInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            LastIpAddress = RemoteIp(client),
            SharedSecretKey = validSecret,
            PairedAt = DateTime.UtcNow
        };

        var settings = LockerSettings.Load();
        settings.PairedPhone = info;
        settings.PhoneBiometricsEnabled = true;
        LockerSettings.Save(settings);

        try { if (File.Exists(PairSessionFile)) File.Delete(PairSessionFile); } catch { }
        _currentPairingSession = null;
        _pendingSessionExpiresUtc = DateTime.MinValue;

        await Reply(writer, new { status = "paired", pcName = Environment.MachineName }, 200);
        try { client.Client.Shutdown(SocketShutdown.Both); } catch { }

        _ = Task.Run(() => _onPairedCallback?.Invoke(info));
    }

    private static async Task HandlePingAsync(TcpClient client, JsonElement root, StreamWriter writer)
    {
        var settings = LockerSettings.Load();
        var paired = settings.PairedPhone;

        // v2 phones sign the ping: HMAC(secret, "ping", ts, nonce). Unsigned/invalid pings still get a
        // harmless pong (so the app can show "PC online") but can NOT change anything and never get the secret.
        string nonce = Str(root, "nonce", "", 64);
        string tsText = Str(root, "ts", "", 20);
        string mac = Str(root, "mac", "", 128);

        bool authed = paired != null
            && PhoneAuth.IsValidNonce(nonce)
            && long.TryParse(tsText, out long ts)
            && PhoneAuth.Verify(paired.SharedSecretKey, mac, "ping", tsText, nonce)
            && PhoneAuth.CheckFreshAndRemember(nonce, long.Parse(tsText));

        if (authed)
        {
            string remoteIp = RemoteIp(client);
            if (!string.IsNullOrEmpty(remoteIp) && remoteIp != "127.0.0.1" && paired!.LastIpAddress != remoteIp)
            {
                paired.LastIpAddress = remoteIp; // only a proven phone may move the unlock target
                LockerSettings.Save(settings);
            }
        }

        await Reply(writer, new
        {
            status = "pong",
            pcName = Environment.MachineName,
            paired = paired != null,
            authenticated = authed,
            phoneName = authed ? paired!.DeviceName : null,
            islandConnected = authed ? InexIslandBridge.IsRunning() : (bool?)null
        }, 200);
        try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
    }

    private static async Task HandleApprovedAsync(TcpClient client, JsonElement root, StreamWriter writer)
    {
        var remoteAddr = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (!PassesSameWiFiPolicy(remoteAddr))
        {
            await Reply(writer, new { status = "wrong_wifi" });
            return;
        }

        var paired = LockerSettings.Load().PairedPhone;

        string target = Str(root, "target");
        string rawType = Str(root, "biometricType", "fingerprint", 32);
        string actionType = Str(root, "actionType", "unlock", 16);
        string nonce = Str(root, "nonce", "", 64);
        string mac = Str(root, "mac", "", 128);

        // 1) MAC proves the phone holds the shared secret (the secret itself is never sent)
        // 2) nonce proves this PC asked for this approval, once, in the last 60 seconds
        bool ok = paired != null
            && PhoneAuth.IsValidNonce(nonce)
            && PhoneAuth.Verify(paired.SharedSecretKey, mac, "approved", target, rawType, actionType, nonce)
            && PhoneAuth.TryConsume(nonce, target, actionType);

        if (!ok)
        {
            await Reply(writer, new { status = "unauthorized" });
            return;
        }

        if (root.TryGetProperty("vault_token", out var vtElem)) LastVaultToken = vtElem.GetString() ?? "";
        RaiseApproved(target, CleanAuthType(rawType), actionType);
        await Reply(writer, new { status = "success" });
    }

    private static async Task HandleUnpairAsync(JsonElement root, StreamWriter writer)
    {
        var settings = LockerSettings.Load();
        var paired = settings.PairedPhone;
        if (paired == null)
        {
            await Reply(writer, new { status = "unpaired" });
            return;
        }

        string nonce = Str(root, "nonce", "", 64);
        string tsText = Str(root, "ts", "", 20);
        string mac = Str(root, "mac", "", 128);

        bool ok = PhoneAuth.IsValidNonce(nonce)
            && long.TryParse(tsText, out long ts)
            && PhoneAuth.Verify(paired.SharedSecretKey, mac, "unpair", tsText, nonce)
            && PhoneAuth.CheckFreshAndRemember(nonce, ts);

        if (!ok)
        {
            await Reply(writer, new { status = "unauthorized" });
            return;
        }

        string unpairedName = paired.DeviceName;
        settings.PairedPhone = null;
        settings.PhoneBiometricsEnabled = false;
        MasterConfig.DeleteBiometricVault();
        LockerSettings.Save(settings);

        InexIslandBridge.NotifyPhoneUnpaired(unpairedName);
        OnPhoneUnpaired?.Invoke();
        await Reply(writer, new { status = "unpaired" });
    }

    public static async Task NotifyUnpairToPhoneAsync(string phoneIp, string secret)
    {
        if (string.IsNullOrWhiteSpace(phoneIp) || string.IsNullOrEmpty(secret)) return;
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(2500);
            await client.ConnectAsync(phoneIp, PhonePort, cts.Token);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };

            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string nonce = PhoneAuth.NewNonce();
            var req = new
            {
                action = "unpair",
                ts = ts,
                nonce = nonce,
                mac = PhoneAuth.Sign(secret, "unpair", ts, nonce)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(req));
            await writer.FlushAsync();
        }
        catch { }
    }

    // ---------------------------------------------------------------------------------------
    // client side (PC -> phone)
    // ---------------------------------------------------------------------------------------

    // Phone er sathe Biometric request pathano
    public static async Task<bool> RequestBiometricAuthAsync(string target, bool isLock = false, int timeoutMs = 12000, string vaultId = "")
    {
        var settings = LockerSettings.Load();
        if (!settings.PhoneBiometricsEnabled || settings.PairedPhone == null)
            return false;

        string secret = settings.PairedPhone.SharedSecretKey;
        string phoneIp = settings.PairedPhone.LastIpAddress;
        if (string.IsNullOrWhiteSpace(phoneIp) || string.IsNullOrEmpty(secret))
            return false;
        if (IPAddress.TryParse(phoneIp, out var targetIp) && !PassesSameWiFiPolicy(targetIp))
            return false;

        string actionType = isLock ? "lock" : "unlock";
        string mode = settings.PreferredBiometric ?? "Any"; // Any, Fingerprint, Face
        string nonce = PhoneAuth.BeginRequest(target, actionType);

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);

            // Connect to companion app on phone (Phone app listens on port 58821)
            await client.ConnectAsync(phoneIp, PhonePort, cts.Token);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
            using var reader = new StreamReader(stream, Utf8NoBom);

            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            LastVaultToken = "";
            var req = new
            {
                action = "auth_request",
                action_type = actionType,
                target = target,
                vaultId = vaultId,
                mode = mode,
                timestamp = ts,
                nonce = nonce,
                mac = PhoneAuth.Sign(secret, "auth_request", actionType, target, mode, ts, nonce)
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(req));
            string? responseLine = await ReadLineBoundedAsync(reader, MaxLineChars, cts.Token);

            if (!string.IsNullOrEmpty(responseLine))
            {
                using var doc = JsonDocument.Parse(responseLine);
                var root = doc.RootElement;
                string status = Str(root, "status", "", 16);
                string rawType = Str(root, "type", "fingerprint", 32);
                string mac = Str(root, "mac", "", 128);

                // The reply must prove knowledge of the secret AND be bound to OUR nonce
                if (status == "approved"
                    && PhoneAuth.Verify(secret, mac, "auth_reply", status, rawType, actionType, target, nonce))
                {
                    if (root.TryGetProperty("vault_token", out var vtElem)) LastVaultToken = vtElem.GetString() ?? "";
                    if (PhoneAuth.TryConsume(nonce, target, actionType))
                        RaiseApproved(target, CleanAuthType(rawType), actionType);
                    return true;
                }
            }
        }
        catch
        {
            // Phone reachable na hole fallback to password
        }
        finally
        {
            PhoneAuth.Cancel(nonce);
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Fingerprint-bound vault (v2)
    //   enroll : phone checks the fingerprint, creates a Keystore key (only the fingers enrolled right now can use
    //            it; adding a finger later destroys it) and sends back the public key. The PC wraps a random U with
    //            it and stores only RSA(U) + AES(master, U).
    //   unlock : PC sends RSA(U); the phone can open it only with a live fingerprint, returns U (encrypted with the
    //            pairing secret); PC opens the master key and forgets U.
    // ---------------------------------------------------------------------------------------

    public sealed class PhoneReply
    {
        public bool Approved;
        public string Reason = "";
        public string AuthType = "fingerprint";
        public byte[]? U;
        public byte[]? PubSpki;
    }

    public sealed class VaultKeyResult
    {
        public byte[]? Key;
        public string Reason = "";
        public string AuthType = "fingerprint";
    }

    // true after the phone said "key_invalid / no_key": next successful password entry links the fingerprint again
    public static bool ReenrollHint { get; private set; }
    private static int _enrollBusy;
    private static DateTime _lastAutoEnrollUtc = DateTime.MinValue;

    private static byte[]? DecryptU(string secret, string nonce, string b64)
    {
        try
        {
            byte[] all = Convert.FromBase64String(b64);
            if (all.Length < 12 + 16 + 1) return null;
            byte[] tk = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("u-key\n" + nonce));
            byte[] iv = all[..12];
            byte[] tag = all[^16..];
            byte[] ct = all[12..^16];
            byte[] plain = new byte[ct.Length];
            using var aes = new AesGcm(tk, 16);
            aes.Decrypt(iv, ct, tag, plain, Encoding.UTF8.GetBytes(nonce));
            return plain;
        }
        catch { return null; }
    }

    private static async Task<PhoneReply> SendPhoneRequestAsync(string target, string actionType, string wrappedB64, int timeoutMs)
    {
        var reply = new PhoneReply();
        var settings = LockerSettings.Load();
        if (!settings.PhoneBiometricsEnabled || settings.PairedPhone == null) { reply.Reason = "not_paired"; return reply; }

        string secret = settings.PairedPhone.SharedSecretKey;
        string phoneIp = settings.PairedPhone.LastIpAddress;
        if (string.IsNullOrWhiteSpace(phoneIp) || string.IsNullOrEmpty(secret)) { reply.Reason = "no_phone"; return reply; }
        if (IPAddress.TryParse(phoneIp, out var targetIp) && !PassesSameWiFiPolicy(targetIp)) { reply.Reason = "wrong_wifi"; return reply; }

        string mode = settings.PreferredBiometric ?? "Any";
        string nonce = PhoneAuth.BeginRequest(target, actionType);

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(phoneIp, PhonePort, cts.Token);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
            using var reader = new StreamReader(stream, Utf8NoBom);

            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string macReq = wrappedB64.Length == 0
                ? PhoneAuth.Sign(secret, "auth_request", actionType, target, mode, ts, nonce)
                : PhoneAuth.Sign(secret, "auth_request", actionType, target, mode, ts, nonce, wrappedB64);

            var req = new
            {
                action = "auth_request",
                action_type = actionType,
                target = target,
                vaultId = "",
                mode = mode,
                timestamp = ts,
                nonce = nonce,
                wrapped = wrappedB64,
                mac = macReq
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(req));

            string? line = await ReadLineBoundedAsync(reader, MaxLineChars, cts.Token);
            if (string.IsNullOrEmpty(line)) { reply.Reason = "no_reply"; return reply; }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string status = Str(root, "status", "", 16);
            string rawType = Str(root, "type", "fingerprint", 32);
            string mac = Str(root, "mac", "", 128);

            if (status != "approved")
            {
                reply.Reason = Str(root, "reason", "rejected", 32); // hint only, not trusted for anything but UI text
                return reply;
            }
            if (!PhoneAuth.Verify(secret, mac, "auth_reply", status, rawType, actionType, target, nonce))
            {
                reply.Reason = "bad_mac";
                return reply;
            }

            string pub = Str(root, "pub", "", 1024);
            string u = Str(root, "u", "", 256);
            string dmac = Str(root, "dmac", "", 128);
            if (pub.Length > 0 || u.Length > 0)
            {
                if (!PhoneAuth.Verify(secret, dmac, "auth_data", actionType, nonce, pub, u))
                {
                    reply.Reason = "bad_data_mac";
                    return reply;
                }
                if (u.Length > 0) reply.U = DecryptU(secret, nonce, u);
                if (pub.Length > 0) reply.PubSpki = Convert.FromBase64String(pub);
            }

            reply.Approved = true;
            reply.AuthType = CleanAuthType(rawType);
            if (PhoneAuth.TryConsume(nonce, target, actionType))
                RaiseApproved(target, reply.AuthType, actionType);
            return reply;
        }
        catch
        {
            reply.Reason = "unreachable";
            return reply;
        }
        finally
        {
            PhoneAuth.Cancel(nonce);
        }
    }

    // Fingerprint on the phone -> master key. Null Key = fall back to the password.
    public static async Task<VaultKeyResult> RequestVaultKeyAsync(string target, bool isLock, int timeoutMs = 15000)
    {
        var res = new VaultKeyResult();
        var vault = MasterConfig.LoadBiometricVaultV2();
        var paired = LockerSettings.Load().PairedPhone;
        if (vault == null || paired == null || (vault.DeviceId.Length > 0 && vault.DeviceId != paired.DeviceId))
        {
            res.Reason = "no_vault";
            return res;
        }

        var r = await SendPhoneRequestAsync(target, isLock ? "lock" : "unlock", vault.Wrapped, timeoutMs);
        res.Reason = r.Reason;
        res.AuthType = r.AuthType;

        if (!r.Approved)
        {
            if (r.Reason is "key_invalid" or "no_key") ReenrollHint = true;
            return res;
        }
        if (r.U == null) { res.Reason = "no_key_data"; return res; }

        try { res.Key = MasterConfig.OpenBiometricVaultV2(r.U); }
        finally { CryptographicOperations.ZeroMemory(r.U); }

        if (res.Key == null) { res.Reason = "vault_mismatch"; ReenrollHint = true; }
        return res;
    }

    // Link the phone's current fingerprints to this PC. Needs the master key (so: right after a password).
    public static async Task<(bool Ok, string Reason)> EnrollBiometricAsync(byte[] masterKey, int startDelayMs = 0)
    {
        if (Interlocked.Exchange(ref _enrollBusy, 1) == 1) return (false, "busy");
        try
        {
            if (startDelayMs > 0) await Task.Delay(startDelayMs);
            var paired = LockerSettings.Load().PairedPhone;
            if (paired == null) return (false, "not_paired");

            var r = await SendPhoneRequestAsync("Locker", "enroll", "", 45000);
            if (!r.Approved) return (false, r.Reason);
            if (r.PubSpki == null) return (false, "no_pub");

            MasterConfig.SaveBiometricVaultV2(masterKey, r.PubSpki, paired.DeviceId);
            ReenrollHint = false;
            return (true, "");
        }
        catch
        {
            return (false, "error");
        }
        finally
        {
            Interlocked.Exchange(ref _enrollBusy, 0);
        }
    }

    // Call with a freshly verified master key. Links the fingerprint in the background when it is missing
    // (first time after update, or after the phone's fingerprints changed). Throttled: at most once / 5 min.
    public static void TryAutoEnroll(byte[]? masterKey)
    {
        try
        {
            if (masterKey == null || masterKey.Length == 0) return;
            var s = LockerSettings.Load();
            if (s.PairedPhone == null || !s.PhoneBiometricsEnabled) return;

            var v = MasterConfig.LoadBiometricVaultV2();
            bool need = v == null || ReenrollHint || (v.DeviceId.Length > 0 && v.DeviceId != s.PairedPhone.DeviceId);
            if (!need) return;
            if (DateTime.UtcNow - _lastAutoEnrollUtc < TimeSpan.FromMinutes(5)) return;
            _lastAutoEnrollUtc = DateTime.UtcNow;

            byte[] copy = (byte[])masterKey.Clone();
            _ = Task.Run(async () =>
            {
                try { await EnrollBiometricAsync(copy); }
                finally { CryptographicOperations.ZeroMemory(copy); }
            });
        }
        catch { }
    }

    public static async Task<bool> RequestBiometricUnlockAsync(string target, int timeoutMs = 12000)
    {
        if (MasterConfig.HasBiometricVaultV2())
        {
            var r = await RequestVaultKeyAsync(target, false, timeoutMs);
            if (r.Key == null) return false;
            CryptographicOperations.ZeroMemory(r.Key);
            return true;
        }
        return await RequestBiometricAuthAsync(target, isLock: false, timeoutMs);
    }
}
