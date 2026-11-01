using System.Text.Json;

namespace AppLocker.Core;

public class LockoutState
{
    public int Fails { get; set; }                 // total bhul count (thik password dile 0)
    public long LockedUntilUtcTicks { get; set; }
    public long LockedAtUtcTicks { get; set; }     // clock pichone ghurale dhora porbe
    public int LockSeconds { get; set; }
}

// 1ta, 2ta bhul: kichu na. 3 no. bhul -> 30s wait.
// Wait shesh hoyar por abar bhul dile EKDOM notun, boro wait: 1m, 5m, 15m, 1h, 3h, 12h, 24h (24h e thambe).
// Thik password dile shob reset. Disk e save hoy, app restart korleo jay na.
// channel: alada counter (pipe "quit" er jonno), jate bairer kono process UI er login lockout bhore dite na pare.
public static class LockoutTracker
{
    public const int FreeTries = 2;
    private static readonly int[] Seconds = { 30, 60, 300, 900, 3600, 10800, 43200, 86400 };
    private static readonly object Gate = new();

    private static string StatePath(string channel) =>
        Path.Combine(MasterConfig.ConfigDir, channel == "main" ? "lockout.json" : $"lockout-{channel}.json");

    // Koto second baki. 0 = try kora jabe.
    public static int SecondsRemaining(string channel = "main")
    {
        lock (Gate)
        {
            var s = Load(channel);
            if (s.LockSeconds <= 0 || s.LockedUntilUtcTicks <= 0) return 0;

            var now = DateTime.UtcNow;
            var at = new DateTime(Math.Clamp(s.LockedAtUtcTicks, 0L, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
            if (now < at) return s.LockSeconds; // PC er ghori pichone ghurano hoyeche: puro wait dhore nao

            var left = new DateTime(Math.Clamp(s.LockedUntilUtcTicks, 0L, DateTime.MaxValue.Ticks), DateTimeKind.Utc) - now;
            return left.TotalSeconds > 0 ? (int)Math.Ceiling(left.TotalSeconds) : 0;
        }
    }

    public static void RecordFail(string channel = "main")
    {
        lock (Gate)
        {
            var s = Load(channel);
            s.Fails++;
            if (s.Fails > FreeTries)
            {
                int step = Math.Min(s.Fails - FreeTries - 1, Seconds.Length - 1);
                var now = DateTime.UtcNow;
                s.LockSeconds = Seconds[step];
                s.LockedAtUtcTicks = now.Ticks;
                s.LockedUntilUtcTicks = now.AddSeconds(s.LockSeconds).Ticks;
            }
            Save(s, channel);
        }
    }

    public static void RecordSuccess(string channel = "main")
    {
        lock (Gate) Save(new LockoutState(), channel);
    }

    // "90" -> "1m 30s", "3600" -> "1h 0m"
    public static string Format(int seconds)
    {
        if (seconds < 60) return seconds + "s";
        if (seconds < 3600) return (seconds / 60) + "m " + (seconds % 60) + "s";
        return (seconds / 3600) + "h " + ((seconds % 3600) / 60) + "m";
    }

    private static LockoutState Load(string channel)
    {
        try
        {
            string p = StatePath(channel);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<LockoutState>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        return new();
    }

    private static void Save(LockoutState s, string channel)
    {
        try
        {
            Directory.CreateDirectory(MasterConfig.ConfigDir);
            File.WriteAllText(StatePath(channel), JsonSerializer.Serialize(s));
        }
        catch { }
    }
}
