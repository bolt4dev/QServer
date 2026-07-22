using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using QServer.Hosting;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// Isolates the kernel guarantee: these tests close ONLY the job handle and never call
/// <see cref="Process.Kill(bool)"/>, so the members can only die because the kernel enforced
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. That is exactly the path taken when the panel is
/// closed with the window X, killed from Task Manager or lost to a crash — no cooperation.
/// </summary>
public class KillOnCloseJobTests
{
    /// <summary>How long FakeServer waits before spawning its grandchild; the job must be assigned inside this window.</summary>
    const int ChildDelayMs = 1500;

    /// <summary>Margin kept between "job assigned" and "grandchild spawned" so the test never passes by luck.</summary>
    const int AssignSlackMs = 100;

    [Fact]
    public void Dispose_kills_assigned_process()
    {
        using var run = FakeServerRun.Start();

        // `using` as well as the explicit Dispose() below: the explicit call is the act under test,
        // the `using` makes sure a failing assertion still releases the handle (Dispose is idempotent).
        using var job = new KillOnCloseJob();
        job.Assign(run.Process);
        job.Dispose();

        Assert.True(run.Process.WaitForExit(3000), "closing the job handle must terminate the member process");
    }

    [Fact]
    public void Dispose_kills_grandchildren_too()
    {
        // Mirrors A -> C -> B: the assigned process spawns its OWN child afterwards, and closing the
        // job must kill both. The child has to appear AFTER the assignment - on Windows 8+ a job does
        // not retroactively capture processes that already exist - hence the delayed spawn.
        var sinceStart = Stopwatch.StartNew();
        using var run = FakeServerRun.Start("--spawn-child-after-ms", ChildDelayMs.ToString());

        using var job = new KillOnCloseJob();
        job.Assign(run.Process);

        Assert.True(sinceStart.ElapsedMilliseconds < ChildDelayMs - AssignSlackMs,
            $"Job assignment took {sinceStart.ElapsedMilliseconds}ms, so the grandchild may already have been " +
            "spawned outside the job and this test would prove nothing.");

        Assert.True(
            run.WaitForLine(line => line.StartsWith("CHILDPID ", StringComparison.Ordinal)),
            $"FakeServer must report its child pid. Output so far:\n{run.OutputSoFar()}");
        int childPid = int.Parse(run.Lines().First(l => l.StartsWith("CHILDPID ", StringComparison.Ordinal))["CHILDPID ".Length..]);

        // Prove the grandchild is actually running first, so "it died" below cannot mean "it never lived".
        // Process.GetProcessById throws ArgumentException if the pid is already gone, which would otherwise
        // surface as a generic "no process with that ID" failure instead of naming what this guard is for.
        try
        {
            using var child = Process.GetProcessById(childPid);
            Assert.False(child.HasExited, $"grandchild (pid {childPid}) was already gone before the job closed");
        }
        catch (ArgumentException)
        {
            Assert.Fail($"grandchild (pid {childPid}) was already gone before the job closed");
        }

        job.Dispose();

        Assert.True(run.Process.WaitForExit(3000), "closing the job handle must terminate the member process");
        Assert.True(ProcessProbe.DiedWithin(childPid, TimeSpan.FromSeconds(3)),
            $"grandchild (pid {childPid}) must die when the job handle closes");
    }

    [Fact]
    public void Assign_throws_win32exception_once_the_process_has_exited()
    {
        // ScraperHost.Start() relies on exactly this exception type to tell the benign "job unavailable"
        // case apart from a genuine interop bug and degrade to cooperative-kill-only instead of crashing
        // the panel on startup. That branch has no coverage without this test.

        // Occupy a UDP port so FakeServer's own bind fails and it exits almost immediately (see
        // FakeServerCliTests.Exits_42_when_the_udp_port_is_already_taken) - the fastest deterministic way
        // to get a handle to a process that has already fully exited.
        using var occupier = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        int port = ((IPEndPoint)occupier.Client.LocalEndPoint!).Port;
        using var run = FakeServerRun.Start("--bind-udp", port.ToString());
        Assert.True(run.Process.WaitForExit((int)FakeServerRun.Timeout.TotalMilliseconds), "FakeServer did not exit.");

        using var job = new KillOnCloseJob();

        Assert.Throws<Win32Exception>(() => job.Assign(run.Process));
    }
}
