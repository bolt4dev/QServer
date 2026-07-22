namespace QServer.Hosting;

/// <summary>
/// A bridge to a running dedicated-server console. An implementation launches the server, streams its console
/// output line by line, injects commands, and reports lifecycle events. Everything above this interface (the
/// noise pipeline, the UI, the watchdog) is platform-agnostic, so porting to another OS means writing one new
/// <see cref="IServerHost"/> - e.g. a Linux host that reads the server's stdout/pty directly.
/// </summary>
public interface IServerHost : IDisposable
{
    /// <summary>Raised for each captured console line.</summary>
    event Action<string>? LineReceived;

    /// <summary>Raised once with the server process id.</summary>
    event Action<int>? ServerPid;

    /// <summary>Raised when the server process exits, with its exit code.</summary>
    event Action<int>? ServerExited;

    /// <summary>Raised when capture has started successfully.</summary>
    event Action? Ready;

    /// <summary>Raised for host diagnostics / errors (not server console lines).</summary>
    event Action<string>? Diagnostic;

    /// <summary>
    /// Raised when the HOST itself failed rather than the server: the reason is human-readable and is
    /// fired before <see cref="ServerExited"/>, so the user learns WHY a start failed instead of only
    /// seeing a bare exit code.
    /// </summary>
    event Action<string>? HostFailed;

    /// <summary>Launch the server and begin capturing its console.</summary>
    void Start();

    /// <summary>Inject a console command into the server.</summary>
    void Send(string command);

    /// <summary>Request the server to stop.</summary>
    void Stop();
}
