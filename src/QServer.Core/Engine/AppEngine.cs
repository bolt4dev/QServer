using System.Diagnostics;
using System.Text.RegularExpressions;
using QServer.Config;
using QServer.Hosting;
using QServer.Logging;
using QServer.Pipeline;
using QServer.Server;
using QServer.Ui;

namespace QServer;

/// <summary>Result/summary of a run (used mainly by headless callers).</summary>
public readonly record struct RunResult(
    long In, long Shown, long Suppressed, double SentinelSeconds, int LastExit, long Dropped, IReadOnlyList<string> SettingKeys);

/// <summary>
/// The UI- and platform-agnostic orchestrator. It wires an <see cref="IServerHost"/> (capture/inject) and an
/// <see cref="IConsoleUi"/> (front-end) around the noise pipeline, the server-info collector and a restart
/// watchdog. Swap either side by passing a different host factory or UI; the engine is unchanged.
/// </summary>
public sealed class AppEngine
{
    readonly QServerConfig _cfg;
    readonly Func<IServerHost> _hostFactory;
    readonly IConsoleUi _ui;
    readonly ILineSink? _sink;
    readonly HostLog? _hostLog;

    // The live host is a field (not a RunAsync local) so EmergencyStop can reach it from a process-exit thread.
    // volatile: written by the supervisor task, read by EmergencyStop on the console-ctrl / ProcessExit thread.
    volatile IServerHost? _host;

    // Startup-progress state, shared between the supervisor (writer) and the ticker (reader). There is only ever
    // one active RunAsync, so instance fields are safe. The supervisor Restarts _hostStart and clears _warned per
    // host; the ticker reads _hostStart for the "Starting (Xs)" text and sets _warned when it prints the one-time
    // slow-start warning. The concurrent Stopwatch.Restart vs Stopwatch.Elapsed is a benign race — worst case one
    // status frame shows a slightly-off duration — so it is intentionally left unlocked (no synchronization added).
    readonly Stopwatch _hostStart = new();
    volatile bool _warned;

    public AppEngine(QServerConfig cfg, Func<IServerHost> hostFactory, IConsoleUi ui,
                     ILineSink? sink = null, HostLog? hostLog = null)
    {
        _cfg = cfg; _hostFactory = hostFactory; _ui = ui; _sink = sink; _hostLog = hostLog;
    }

