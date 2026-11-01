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
}
