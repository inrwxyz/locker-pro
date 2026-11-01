using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AppLocker.Core;

// Nijer process e "Terminate" deny kore: Task Manager er End Task e "Access denied" dey.
// Limit: SeDebugPrivilege wala tool (Process Hacker, SYSTEM) ekhono marte pare, tai watchdog lage.
public static class ProcessProtector
{
    private const int DACL_SECURITY_INFORMATION = 0x4;
    private const int PROCESS_TERMINATE = 0x0001;
    private const int PROCESS_SUSPEND_RESUME = 0x0800;
    private const int WRITE_DAC = 0x00040000;
    private const int WRITE_OWNER = 0x00080000;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(IntPtr handle, int securityInformation,
        byte[]? pSecurityDescriptor, uint nLength, out uint lpnLengthNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(IntPtr handle, int securityInformation,
        byte[] pSecurityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static bool Protect()
    {
        try
        {
            IntPtr h = GetCurrentProcess();

            GetKernelObjectSecurity(h, DACL_SECURITY_INFORMATION, null, 0, out uint needed);
            if (needed == 0) return false;
            var buf = new byte[needed];
            if (!GetKernelObjectSecurity(h, DACL_SECURITY_INFORMATION, buf, needed, out _)) return false;

            var sd = new RawSecurityDescriptor(buf, 0);
            if (sd.DiscretionaryAcl == null) return false;

            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            int mask = PROCESS_TERMINATE | PROCESS_SUSPEND_RESUME | WRITE_DAC | WRITE_OWNER;
            sd.DiscretionaryAcl.InsertAce(0,
                new CommonAce(AceFlags.None, AceQualifier.AccessDenied, mask, everyone, false, null));

            var outBuf = new byte[sd.BinaryLength];
            sd.GetBinaryForm(outBuf, 0);
            return SetKernelObjectSecurity(h, DACL_SECURITY_INFORMATION, outBuf);
        }
        catch
        {
            return false;
        }
    }
}
