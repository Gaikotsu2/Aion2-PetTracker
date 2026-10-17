using System.Security.Cryptography;
using System.Text;

namespace PetTracker.Core.Startup;

/// <summary>
/// One tracker per data folder: a named Mutex says who is first, two named EventWaitHandles let a later launch tell the first
/// to show its collection window or to quit. Names are per Windows session. The key comes from the data folder so a test run with
/// --data never talks to the real tracker.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _quit;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;

    public SingleInstanceGuard(string key)
    {
        _mutex = new Mutex(false, $@"Local\Aion2PetTracker.{key}.Instance", out var createdNew);
        IsFirst = createdNew;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Aion2PetTracker.{key}.Show");
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Aion2PetTracker.{key}.Quit");
    }

    public bool IsFirst { get; }

    /// <summary>A short stable key for a data folder.</summary>
    public static string KeyFor(string dataFolder)
    {
        var full = Path.GetFullPath(dataFolder).TrimEnd('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    public void SignalShow() => _show.Set();

    public void SignalQuit() => _quit.Set();

    /// <summary>Runs the callbacks on a background thread when a later launch signals; marshal to the UI thread yourself.</summary>
    public void Listen(Action onShow, Action onQuit)
    {
        _listener = new Thread(() =>
        {
            WaitHandle[] handles = [_show, _quit, _stop.Token.WaitHandle];
            while (true)
            {
                var index = WaitHandle.WaitAny(handles);
                if (index == 0) onShow();
                else if (index == 1) onQuit();
                else return;
            }
        })
        { IsBackground = true, Name = "single-instance listener" };
        _listener.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));
        _show.Dispose();
        _quit.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