    // Critical startup patterns: shown (prefixed "[startup]", error colour) even when a noise rule would hide
    // them — but ONLY before the ready sentinel, so a healthy server's steady-state noise rules are untouched.
    // This is the whole point of the feature: the real reason a start fails ("Could not load file or assembly",
    // "Login Failed!", ...) is often exactly what the hide rules suppress.
    static readonly Regex CriticalStartup = new(
        @"Messagebox \[ERROR\]|Could not load file or assembly|Login Failed!|" +
        @"Disconnected from custom battle server manager|Couldn't connect to custom battle server manager",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Strips the "[HH:MM:SS.fff]" console timestamp so a surfaced [startup] line is not doubly prefixed.
    static readonly Regex TsPrefix = new(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s?", RegexOptions.Compiled);

    /// <summary>
    /// Human meaning of a host/server exit code: the scraper's FAIL codes (see QServer.Scraper) plus a
    /// generic fallback. Surfaced on a failed start so the user learns WHY instead of seeing a bare number.
    /// <c>internal</c> (not private) purely so the startup-visibility tests can assert the mapping directly.
    /// </summary>
    internal static string DescribeExit(int code) => code switch
    {
        2 => "scraper: server exe not found",
        3 => "scraper: CreateProcess failed",
        4 => "scraper: could not attach to server console; server was killed",
        5 => "scraper: console handles unavailable; server was killed",
        10 => "scraper: panel process died; server was terminated",
        11 => "scraper: panel closed the pipe; server was terminated",
        _ => $"server exit code {code}",
    };

    /// <summary>Run until the user quits (interactive) or the server ends (headless / <paramref name="runSec"/>).</summary>
    public async Task<RunResult> RunAsync(int runSec = 0, CancellationToken externalCt = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        var proc = new LineProcessor(_cfg.Noise, dl => { _ui.AddLine(dl); _sink?.Write(dl.Text); }, _ => { });
        foreach (var bad in proc.InvalidRules)
            _ui.AddLine(new DisplayLine(DateTime.UtcNow, $"[config] ignored invalid noise rule: {bad}", Severity.Warn, false));
        var monitor = new ServerMonitor();
        var info = new ServerInfo();

        bool userQuit = false, autoRestart = _cfg.Restart.Enabled, manualRestart = false;
        double sentinelSec = -1; int lastExit = 0;
        var sw = Stopwatch.StartNew();

        _ui.ServerCommand += cmd => _host?.Send(cmd);
        _ui.MetaCommand += cmd =>
        {
            if (cmd == ":stop") { autoRestart = false; _host?.Stop(); }
            else if (cmd == ":restart") { manualRestart = true; _host?.Stop(); }
        };
        _ui.Quit += () => { userQuit = true; _host?.Stop(); cts.Cancel(); };

        var ticker = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                proc.Tick();
                var (cpu, ram) = monitor.Sample();
                // While waiting for the ready sentinel, count "Starting (Xs)" and warn ONCE if it drags on past
                // startupWarnSeconds (a slow or stuck start). _hostStart/_warned are shared with the supervisor.
                string stateText = monitor.State == ServerState.Starting
                    ? $"Starting ({(int)_hostStart.Elapsed.TotalSeconds}s)"
                    : monitor.State.ToString();
                if (!_warned && monitor.State == ServerState.Starting &&
                    _hostStart.Elapsed.TotalSeconds > _cfg.Server.StartupWarnSeconds)
                {
                    _warned = true;
                    _ui.AddLine(new DisplayLine(DateTime.UtcNow,
                        $"===== Server not ready after {_cfg.Server.StartupWarnSeconds}s; check the lines above / logs =====",
                        Severity.Warn, true));
                }
                _ui.SetStatus(new UiStatus(stateText, cpu, ram, proc.SpamRate,
                    proc.TotalIn, proc.TotalShown, proc.TotalSuppressed, _sink?.Dropped ?? 0));
                _ui.SetServerInfo(info.Snapshot());
                if (runSec > 0 && sw.Elapsed.TotalSeconds > runSec) cts.Cancel();
                try { await Task.Delay(120, cts.Token); } catch { }
            }
        });

