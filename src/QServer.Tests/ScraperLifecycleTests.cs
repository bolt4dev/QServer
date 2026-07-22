using System.Diagnostics;
using System.Text;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// Drives the real scraper ('C') against FakeServer ('B') and asserts that C never leaves B behind.
/// <para>
/// An orphaned server keeps the UDP port and the lobby session, which is what makes the NEXT start
/// fail, so every abnormal way of losing the panel ('A') must still take B down: stdin EOF, the death
/// of the watched parent and a failed attach each kill B behind a distinct exit code, and a hard kill
/// of C - where no code of ours runs at all - is caught by the kernel job. The ordinary <c>@@stop</c>
/// path must keep reporting B's own clean exit as 0.
/// </para>
/// </summary>
public class ScraperLifecycleTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Stdin_EOF_kills_server()
    {
        using var scraper = StartScraper();
        int pid = ReadServerPid(scraper.Stderr);

        scraper.Stdin.Close();                       // the panel died and closed the pipe

        Assert.True(ProcessProbe.DiedWithin(pid, Timeout), "server must die on stdin EOF");
        Assert.True(scraper.Process.WaitForExit((int)Timeout.TotalMilliseconds), "scraper must exit on stdin EOF");
        Assert.Equal(11, scraper.Process.ExitCode);
    }

    [Fact]
    public void Parent_death_kills_server()
    {
        // A FakeServer instance stands in for the panel: the scraper only ever watches a pid, so any
        // process it can open is a faithful parent for this purpose.
        using var parent = FakeServerRun.Start();
        using var scraper = StartScraper("--parent-pid", parent.Process.Id.ToString());
        int pid = ReadServerPid(scraper.Stderr);

        parent.Process.Kill();

        Assert.True(ProcessProbe.DiedWithin(pid, Timeout), "server must die when the watched parent dies");
        Assert.True(scraper.Process.WaitForExit((int)Timeout.TotalMilliseconds), "scraper must exit when the watched parent dies");
        Assert.Equal(10, scraper.Process.ExitCode);
    }

    [Fact]
    public void Hard_killing_the_scraper_kills_server()
    {
        // Process.Kill is TerminateProcess: no thread of the scraper gets to run, so neither the parent
        // watchdog nor the stdin-EOF path can help here. Only the kill-on-close job the scraper puts the
        // server into can - which is also why that job exists: the panel installs an equivalent one, but
        // only AFTER its CreateProcess returns, so a fast scraper could spawn the server outside it.
        using var scraper = StartScraper();
        int pid = ReadServerPid(scraper.Stderr);
        ReadCtrl(scraper.Stderr, "@CTRL READYHOST");   // the server is up and attached before we pull the plug

        scraper.Process.Kill();                        // NOT entireProcessTree - the kernel must do the reaping

        Assert.True(ProcessProbe.DiedWithin(pid, Timeout),
            "the scraper's kill-on-close job must reap the server when the scraper is killed outright");
    }

    [Fact]
    public void Attach_timeout_kills_server()
    {
        // A zero budget makes the attach give up on its first check, standing in for the cold/loaded
        // machine where attaching really does time out. That path used to `return` with B still running,
        // leaving an invisible server on the UDP port that made the next start fail.
        using var scraper = StartScraper("--attach-timeout-ms", "0");
        int pid = ReadServerPid(scraper.Stderr);

        // Pins the wire format ScraperHost.OnStderr parses into HostFailed + ServerExited.
        Assert.StartsWith("@CTRL FAIL 4 ", ReadCtrl(scraper.Stderr, "@CTRL FAIL "));
        Assert.True(ProcessProbe.DiedWithin(pid, Timeout), "server must die when the scraper gives up attaching");
        Assert.True(scraper.Process.WaitForExit((int)Timeout.TotalMilliseconds), "scraper must exit on attach timeout");
        Assert.Equal(4, scraper.Process.ExitCode);
    }

    [Fact]
    public void Stop_command_still_works()
    {
        using var scraper = StartScraper();
        int pid = ReadServerPid(scraper.Stderr);

        scraper.Stdin.WriteLine("@@stop");
        scraper.Stdin.Flush();

        Assert.True(ProcessProbe.DiedWithin(pid, Timeout), "server must die on @@stop");
        Assert.True(scraper.Process.WaitForExit((int)Timeout.TotalMilliseconds), "scraper must exit after @@stop");
        Assert.Equal(0, scraper.Process.ExitCode);   // B's own exit observed -> clean path
    }

    [Fact]
    public void Delayed_hide_emits_HIDDEN_telemetry_after_first_output()
    {
        // The hide itself is visual and validated by the experiment protocol (docs/experiments/hide-timing.md),
        // but the delayed-hide PATH is automatable: with a small delay and FakeServer's immediate
        // "Loading xml file:" rows, both gates (delay elapsed AND rows>0) are satisfied within a second, so the
        // HIDDEN telemetry must appear. StartScraper already passes --hide, so hiding is enabled.
        using var scraper = StartScraper("--hide-delay-ms", "200");

        // Bounded wait: the scraper keeps running (FakeServer heartbeats forever), so a missing HIDDEN line
        // would block ReadCtrl indefinitely - poll it on a task and fail fast instead of hanging the suite.
        string? hidden = WaitForCtrl(scraper.Stderr, "@CTRL HIDDEN", Timeout);

        Assert.NotNull(hidden);   // the delayed hide fired exactly once, after the delay and first output
        Assert.Matches(@"^@CTRL HIDDEN after_ms=\d+ rows=\d+$", hidden);
    }

    static ScraperRun StartScraper(params string[] extraArgs)
    {
        var psi = new ProcessStartInfo(TestPaths.ScraperExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        void Arg(string a) => psi.ArgumentList.Add(a);
        Arg("--server-exe"); Arg(TestPaths.FakeServerExe);
        Arg("--server-args"); Arg("--ready-after-ms 100");
        Arg("--server-cwd"); Arg(Path.GetDirectoryName(TestPaths.FakeServerExe)!);
        Arg("--hide");                       // no pop-up server console per test
        foreach (var a in extraArgs) Arg(a);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("scraper did not start");
        // Drain the mirrored console output. Nothing asserts on it, but FakeServer heartbeats forever
        // and an unread stdout pipe would eventually fill and block the scraper's main loop - which is
        // precisely the loop these tests measure.
        process.OutputDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        return new ScraperRun(process);
    }

    /// <summary>Blocks until the scraper announces the server pid on its control channel (stderr).</summary>
    static int ReadServerPid(StreamReader stderr)
    {
        const string prefix = "@CTRL PID ";
        return int.Parse(ReadCtrl(stderr, prefix)[prefix.Length..]);
    }

    /// <summary>
    /// Blocks until a control line starting with <paramref name="prefix"/> arrives. Reaching the end of
    /// stderr instead means the scraper never said it, which is a test failure, not a silent skip.
    /// </summary>
    static string ReadCtrl(StreamReader stderr, string prefix)
    {
        string? line;
        while ((line = stderr.ReadLine()) is not null)
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;

        throw new InvalidOperationException($"scraper exited without a '{prefix}' control line");
    }

    /// <summary>
    /// Bounded variant of <see cref="ReadCtrl"/> for control lines emitted while the scraper keeps running
    /// (so stderr never reaches EOF on the happy path). Reads on a background task and waits at most
    /// <paramref name="timeout"/>, so a missing line fails the test fast instead of blocking the whole suite.
    /// Returns null on timeout or if stderr closes first.
    /// </summary>
    static string? WaitForCtrl(StreamReader stderr, string prefix, TimeSpan timeout)
    {
        var read = Task.Run(() =>
        {
            // On timeout the caller abandons this task; the scraper's Dispose then kills the process, stderr
            // hits EOF (or is disposed), ReadCtrl throws, and the task ends cleanly - nothing left unobserved.
            try { return ReadCtrl(stderr, prefix); }
            catch (Exception) { return null; }
        });
        return read.Wait(timeout) ? read.Result : null;
    }

    /// <summary>A running scraper plus the two stdio ends the tests drive it through.</summary>
    sealed class ScraperRun : IDisposable
    {
        public ScraperRun(Process process) => Process = process;

        public Process Process { get; }
        public StreamWriter Stdin => Process.StandardInput;
        public StreamReader Stderr => Process.StandardError;

        public void Dispose()
        {
            // Safety net for a failing assertion: a surviving FakeServer would hold its console and
            // break the next test, which is the very orphan problem this suite exists to catch.
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
            Process.Dispose();
        }
    }
}
