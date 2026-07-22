namespace QServer.Logging;

/// <summary>
/// Tiny append-only lifecycle log (panel start, sweep kills, host start/pid/ready/exit/fail — each UTC-stamped).
/// This is the evidence trail for "it sometimes fails" reports: it records WHAT the supervisor did, and is
/// deliberately SEPARATE from the filtered <see cref="FileSink"/> server log (whose noise rules can hide the very
/// lines that explain a failed start). Thread-safe and auto-flushed, so an event is on disk even if the panel is
/// killed the instant after it. A null/empty path disables it entirely — every method becomes a no-op — so the
/// call sites stay unconditional (<c>hostLog?.Log(...)</c> is only for the "no HostLog at all" case).
/// </summary>
public sealed class HostLog : IDisposable
{
    readonly StreamWriter? _w;
    readonly object _lock = new();
    bool _disposed;

    /// <summary>Open the log at <paramref name="path"/> in append mode; null/whitespace disables logging.</summary>
    public HostLog(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // FileShare.Read so a tail/editor can watch the log live; AutoFlush so events survive a hard kill.
        _w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    /// <summary>
    /// Append one UTC-stamped event line. No-op when disabled or already disposed, and never throws to the
    /// caller: a disk-full or handle-yanked-during-shutdown error must not take the panel down with it.
    /// </summary>
    public void Log(string evt)
    {
        lock (_lock)
        {
            if (_disposed || _w is null) return;
            try { _w.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z  {evt}"); }
            catch { /* lifecycle logging is best-effort; swallow I/O failures */ }
        }
    }

    /// <summary>Idempotent: the shutdown guard and the normal-exit path both dispose this and can race.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _w?.Dispose();
        }
    }
}