        var supervisor = Task.Run(async () =>
        {
            double delay = _cfg.Restart.InitialDelaySeconds;
            var restarts = new Queue<DateTime>();
            while (!cts.IsCancellationRequested)
            {
                int lastServerPid = 0;   // pid this iteration's host reported; used for the post-exit death check
                var rawTail = new Queue<string>();   // last RAW (pre-pipeline) lines, kept per-host for the dump
                const int RawTailMax = 40;
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var h = _hostFactory();
                h.ServerPid += pid => { lastServerPid = pid; monitor.Attach(pid); _hostLog?.Log($"SERVER PID {pid}"); };
                h.ServerExited += code => exited.TrySetResult(code);
                h.Diagnostic += d =>
                {
                    _ui.AddLine(new DisplayLine(DateTime.UtcNow, "[host] " + d, Severity.Warn, false));
                    _hostLog?.Log("DIAG " + d);
                };
                h.HostFailed += r =>
                {
                    _ui.AddLine(new DisplayLine(DateTime.UtcNow, "[host] FAILED: " + r, Severity.Error, true));
                    _hostLog?.Log("HOST FAIL " + r);
                };
                h.LineReceived += line =>
                {
                    info.Observe(line);
                    // Capture the RAW line for the post-mortem tail BEFORE the pipeline can hide/rewrite it.
                    lock (rawTail) { rawTail.Enqueue(line); while (rawTail.Count > RawTailMax) rawTail.Dequeue(); }
                    // Pre-ready only: surface critical lines directly, bypassing the pipeline, so a hide rule can
                    // never swallow the real reason a start is failing. Strip the console timestamp for display.
                    if (sentinelSec < 0 && CriticalStartup.IsMatch(line))
                        _ui.AddLine(new DisplayLine(DateTime.UtcNow, "[startup] " + TsPrefix.Replace(line, ""), Severity.Error, true));
                    proc.Ingest(line);
                    if (sentinelSec < 0 && line.Contains(_cfg.Server.ReadySentinel, StringComparison.Ordinal))
                    {
                        sentinelSec = sw.Elapsed.TotalSeconds;
                        monitor.State = ServerState.Ready;
                        _hostLog?.Log($"READY after {_hostStart.Elapsed.TotalSeconds:F1}s");
                    }
                };

                _host = h;
                // Reset the startup clock and clear the one-time slow-start warning BEFORE flipping to Starting.
                // A ticker frame landing between these statements then sees State==Stopped (skips the warn logic)
                // rather than State==Starting against the PREVIOUS host's elapsed (>warn threshold), which would
                // print a bogus "Server not ready after Ns" / "Starting (3600s)" at the restart boundary.
                _hostStart.Restart(); _warned = false;
                monitor.State = ServerState.Starting;
                _hostLog?.Log("HOST START");
                h.Start();

                int code;
                try { code = await exited.Task.WaitAsync(cts.Token); }
                catch (OperationCanceledException) { _host = null; try { h.Stop(); } catch { } h.Dispose(); break; }
                lastExit = code;
                _host = null; h.Dispose();
                monitor.State = ServerState.Stopped;

                // The host is gone, but the server process it launched may not be. If it lingers, the next
                // launch would collide with it over the UDP port and lobby session (two servers coexisting),
                // so verify - by name-checked pid - that it is dead, killing it if it is not.
                EnsureServerDead(lastServerPid, Path.GetFileNameWithoutExtension(_cfg.Server.ExePath),
                    msg => _ui.AddLine(new DisplayLine(DateTime.UtcNow, "[cleanup] " + msg, Severity.Warn, true)));
                lastServerPid = 0;

                // Startup-failure post-mortem. sentinelSec is a GLOBAL "did we EVER reach ready" flag (it is not
                // reset per host), so this dump fires only for a server that never became ready — first-start
                // visibility, which is the point. A restart that fails AFTER an earlier successful ready will not
                // re-dump; that is an accepted limitation of this task's scope, not something to work around here.
                _hostLog?.Log($"HOST EXIT code={code} ({DescribeExit(code)}) sentinel={(sentinelSec >= 0 ? "yes" : "no")}");
                if (sentinelSec < 0)
                {
                    _ui.AddLine(new DisplayLine(DateTime.UtcNow, $"===== STARTUP FAILED: {DescribeExit(code)} =====", Severity.Error, true));
                    string[] tail; lock (rawTail) tail = rawTail.ToArray();
                    if (tail.Length > 0)
                    {
                        _ui.AddLine(new DisplayLine(DateTime.UtcNow, $"----- last raw lines ({tail.Length}) -----", Severity.Warn, true));
                        foreach (var t in tail) _ui.AddLine(new DisplayLine(DateTime.UtcNow, "  " + t, Severity.Warn, false));
                        foreach (var t in tail) _hostLog?.Log("TAIL " + t);
                    }
                }
                lock (rawTail) rawTail.Clear();

                if (userQuit || cts.IsCancellationRequested) break;

                bool wasManual = manualRestart; manualRestart = false;
                if (!(wasManual || autoRestart))
                {
                    _ui.AddLine(new DisplayLine(DateTime.UtcNow,
                        $"===== SERVER STOPPED (exit={code} ({DescribeExit(code)})). UI still open; scroll with mouse/PgUp, type :quit to exit. =====",
                        Severity.Error, true));
                    if (_ui.Headless) cts.Cancel();
                    break;
                }

                var now = DateTime.UtcNow;
                if (!wasManual)
                {
                    while (restarts.Count > 0 && (now - restarts.Peek()).TotalMinutes > _cfg.Restart.RestartWindowMinutes) restarts.Dequeue();
                    if (restarts.Count >= _cfg.Restart.MaxRestartsPerWindow)
                    {
                        _ui.AddLine(new DisplayLine(now,
                            $"===== Gave up restarting ({restarts.Count} times within {_cfg.Restart.RestartWindowMinutes} min). Type :quit. =====",
                            Severity.Error, true));
                        if (_ui.Headless) cts.Cancel();
                        break;
                    }
                    restarts.Enqueue(now);
                }

                double wait = wasManual ? 1 : delay;
                _ui.AddLine(new DisplayLine(now, $"----- server exited (exit={code}); restarting in {wait:F0}s -----", Severity.Warn, true));
                try { await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token); } catch { break; }
                if (!wasManual) delay = Math.Min(delay * _cfg.Restart.Multiplier, _cfg.Restart.MaxDelaySeconds);

                // Rebind grace: even once the old server pid is confirmed dead, give the OS a moment to release
                // the UDP port and let the lobby drop the old session before the next server claims them. This
                // also applies to manual :restart (hammering :restart is a common double-server trigger); the
                // normal :stop path breaks out above and never reaches here.
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _cfg.Restart.RebindGraceSeconds)), cts.Token); }
                catch { break; }
            }
        });

        _ui.Run(cts.Token); // interactive: blocks until quit; headless: returns on cancellation
        cts.Cancel();
        _host?.Stop();
        try { await Task.WhenAll(supervisor, ticker).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _host?.Dispose();

        return new RunResult(proc.TotalIn, proc.TotalShown, proc.TotalSuppressed, sentinelSec, lastExit,
            _sink?.Dropped ?? 0, info.Snapshot().Select(s => s.Key).ToList());
    }

    /// <summary>
    /// Last-chance synchronous teardown for process-exit paths (window X, logoff, OS shutdown). Safe to call
    /// from any thread; idempotent because <c>ScraperHost.Dispose</c> is, so it may race the supervisor's own
    /// teardown without harm. Kills the child tree AND closes the kill-on-close job handle.
    /// </summary>
    public void EmergencyStop()
    {
        var h = _host;
        try { h?.Stop(); } catch { }
        try { h?.Dispose(); } catch { }   // kills the tree AND closes the job handle
    }

    /// <summary>
    /// Blocks until the given server pid is gone, so the supervisor never launches the next server while the
    /// previous one is still alive - two servers would collide over the UDP port and the lobby session. The
    /// kill is name-verified to survive pid reuse: only if the pid still resolves AND its process name matches
    /// <paramref name="expectedName"/> is it killed (whole tree) and awaited; on a name mismatch the pid has
    /// been recycled by an unrelated process and is left untouched, so we never kill a stranger. A pid that no
    /// longer resolves - <see cref="Process.GetProcessById(int)"/> throws <see cref="ArgumentException"/>, or
    /// exits in the race window so <see cref="Process.ProcessName"/> throws <see cref="InvalidOperationException"/>
    /// - is already gone, which is the normal, healthy outcome and not an error.
    /// </summary>
    /// <param name="pid">The server pid reported for the run that just ended; values &lt;= 0 mean "never known".</param>
    /// <param name="expectedName">Process name (no extension) the pid must have to be eligible for killing.</param>
    /// <param name="report">Sink for human-readable cleanup notes (only called when action is actually taken).</param>
    static void EnsureServerDead(int pid, string expectedName, Action<string> report)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                return;   // pid was reused by an unrelated process - never kill a stranger
            report($"previous server (pid {pid}) still alive; killing before restart");
            try { p.Kill(entireProcessTree: true); } catch { /* it may have raced us to exit; WaitForExit confirms */ }
            if (!p.WaitForExit(10_000))
                report($"WARNING: previous server (pid {pid}) did not die within 10s");
        }
        catch (ArgumentException) { /* GetProcessById: no such pid - already gone, the normal case */ }
        catch (InvalidOperationException) { /* pid exited mid-inspection (ProcessName threw) - also already gone */ }
    }
}
