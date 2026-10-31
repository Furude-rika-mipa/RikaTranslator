using System.Security.Cryptography;
using System.Text;

namespace GameTranslateToolkit.Updates;

// Cooperative shutdown scoped to this installation; never enumerates or kills other applications.
public static class McpLifetime
{
    private static EventWaitHandle Open(string directory)
    {
        var path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var name = "Local\\RikaMcpStop-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
        return new EventWaitHandle(false, EventResetMode.ManualReset, name);
    }
    public static EventWaitHandle AllowConnections(string directory) { var signal = Open(directory); signal.Reset(); return signal; }
    public static EventWaitHandle StopConnections(string directory) { var signal = Open(directory); signal.Set(); return signal; }
    public static IDisposable Watch(string directory)
    {
        var signal = Open(directory);
        if (signal.WaitOne(0)) Environment.Exit(0);
        return new Watcher(signal);
    }
    private sealed class Watcher : IDisposable
    {
        private readonly EventWaitHandle _signal;
        private readonly RegisteredWaitHandle _wait;
        public Watcher(EventWaitHandle signal)
        {
            _signal = signal;
            _wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Environment.Exit(0), null, Timeout.Infinite, true);
        }
        public void Dispose() { _wait.Unregister(null); _signal.Dispose(); }
    }
}
