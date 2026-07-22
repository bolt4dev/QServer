using System.Runtime.InteropServices;

namespace QServer.Hosting;

/// <summary>
/// Runs registered cleanup exactly once when the process is asked to die: console window closed (X),
/// user logoff, OS shutdown, or normal CLR exit. Ctrl+C / Ctrl+Break instead raise <see cref="QuitRequested"/>
/// so the TUI can exit gracefully. The OS grants console apps ~5s on CTRL_CLOSE — enough to kill the child
/// tree and flush logs. The job object (KillOnCloseJob) remains the hard guarantee; this layer makes the
/// shutdown ORDERLY (logged, flushed) instead of relying on the kernel alone.
/// </summary>
public static class ShutdownGuard
{
    static readonly List<Action> Actions = new();
    static int _ran;
    static HandlerRoutine? _keepAlive;   // prevents GC of the native callback delegate

    /// <summary>Raised on Ctrl+C / Ctrl+Break (legacy input mode); wire this to the UI quit path.</summary>
    public static event Action? QuitRequested;

    /// <summary>
    /// Register a cleanup action and (on the first call) install the console control handler plus a
    /// <see cref="AppDomain.ProcessExit"/> hook so the actions still run on a normal CLR exit.
    /// </summary>
    public static void Register(Action cleanup)
    {
        lock (Actions) Actions.Add(cleanup);
        if (_keepAlive != null) return;
        _keepAlive = OnCtrl;
        // Under `dotnet test` (or any redirected / no-console host) there is no console control handler to
        // attach to, so SetConsoleCtrlHandler can return false. That is not fatal — cooperative cleanup via
        // ProcessExit still runs — so ignore the result and never let a missing console break startup.
        try { SetConsoleCtrlHandler(_keepAlive, add: true); } catch { }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RunOnce();
    }

    /// <summary>Runs every registered action once, swallowing exceptions; safe to call repeatedly (subsequent calls no-op).</summary>
    public static void RunOnce()
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0) return;
        Action[] acts;
        lock (Actions) acts = Actions.ToArray();
        foreach (var a in acts) { try { a(); } catch { } }
    }

    static bool OnCtrl(uint ctrlType)
    {
        if (ctrlType is CTRL_C_EVENT or CTRL_BREAK_EVENT) { QuitRequested?.Invoke(); return true; }
        RunOnce();          // CTRL_CLOSE / LOGOFF / SHUTDOWN: clean up inside the OS grace window,
        return false;       // then let the default handler terminate us.
    }

    delegate bool HandlerRoutine(uint ctrlType);
    const uint CTRL_C_EVENT = 0, CTRL_BREAK_EVENT = 1;
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);
}
