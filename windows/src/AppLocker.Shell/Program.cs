using AppLocker.Core;
using System.Security.Cryptography;

// Use:
//   AppLocker.Shell.exe lock "C:\folder"
//   AppLocker.Shell.exe unlock "C:\folder"      (permanent lock shoriye dey)
//   AppLocker.Shell.exe open "C:\folder"        (Guard ke bole popup dekhate)
//   AppLocker.Shell.exe lockapp "C:\x\app.exe"  (app lock)
//   AppLocker.Shell.exe unlockapp "C:\x\app.exe"(app lock shoriye dey)
//   AppLocker.Shell.exe quit                    (Guard bondho, shob folder lock kore)
//   AppLocker.Shell.exe changepass
// Test er jonno: --config C:\Dev\AppLockerTest\xxx  (je kono jaygay dite paro)

// --config handle
var argList = args.ToList();
int ci = argList.IndexOf("--config");
if (ci >= 0 && ci + 1 < argList.Count)
{
    MasterConfig.ConfigDir = argList[ci + 1].Trim('"');
    argList.RemoveRange(ci, 2);
}
args = argList.ToArray();

if (args.Length < 1)
{
    Console.WriteLine("Use: AppLocker.Shell lock|unlock|open|lockapp|unlockapp <path>  ba  quit  ba  changepass  ba  uninstall");
    Console.ReadLine();
    return;
}

string action = args[0].ToLowerInvariant();
string folder = args.Length > 1 ? args[1].Trim('"') : "";

