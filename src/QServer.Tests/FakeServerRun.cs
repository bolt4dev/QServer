using System.Collections.Concurrent;
using System.Diagnostics;

namespace QServer.Tests;

/// <summary>
/// A running <c>FakeServer.exe</c> plus its captured output, shared by every test that drives one.
/// <para>
/// Output is read on the background reader (<c>BeginOutputReadLine</c>) and polled through
/// <see cref="WaitForLine"/> rather than read to completion: a FakeServer started with any
/// <c>--spawn-child</c> form hands its inherited stdout/stderr handles to the grandchild, so those
/// pipes stay open past the parent's death and a blocking <c>ReadToEnd()</c> / parameterless
/// <c>WaitForExit()</c> would hang the whole suite instead of failing one test.
/// </para>
/// </summary>
sealed class FakeServerRun : IDisposable
{
    /// <summary>Upper bound for every wait in the lifecycle tests; generous, since it only bounds failures.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    readonly ConcurrentQueue<string> _lines = new();
    readonly ConcurrentQueue<string> _stderr = new();

    FakeServerRun(Process process) => Process = process;

    public Process Process { get; }

    public static FakeServerRun Start(params string[] args)
    {
        var psi = new ProcessStartInfo(TestPaths.FakeServerExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var run = new FakeServerRun(Process.Start(psi) ?? throw new InvalidOperationException("FakeServer did not start."));
        run.Process.OutputDataReceived += (_, e) => { if (e.Data is not null) run._lines.Enqueue(e.Data); };
        run.Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) run._stderr.Enqueue(e.Data); };
        run.Process.BeginOutputReadLine();
        run.Process.BeginErrorReadLine();
        return run;
    }

    public bool WaitForLine(Func<string, bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < Timeout)
        {
            if (_lines.Any(predicate)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    public IEnumerable<string> Lines() => _lines.ToArray();

    public string OutputSoFar() => string.Join(Environment.NewLine, _lines);

    public string StandardError()
    {
        // Bound the wait for exit first, so a misuse on a still-running FakeServer fails the
        // test loudly instead of hanging the suite. Process.WaitForExit(int) returns as soon
        // as the process dies; it does not wait for the async stderr reader (BeginErrorReadLine)
        // to reach EOF, so a line could still be in flight here. Only the parameterless
        // WaitForExit() actually drains that reader - it's safe to call unbounded now because
        // the process has already exited and callers of StandardError() never start FakeServer
        // with --spawn-child, so no grandchild inherits our handles and keeps the pipe open.
        if (!Process.WaitForExit((int)Timeout.TotalMilliseconds))
            throw new InvalidOperationException(
                $"FakeServer (pid {Process.Id}) did not exit within {Timeout.TotalSeconds:0}s.");
        Process.WaitForExit();
        return string.Join(Environment.NewLine, _stderr);
    }

    public void Dispose()
    {
        // Kill the whole tree: a leaked grandchild would hold its port and break the next test,
        // which is exactly the orphan problem this suite exists to catch.
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        Process.Dispose();
    }
}
