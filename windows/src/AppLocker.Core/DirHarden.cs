using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AppLocker.Core;

// %ProgramData%\AppLocker holds settings.json (phone shared secret), biometric.vault and the
// pairing PIN file. By default ProgramData lets every local user read what is created there.
// This limits the folder to SYSTEM + Administrators. Every Locker process already runs elevated
// (requireAdministrator / schtasks "highest" / SYSTEM boot task), so nothing legitimate loses access.
public static class DirHarden
{
    private static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void RestrictToAdminsAndSystem(string dir)
    {
        try
        {
            lock (Gate)
            {
                if (!Done.Add(dir)) return; // once per process
            }

            Directory.CreateDirectory(dir);
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

            // stop inheriting ProgramData's "Users can read" rules
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // drop any explicit rule for somebody else
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)r.IdentityReference;
                if (!sid.Equals(system) && !sid.Equals(admins))
                    sec.RemoveAccessRuleSpecific(r);
            }

            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit,
                PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit,
                PropagationFlags.None, AccessControlType.Allow));

            di.SetAccessControl(sec); // Windows propagates this to existing children
        }
        catch
        {
            // Never block the locker because ACL hardening failed (e.g. test config dir).
        }
    }

    public const string WatchdogStopEventName = "AppLockerWatchdogStop";

    /// <summary>
    /// Creates or opens the watchdog stop event restricted to SYSTEM and Administrators so
    /// unprivileged processes cannot silence LockerGuard.exe.
    /// </summary>
    public static EventWaitHandle OpenOrCreateWatchdogStopEvent()
    {
        try
        {
            var sec = new EventWaitHandleSecurity();
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            sec.AddAccessRule(new EventWaitHandleAccessRule(system, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new EventWaitHandleAccessRule(admins, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, WatchdogStopEventName, out _, sec);
        }
        catch
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, WatchdogStopEventName);
        }
    }

    public static void SignalWatchdogStop()
    {
        try
        {
            using var evt = OpenOrCreateWatchdogStopEvent();
            evt.Set();
        }
        catch { }
    }
}
