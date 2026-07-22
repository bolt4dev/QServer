using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using QServer.Config;

namespace QServer.Hosting;

/// <summary>
/// Windows <see cref="IServerHost"/>. It launches the console-scraper helper (QServer.Scraper.exe), which
/// in turn launches the dedicated server and mirrors its console over stdio (stdout = captured lines, stderr =
/// <c>@CTRL ...</c> control messages). To support another OS, implement a different <see cref="IServerHost"/> -
/// nothing above this class changes.
/// <para>
/// <see cref="Start"/> may only be called once per instance: a second call overwrites the internal process/job
/// fields, leaking the first job handle and - because it is kill-on-close - leaving the previous process tree
/// alive until finalisation. Build a fresh <see cref="ScraperHost"/> per start instead of reusing one.
/// </para>
/// </summary>
public sealed class ScraperHost : IServerHost
{
    readonly string _scraperExe;
    readonly QServerConfig.ServerOptions _server;
    readonly QServerConfig.ScraperOptions _scraper;
    Process? _proc;
    KillOnCloseJob? _job;
    int _exitReported;

    public event Action<string>? LineReceived;
    public event Action<int>? ServerPid;
    public event Action<int>? ServerExited;
    public event Action? Ready;
    public event Action<string>? Diagnostic;
    public event Action<string>? HostFailed;

    public ScraperHost(string scraperExe, QServerConfig.ServerOptions server, QServerConfig.ScraperOptions scraper)
    {
        _scraperExe = scraperExe; _server = server; _scraper = scraper;
    }

    public void Start()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _scraperExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        void Arg(string a) => psi.ArgumentList.Add(a);
        Arg("--server-exe"); Arg(_server.ExePath);
        Arg("--server-args"); Arg(_server.Args);
        Arg("--server-cwd"); Arg(_server.WorkingDirectory);
        Arg("--poll-ms"); Arg(_scraper.PollMs.ToString());
        Arg("--readback"); Arg(_scraper.ReadbackLines.ToString());
        Arg("--buffer-width"); Arg(_scraper.BufferWidth.ToString());
        Arg("--buffer-height"); Arg(_scraper.BufferHeight.ToString());
        Arg("--attach-timeout-ms"); Arg(_scraper.AttachTimeoutMs.ToString());
        // The helper watches us and takes the server down if we die without getting to Dispose - the
        // cooperative counterpart to the kill-on-close job installed below.
        Arg("--parent-pid"); Arg(Environment.ProcessId.ToString());
        if (_scraper.HideServerWindow) Arg("--hide");
        Arg("--hide-delay-ms"); Arg(_scraper.HideDelayMs.ToString());

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) LineReceived?.Invoke(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OnStderr(e.Data); };
        // If the helper dies without an "@CTRL EXIT" (wrong server path, attach failure, crash), still report an exit
        // so the engine never hangs waiting for a server that will never start.
        _proc.Exited += (_, _) =>
        {
            // Flush the async stderr reader to EOF FIRST, so any buffered "@CTRL EXIT/FAIL" line is delivered
            // (and sets _exitReported) before this fallback reads the scraper's OWN code. The parameterless
            // WaitForExit() drains the readers; the bounded WaitForExit(int) does NOT (same distinction the
            // test helper FakeServerRun.StandardError documents). B does not inherit the scraper's stderr
            // pipe (CREATE_NEW_CONSOLE), so it closes promptly on exit and this returns without hanging.
            try { _proc?.WaitForExit(); } catch { }
            int ec = 0; try { ec = _proc?.ExitCode ?? 0; } catch { }
            if (Interlocked.Exchange(ref _exitReported, 1) == 0) ServerExited?.Invoke(ec);
        };
        _proc.Start();
        // Kernel backstop, installed before the helper can spawn the server: once this process dies for ANY
        // reason - window X, crash, Task Manager - the job handle closes with it and Windows terminates the
        // helper and the server underneath it. Without this, closing the panel orphans the server, which then
        // keeps the UDP port and the lobby session and makes the next start fail.
        try { _job = new KillOnCloseJob(); _job.Assign(_proc); }
        catch (Win32Exception ex)
        {
            // e.g. the panel itself runs inside a job that denies nesting (pre-Win8 semantics). Cooperative
            // shutdown still works; make the degradation visible instead of silent.
            Diagnostic?.Invoke($"job object unavailable ({ex.Message}); relying on cooperative kill only");
        }
        catch (Exception ex)
        {
            // Anything other than Win32Exception is not the expected "host denies nesting" case - it is a
            // bug in the interop above. Still degrade rather than crash the panel on startup, but say so
            // distinctly in the log so this does not get mistaken for the benign path.
            Diagnostic?.Invoke($"job object setup failed unexpectedly ({ex.GetType().Name}: {ex.Message}); relying on cooperative kill only");
        }
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    void OnStderr(string line)
    {
        const string ctrl = "@CTRL ";
        if (!line.StartsWith(ctrl, StringComparison.Ordinal)) { Diagnostic?.Invoke(line); return; }
        var rest = line[ctrl.Length..];
        if (rest.StartsWith("PID ") && int.TryParse(rest[4..], out var pid)) ServerPid?.Invoke(pid);
        else if (rest.StartsWith("FAIL "))
        {
            // "FAIL <code> <reason>": the helper itself failed and already killed the server. Surface the
            // reason first, then end the run - "EXIT" stays reserved for the server's own exit code.
            var parts = rest.Split(' ', 3);
            HostFailed?.Invoke(parts.Length > 2 ? parts[2] : rest);
            if (parts.Length > 1 && int.TryParse(parts[1], out var failCode) &&
                Interlocked.Exchange(ref _exitReported, 1) == 0) ServerExited?.Invoke(failCode);
        }
        else if (rest.StartsWith("EXIT ") && uint.TryParse(rest[5..], out var ucode))
        {
            // The scraper emits B's exit code as an UNSIGNED value; NTSTATUS crash codes (e.g. access
            // violation 0xC0000005, STATUS_CONTROL_C_EXIT 0xC000013A) exceed int.MaxValue, so parse as uint
            // and reinterpret the bits. int.TryParse here would fail on exactly those codes and let the
            // process-exit fallback mislabel a hard native crash as the scraper's own clean exit (0).
            if (Interlocked.Exchange(ref _exitReported, 1) == 0) ServerExited?.Invoke(unchecked((int)ucode));
        }
        else if (rest.StartsWith("READYHOST")) Ready?.Invoke();
        else Diagnostic?.Invoke(rest);
    }

    public void Send(string command)
    {
        var p = _proc;
        if (p is not { HasExited: false }) return;
        try { p.StandardInput.WriteLine(command); p.StandardInput.Flush(); }
        catch { /* the helper may be exiting/disposing concurrently */ }
    }

    public void Stop() => Send("@@stop");

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc?.Dispose();
        _proc = null;
        _job?.Dispose();   // kernel backstop: kills anything still alive under the job
        _job = null;
    }
}
