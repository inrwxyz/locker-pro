using System.IO;
using System.IO.Pipes;
using System.Text;

namespace AppLocker.Core;

// Shell/Manager theke Guard ke command pathanor rasta (named pipe).
// Command format: "open|C:\folder", "watch|C:\folder", "quit"
public static class GuardPipe
{
    public const string Name = "AppLockerGuard";

    // Reply return kore. Guard chalu na thakle null.
    public static string? Send(string line, int timeoutMs = 2000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var c = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            c.Connect(timeoutMs);
            using var w = new StreamWriter(c, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            w.WriteLine(line);
            using var r = new StreamReader(c, Encoding.UTF8, leaveOpen: true);
            var readTask = r.ReadLineAsync(cts.Token).AsTask();
            if (readTask.Wait(timeoutMs))
            {
                return readTask.Result;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
