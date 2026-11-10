namespace AppLocker.Core;

// System ba AppLocker er nijer folder lock korle PC/AppLocker nosto hobe
public static class LockSafety
{
    public static bool Inside(string child, string parent)
    {
        child = child.TrimEnd('\\');
        parent = parent.TrimEnd('\\');
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeFolder(string full, out string why)
    {
        why = "";
        if (full.Length <= 2) { why = "Drive root cannot be locked."; return false; }

        string[] protectedDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            MasterConfig.ConfigDir,
            AppContext.BaseDirectory
        };
        foreach (var p in protectedDirs)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (Inside(full, p) || Inside(p, full))
            {
                why = "System or Locker folders cannot be locked.";
                return false;
            }
        }
        return true;
    }

    // Vault (in-place) folder check. Less strict than IsSafeFolder: normal folders inside the user profile
    // (Desktop, Documents, pendrive folders...) are fine. Blocked: drive roots, Windows/Program Files,
    // the profile folder itself or its parents (C:\Users), and Locker's own install/config folders.
    public static bool IsSafeVaultFolder(string full, out string why)
    {
        why = "";
        full = Path.GetFullPath(full).TrimEnd('\\');

        string? root = Path.GetPathRoot(full)?.TrimEnd('\\');
        if (full.Length <= 2 || (root != null && full.Equals(root, StringComparison.OrdinalIgnoreCase)))
        { why = "Drive root cannot be locked. Choose a folder."; return false; }

        string[] systemDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            MasterConfig.ConfigDir,
            AppContext.BaseDirectory
        };
        foreach (var p in systemDirs)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (Inside(full, p) || Inside(p, full))
            {
                why = "System or Locker folders cannot be locked.";
                return false;
            }
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && Inside(profile, full)) // profile itself or a parent of it
        {
            why = "User profile folder (or its parent) cannot be locked. Choose a folder inside it.";
            return false;
        }
        return true;
    }
}