try
{
    if (action == "lock")
    {
        if (!Directory.Exists(folder)) { Console.WriteLine("Eta folder na."); }
        else if (FolderLocker.IsLocked(folder)) { Console.WriteLine("Already locked."); }
        else
        {
            byte[]? master = EnsureSetupAndGetKey();
            if (master != null)
            {
                Console.WriteLine("Locking: " + folder);
                FolderLocker.Lock(folder, master);
                CryptographicOperations.ZeroMemory(master);
                Console.WriteLine("LOCKED!");

                // Guard chalu thakle bole dei, jate rename watch shuru kore
                GuardPipe.Send("watch|" + Path.GetFullPath(folder), 500);
            }
        }
    }
    else if (action == "unlock")
    {
        if (!MasterConfig.IsSetup()) Console.WriteLine("AppLocker setup hoyni.");
        else if (!FolderLocker.IsLocked(folder)) Console.WriteLine("Folder locked na.");
        else
        {
            byte[]? master = AskMasterKey();
            if (master == null) Console.WriteLine("Bhul password/recovery key.");
            else
            {
                bool ok = FolderLocker.Unlock(folder, master);
                CryptographicOperations.ZeroMemory(master);
                Console.WriteLine(ok ? "UNLOCKED (lock shoriye deya hoyeche)." : "Ei folder er key mile nai.");
            }
        }
    }
    else if (action == "open")
    {
        string? reply = GuardPipe.Send("open|" + Path.GetFullPath(folder));
        Console.WriteLine(reply switch
        {
            null => "Guard chalu nai. Age Guard chalao.",
            "accepted" => "Guard ke bola hoyeche. Popup dekho.",
            "busy" => "Guard byasto, ektu por try koro.",
            "not-locked" => "Eta AppLocker er locked folder na.",
            "already-open" => "Folder already open ache.",
            "not-setup" => "AppLocker setup hoyni (age ekta folder lock koro).",
            _ => "Guard reply: " + reply
        });
    }
    else if (action == "lockapp")
    {
        // Shortcut (.lnk) e right-click korle tar asol .exe ber kori
        if (folder.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            folder = ResolveShortcut(folder);

        if (!File.Exists(folder) || !folder.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("Eta .exe file na ba pawa jayni.");
        else
        {
            byte[]? master = EnsureSetupAndGetKey();
            if (master == null) Console.WriteLine("Bhul password.");
            else
            {
                AppRegistry.Add(folder);
                CryptographicOperations.ZeroMemory(master);
                Console.WriteLine("APP LOCKED: " + Path.GetFullPath(folder));
                Console.WriteLine("Guard chalu thakle ekhon theke ei app khulte password lagbe.");
            }
        }
    }
    else if (action == "unlockapp")
    {
        if (!MasterConfig.IsSetup()) Console.WriteLine("AppLocker setup hoyni.");
        else
        {
            byte[]? master = AskMasterKey();
            if (master == null) Console.WriteLine("Bhul password/recovery key.");
            else
            {
                AppRegistry.Remove(folder);
                CryptographicOperations.ZeroMemory(master);
                Console.WriteLine("APP LOCK shoriye deya hoyeche.");
            }
        }
    }
    else if (action == "quit")
    {
        Console.Write("Master password dao: ");
        string qp = ReadPassword();
        string? reply = GuardPipe.Send("quit|" + qp);
        Console.WriteLine(reply == null ? "Guard chalu nai."
            : reply == "bye" ? "Guard bondho hochhe (open folder gulo lock hobe)."
            : "Bhul password.");
    }
    else if (action == "uninstall")
    {
        Console.WriteLine("=== UNINSTALL LOCKER ===");
        Console.WriteLine("Locker uninstall korle shob locked folder auto-decrypt o unlock hobe,");
        Console.WriteLine("shob app lock shorano hobe, ar context menu remove hoye jabe.");
        Console.Write("Proceed korte Master Password (ba Recovery Key) dao: ");
        string pw = ReadPassword();
        var (ok, msg) = Uninstaller.FullUninstall(pw);
        Console.WriteLine(msg);
    }
    else if (action == "changepass")
    {
        if (!MasterConfig.IsSetup()) Console.WriteLine("AppLocker setup hoyni.");
        else
        {
            Console.Write("Purono password: ");
            string oldP = ReadPassword();
            Console.Write("Notun password: ");
            string n1 = ReadPassword();
            Console.Write("Abar notun password: ");
            string n2 = ReadPassword();
            if (n1 != n2 || n1.Length < 4) Console.WriteLine("Mile nai ba khub chhoto (min 4).");
            else Console.WriteLine(MasterConfig.ChangePassword(oldP, n1)
                ? "Password change hoyeche (shob folder er jonno)."
                : "Purono password bhul.");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine("Error: " + ex.Message);
}

Console.WriteLine();
Console.WriteLine("Enter chapo bondho korte...");
Console.ReadLine();

// Prothom bar hole setup kore, tarpor master key return kore
static byte[]? EnsureSetupAndGetKey()
{
    if (!MasterConfig.IsSetup())
    {
        Console.WriteLine("=== Prothom bar setup ===");
        Console.Write("Master password dao: ");
        string p1 = ReadPassword();
        Console.Write("Abar dao: ");
        string p2 = ReadPassword();
        if (p1 != p2 || p1.Length < 4)
        {
            Console.WriteLine("Mile nai ba khub chhoto (min 4).");
            return null;
        }
        string recovery = MasterConfig.Setup(p1);
        Console.WriteLine();
        Console.WriteLine("RECOVERY KEY (EKBAR-I dekhabe, likhe rakho):");
        Console.WriteLine("  " + recovery);
        Console.WriteLine("Eta hariye gele + password bhule gele shob data gelo!");
        Console.Write("Likhe rekhechi bole 'OK' likho: ");
        while ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "OK")
            Console.Write("'OK' likho: ");
        return MasterConfig.GetKeyWithPassword(p1);
    }

    Console.Write("Master password dao: ");
    return MasterConfig.GetKeyWithPassword(ReadPassword());
}

static byte[]? AskMasterKey()
{
    Console.Write("Password dao (recovery key dite shudhu Enter chapo): ");
    string p = ReadPassword();
    if (p.Length > 0) return MasterConfig.GetKeyWithPassword(p);
    Console.Write("Recovery key dao: ");
    return MasterConfig.GetKeyWithRecovery(Console.ReadLine() ?? "");
}

static string ReadPassword()
{
    var sb = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace && sb.Length > 0) { sb.Length--; continue; }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }
    return sb.ToString();
}

// .lnk shortcut er target (.exe) return kore. Na pele ager path-i.
static string ResolveShortcut(string lnk)
{
    try
    {
        Type? t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return lnk;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic sc = shell.CreateShortcut(lnk);
        string target = sc.TargetPath;
        return string.IsNullOrEmpty(target) ? lnk : target;
    }
    catch { return lnk; }
}
