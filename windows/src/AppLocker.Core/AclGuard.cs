using System.Security.AccessControl;
using System.Security.Principal;

namespace AppLocker.Core;

// Folder e deny rule set/remove kore. Admin permission lagbe.
public static class AclGuard
{
    private static readonly SecurityIdentifier Everyone =
        new(WellKnownSidType.WorldSid, null);

    private const FileSystemRights DenyRights =
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.CreateFiles |
        FileSystemRights.CreateDirectories |
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private static FileSystemAccessRule Rule() => new(
        Everyone, DenyRights,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None, AccessControlType.Deny);

    public static void Protect(string folder)
    {
        var di = new DirectoryInfo(folder);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(Rule());
        di.SetAccessControl(sec);
    }

    public static void Unprotect(string folder)
    {
        var di = new DirectoryInfo(folder);
        var sec = di.GetAccessControl();
        sec.RemoveAccessRuleAll(Rule());
        di.SetAccessControl(sec);
    }
}