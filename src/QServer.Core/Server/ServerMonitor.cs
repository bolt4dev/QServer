using System.Diagnostics;

namespace QServer.Server;

/// <summary>High-level state of the supervised server process.</summary>
public enum ServerState { Stopped, Starting, Ready, Stopping, Crashed }

/// <summary>
/// Tracks the server process (launched by the scraper) by pid and samples its CPU% / working set. The app uses
/// this only for the status bar; it does not own the process lifecycle (the scraper does).
/// </summary>
public sealed class ServerMonitor
{
    Process? _p;
    DateTime _lastT = DateTime.MinValue;
    TimeSpan _lastCpu = TimeSpan.Zero;

    public int Pid { get; private set; }
    public ServerState State { get; set; } = ServerState.Stopped;

    public void Attach(int pid)
    {
        Pid = pid;
        _p?.Dispose();
        _lastT = DateTime.MinValue; _lastCpu = TimeSpan.Zero; // reset baseline so the first sample isn't garbage
        try { _p = Process.GetProcessById(pid); State = ServerState.Starting; } catch { _p = null; }
    }

    public bool HasExited { get { try { return _p?.HasExited ?? true; } catch { return true; } } }

    public (double cpuPct, double ramMb) Sample()
    {
        if (_p is null) return (0, 0);
        try
        {
            _p.Refresh();
            var now = DateTime.UtcNow;
            var cpu = _p.TotalProcessorTime;
            double pct = 0;
            if (_lastT != DateTime.MinValue)
            {
                double wall = (now - _lastT).TotalMilliseconds;
                if (wall > 0) pct = (cpu - _lastCpu).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100.0;
            }
            _lastT = now; _lastCpu = cpu;
            return (Math.Clamp(pct, 0, 100), _p.WorkingSet64 / (1024.0 * 1024.0));
        }
        catch { return (0, 0); }
    }
}
