namespace AppLocker.Core;

// Password check + lockout ek jaygay. Je jaygay password nei, shekhane eta use korle
// bhul password count hobe ebong lockout cholle password check-ii hobe na.
public static class AuthGate
{
    // Thik hole master key dey. Bhul hole null; waitSeconds > 0 mane ekhon lockout cholche.
    public static byte[]? TryPassword(string password, out int waitSeconds,
        bool allowRecovery = false, string channel = "main")
    {
        waitSeconds = LockoutTracker.SecondsRemaining(channel);
        if (waitSeconds > 0) return null;

        byte[]? key = null;
        try
        {
            key = MasterConfig.GetKeyWithPassword(password);
            if (key == null && allowRecovery) key = MasterConfig.GetKeyWithRecovery(password);
        }
        catch
        {
            key = null;
        }

        if (key == null)
        {
            LockoutTracker.RecordFail(channel);
            waitSeconds = LockoutTracker.SecondsRemaining(channel);
            return null;
        }

        LockoutTracker.RecordSuccess(channel);
        return key;
    }
}
