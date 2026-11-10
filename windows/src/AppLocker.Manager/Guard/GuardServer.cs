using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using AppLocker.Core;

namespace AppLocker.Manager.Guard;

public class GuardServer
{
    private const string PipeName = GuardPipe.Name;
    private readonly Func<string, string> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public GuardServer(Func<string, string> handler)
    {
        _handler = handler;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "GuardPipeServer" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(200);
        }
        catch { }
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var ps = new PipeSecurity();
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Deny));
                ps.AddAccessRule(new PipeAccessRule(system,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(admins,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0, 0,
                    ps);

                server.WaitForConnection();
                if (_cts.IsCancellationRequested) { server.Dispose(); break; }

                var cur = server;
                ThreadPool.QueueUserWorkItem(_ => Serve(cur));
            }
            catch
            {
                server?.Dispose();
                Thread.Sleep(300);
            }
        }
    }

    private void Serve(NamedPipeServerStream s)
    {
        try
        {
            using (s)
            using (var reader = new StreamReader(s, Encoding.UTF8))
            using (var writer = new StreamWriter(s, Encoding.UTF8) { AutoFlush = true })
            {
                string? line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return;

                string reply;
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    // InvokeAsync + wait with timeout so pipe never blocks forever
                    var task = app.Dispatcher.InvokeAsync(() => _handler(line.Trim()));
                    if (!task.Task.Wait(TimeSpan.FromSeconds(5)))
                    {
                        reply = "timeout";
                    }
                    else
                    {
                        reply = task.Result;
                    }
                }
                else
                {
                    reply = _handler(line.Trim());
                }

                writer.WriteLine(reply);
            }
        }
        catch
        {
            // Suppress broken pipe, client disconnect, and I/O errors completely so thread never aborts process
        }
    }
}
