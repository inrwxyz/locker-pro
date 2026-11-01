using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AppLocker.Core;

public static class InexIslandBridge
{
    public const string PipeName = "InexIslandLock";

    private const int MaxQueued = 64;
    private const int ConnectTimeoutMs = 300;
    private const int Attempts = 3;

    // Events are queued and delivered by ONE worker, strictly in order. The old version did a
    // fresh Task.Run per event, so "prompt" and "unlock" fired back to back could arrive
    // reversed, and a busy pipe silently dropped the event.
    private static readonly ConcurrentQueue<string> Queue = new();
    private static int _draining;
    private static long _seq;

    public static string GetInstalledPath()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApp, "Inex Island", "inex-island.exe");
    }

    public static bool IsInstalled()
    {
        try { return File.Exists(GetInstalledPath()); }
        catch { return false; }
    }

    public static bool IsRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("inex-island");
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose(); // old code leaked a handle per call
            return any;
        }
        catch { return false; }
    }

    private static void SendEvent(string eventName, string type, string target, bool heartbeat = false)
    {
        try
        {
            // ONE place for the Locker Pro toggle ("Inex Island Dynamic Lock / Animations"): every caller
            // (Manager, Guard, popup, phone service) goes through here, so the switch now works everywhere.
            // "cancel" is always allowed so the island can never get stuck on a prompt after the switch is turned off.
            if (eventName != "locker_cancel" && !IslandEnabled())
            {
                Log($"SKIP {eventName} '{target}' (toggle OFF)");
                return;
            }
            Log($"QUEUE {eventName} '{target}' hb={heartbeat}");

            if (target != null && target.Length > 128) target = target[..128];
            var payload = new
            {
                @event = eventName,
                type = type, // "fingerprint", "face", "password", "locked", "cancel"
                target = target,
                hb = heartbeat, // true = keep-alive of an already open prompt
                seq = System.Threading.Interlocked.Increment(ref _seq),
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), // ms: island drops stale/out-of-order events
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() // kept for older island builds
            };
            Enqueue(JsonSerializer.Serialize(payload));
        }
        catch { /* the locker must never fail because the island is unreachable */ }
    }

    // Diagnostic log: %TEMP%\inex-island-bridge.log  (first line shows the build marker, so you can tell which build is running)
    private const string BuildMarker = "bridge-v3-2026-10-28";
    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "inex-island-bridge.log");
    private static readonly object LogGate = new();
    private static bool _logStarted;

    private static void Log(string text)
    {
        try
        {
            lock (LogGate)
            {
                if (!_logStarted)
                {
                    _logStarted = true;
                    try { if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 200_000) File.Delete(LogFile); } catch { }
                    File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} --- {BuildMarker} pid={Environment.ProcessId} ---{Environment.NewLine}");
                }
                File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static bool IslandEnabled()
    {
        try { return LockerSettings.Current.InexIslandAnimation; }
        catch { return true; }
    }

    private static void Enqueue(string json)
    {
        while (Queue.Count >= MaxQueued) Queue.TryDequeue(out _);
        Queue.Enqueue(json);
        StartDrain();
    }

    private static void StartDrain()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        // Foreground thread on purpose: a short-lived locker process (popup / shell helper) must not
        // exit before the last event is delivered. It ends as soon as the queue is empty.
        var t = new Thread(Drain) { IsBackground = false, Name = "InexIslandBridge" };
        t.Start();
    }

    private static void Drain()
    {
        try
        {
            while (true)
            {
                while (Queue.TryDequeue(out var json)) Deliver(json);

                System.Threading.Volatile.Write(ref _draining, 0);
                // an event may have been enqueued between the last dequeue and the line above
                if (Queue.IsEmpty) break;
                if (System.Threading.Interlocked.CompareExchange(ref _draining, 1, 0) != 0) break;
            }
        }
        catch
        {
            System.Threading.Volatile.Write(ref _draining, 0);
        }
    }

    private static void Deliver(string json)
    {
        if (!IsRunning()) { Log("DROP island not running"); return; } // island not running: nothing to animate

        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(ConnectTimeoutMs);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                Log("SENT ok");
                return;
            }
            catch (UnauthorizedAccessException) { return; } // pipe owned by someone else - do not retry
            catch (TimeoutException) { }                      // island busy / restarting
            catch (IOException) { }
            Thread.Sleep(80);
        }
        Log("FAILED all attempts (pipe busy / not found)");
    }

    // ---- prompt heartbeat ------------------------------------------------------------------
    // The island used to hide the lock status after a fixed 12 s even though the popup was still open.
    // Now the locker re-sends the prompt every 3 s for exactly as long as the popup lives, and the island
    // keeps the status while heartbeats keep coming (its lease is 8 s, so a crashed locker cannot leave
    // the status stuck). Unlock / lock / cancel stop the heartbeat first, so no stale keep-alive can
    // re-open the status after the real end event.
    private const int HeartbeatMs = 3000;
    private static System.Threading.Timer? _heartbeat;
    private static int _heartbeatGen;
    private static readonly object HeartbeatGate = new();

    private static void StartHeartbeat(string target)
    {
        lock (HeartbeatGate)
        {
            _heartbeat?.Dispose();
            int gen = System.Threading.Interlocked.Increment(ref _heartbeatGen);
            _heartbeat = new System.Threading.Timer(_ =>
            {
                lock (HeartbeatGate)
                {
                    if (gen != _heartbeatGen) return; // stopped in the meantime
                    SendEvent("locker_prompt", "locked", target, heartbeat: true);
                }
            }, null, HeartbeatMs, HeartbeatMs);
        }
    }

    private static void StopHeartbeat()
    {
        lock (HeartbeatGate)
        {
            System.Threading.Interlocked.Increment(ref _heartbeatGen);
            _heartbeat?.Dispose();
            _heartbeat = null;
        }
    }

    // Call these from the locker:
    public static void NotifyPrompt(string target)
    {
        StopHeartbeat();
        SendEvent("locker_prompt", "locked", target);
        StartHeartbeat(target); // popup is open -> keep the island status alive until it closes
    }

    public static void NotifyWrong(string target) => SendEvent("locker_wrong", "locked", target); // popup still open: heartbeat continues

    public static void NotifyUnlock(string type, string target)
    {
        StopHeartbeat();
        SendEvent("locker_unlock", type, target);
    }

    public static void NotifyLock(string target, string type = "lock")
    {
        StopHeartbeat();
        SendEvent("locker_lock", type, target);
    }

    // phone pair / unpair, folder or app removed (add already uses NotifyLock) -> island status (same pipe, same toggle)
    // each of these ends a running prompt, so the heartbeat is stopped first (no stale keep-alive afterwards)
    public static void NotifyPhonePaired(string deviceName) { StopHeartbeat(); SendEvent("locker_pair", "phone", deviceName); }
    public static void NotifyPhoneUnpaired(string deviceName = "") { StopHeartbeat(); SendEvent("locker_unpair", "phone", deviceName); }
    public static void NotifyRemoved(string target) { StopHeartbeat(); SendEvent("locker_remove", "unlock", target); }

    public static void NotifyCancel(string target = "")
    {
        StopHeartbeat();
        SendEvent("locker_cancel", "cancel", target);
    }
}
