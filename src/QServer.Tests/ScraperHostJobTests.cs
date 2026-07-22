using System.Collections.Concurrent;
using QServer.Config;
using QServer.Hosting;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// End-to-end guard over the real A -> C -> B chain (panel host -> scraper -> FakeServer): after
/// <see cref="ScraperHost.Dispose"/> nothing must survive. This covers the whole teardown path, not
/// the job object specifically — <see cref="KillOnCloseJobTests"/> isolates the kernel semantics.
/// </summary>
public class ScraperHostJobTests
{
    [Fact]
    public void Dispose_kills_scraper_and_server()
    {
        var server = new QServerConfig.ServerOptions
        {
            ExePath = TestPaths.FakeServerExe,
            Args = "--ready-after-ms 100",
            WorkingDirectory = Path.GetDirectoryName(TestPaths.FakeServerExe)!,
        };
        using var host = new ScraperHost(TestPaths.ScraperExe, server, new QServerConfig.ScraperOptions());
        var diagnostics = new ConcurrentQueue<string>();
        host.Diagnostic += diagnostics.Enqueue;
        int serverPid = 0;
        using var gotPid = new ManualResetEventSlim();
        host.ServerPid += pid => { serverPid = pid; gotPid.Set(); };
        host.Start();
        Assert.True(gotPid.Wait(10_000), "scraper must report the server pid");

        // Start() degrades to cooperative-kill-only when the job cannot be created or assigned, and says so.
        // Without this the test would still pass on that degraded path (Dispose also kills the process tree),
        // and the kernel guarantee would be quietly absent in production.
        Assert.DoesNotContain(diagnostics, d => d.Contains("job object unavailable", StringComparison.Ordinal));

        host.Dispose();

        Assert.True(ProcessProbe.DiedWithin(serverPid, TimeSpan.FromSeconds(5)),
            $"FakeServer (pid {serverPid}) must be dead after ScraperHost.Dispose()");
    }

    /// <summary>
    /// The scraper emits the server's exit code as an UNSIGNED value (<c>@CTRL EXIT {(uint)code}</c>).
    /// NTSTATUS crash codes exceed <see cref="int.MaxValue"/> — STATUS_CONTROL_C_EXIT 0xC000013A is the
    /// exact rapid-cycle flake — so parsing them with <c>int.TryParse</c> fails and the run gets mislabelled
    /// as the scraper's own clean exit (0) via the process-exit fallback. This asserts the real crash code
    /// is reported faithfully instead, round-tripping to <c>unchecked((int)0xC000013A)</c> and never 0.
    /// </summary>
    [Fact]
    public void Reports_the_servers_real_exit_code_even_when_it_exceeds_int_MaxValue()
    {
        const int crashCode = unchecked((int)0xC000013A);   // -1073741510 (STATUS_CONTROL_C_EXIT)

        var server = new QServerConfig.ServerOptions
        {
            ExePath = TestPaths.FakeServerExe,
            Args = $"--ready-after-ms 100 --exit-code {crashCode}",
            WorkingDirectory = Path.GetDirectoryName(TestPaths.FakeServerExe)!,
        };
        using var host = new ScraperHost(TestPaths.ScraperExe, server, new QServerConfig.ScraperOptions());
        int reportedExit = int.MinValue;
        using var exited = new ManualResetEventSlim();
        host.ServerExited += code => { reportedExit = code; exited.Set(); };
        host.Start();

        Assert.True(exited.Wait(20_000), "ScraperHost must report the server's exit");
        Assert.NotEqual(0, reportedExit);           // 0 = the fallback reporting the scraper's own code (the bug)
        Assert.Equal(crashCode, reportedExit);      // faithful NTSTATUS round-trip
    }
}
