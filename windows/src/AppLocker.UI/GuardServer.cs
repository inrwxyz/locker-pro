using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using AppLocker.Core;

namespace AppLocker.UI;

// Pipe e command shuney GuardHost ke dey.
// Pipe e normal (non-admin) user o likhte pare, jate right-click launcher UAC chhara kaj kore.
// Shob gurutopurno kaj (lock/unlock/quit) e password lage.
public class GuardServer
{
    private readonly GuardHost _host;

    public GuardServer(GuardHost host) => _host = host;

    public void Start() => _ = Task.Run(Loop);

    private static PipeSecurity MakeSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var me = WindowsIdentity.GetCurrent().User;
        if (me != null)
            ps.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    private async Task Loop()
    {
        var security = MakeSecurity();
        while (true)
        {
            try
            {
                using var s = NamedPipeServerStreamAcl.Create(GuardPipe.Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await s.WaitForConnectionAsync();

                using var r = new StreamReader(s);
                using var w = new StreamWriter(s) { AutoFlush = true };
                string line = await r.ReadLineAsync() ?? "";

                string reply = await Application.Current.Dispatcher
                    .InvokeAsync(() => _host.Handle(line)).Task;
                await w.WriteLineAsync(reply);
            }
            catch
            {
                await Task.Delay(200);
            }
        }
    }
}
