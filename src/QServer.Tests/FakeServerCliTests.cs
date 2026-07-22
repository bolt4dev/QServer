using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// Pins the FakeServer CLI contract. Tasks 1-8 assert on these exact signals — the ready sentinel,
/// the <c>CHILDPID</c> line and the bind-failure exit code — so a silent change here would quietly
/// invalidate every later lifecycle test rather than fail one.
/// </summary>
public class FakeServerCliTests
{
    static readonly TimeSpan Timeout = FakeServerRun.Timeout;

    [Fact]
    public void Prints_the_ready_sentinel()
    {
        using var run = FakeServerRun.Start("--ready-after-ms", "100");

        Assert.True(
            run.WaitForLine(line => line.StartsWith("Custom Server is ready!", StringComparison.Ordinal)),
            $"No ready sentinel within {Timeout.TotalSeconds:0}s. Output so far:\n{run.OutputSoFar()}");
    }

    [Fact]
    public void Exits_42_when_the_udp_port_is_already_taken()
    {
        // Hold a port ourselves so FakeServer cannot have it - this is the orphan-holds-the-port
        // situation the startup bug is about.
        using var occupier = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)occupier.Client.LocalEndPoint!).Port;

        using var run = FakeServerRun.Start("--bind-udp", port.ToString());

        Assert.True(run.Process.WaitForExit((int)Timeout.TotalMilliseconds), "FakeServer did not exit.");
        Assert.Equal(42, run.Process.ExitCode);
        Assert.Contains("FAKESERVER: bind failed", run.StandardError());
    }

    [Fact]
    public void Exits_2_when_a_flag_value_is_missing_or_not_an_integer()
    {
        using var run = FakeServerRun.Start("--ready-after-ms", "notanumber");

        Assert.True(run.Process.WaitForExit((int)Timeout.TotalMilliseconds), "FakeServer did not exit.");
        Assert.Equal(2, run.Process.ExitCode);
        Assert.Contains("FAKESERVER: '--ready-after-ms' needs an integer value.", run.StandardError());
    }

    [Fact]
    public void Spawn_child_after_ms_delays_the_child_then_reports_its_pid()
    {
        var started = Stopwatch.StartNew();
        using var run = FakeServerRun.Start("--spawn-child-after-ms", "1500");

        Assert.True(
            run.WaitForLine(line => line.StartsWith("CHILDPID ", StringComparison.Ordinal)),
            $"No CHILDPID within {Timeout.TotalSeconds:0}s. Output so far:\n{run.OutputSoFar()}");
        var elapsed = started.Elapsed;

        // The delay is the whole point of the flag: Task 1 assigns a job object in this window.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(1400), $"Child appeared after only {elapsed.TotalMilliseconds:0}ms.");

        int pid = int.Parse(run.Lines().First(l => l.StartsWith("CHILDPID ", StringComparison.Ordinal))["CHILDPID ".Length..]);
        using var child = Process.GetProcessById(pid);
        Assert.False(child.HasExited);
    }
}
