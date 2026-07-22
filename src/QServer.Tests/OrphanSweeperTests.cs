using System.Diagnostics;
using QServer.Hosting;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// Proves the startup safety net: a leftover server/scraper from a previous run must be killed before
/// the next launch, and matching is by FULL exe path so unrelated processes are never touched. This is
/// the "starting is flaky, I have to close/reopen once or twice" bug — an orphan holding the game port
/// and lobby session collides with the new instance until it is swept away.
/// </summary>
public class OrphanSweeperTests
{
    [Fact]
    public void Sweep_kills_matching_leftover_process()
    {
        // A plain heartbeat FakeServer (NO --spawn-child): it holds no ports and inherits no handles,
        // so it stands in for a leftover server the sweep must reap.
        using var orphan = Process.Start(new ProcessStartInfo(TestPaths.FakeServerExe)
        { UseShellExecute = false, CreateNoWindow = true })!;

        try
        {
            var killed = OrphanSweeper.Sweep(TestPaths.FakeServerExe, "Z:\\does-not-exist.exe");

            Assert.True(orphan.WaitForExit(5000), "orphan must be killed by the sweep");
            Assert.Contains(killed, k => k.Contains($"(pid {orphan.Id})"));
        }
        finally
        {
            // Belt-and-braces: if the sweep failed to kill it, a failing assertion must still not leak a
            // live FakeServer into the rest of the suite / the machine. Kill (not just Dispose) the tree.
            try { if (!orphan.HasExited) orphan.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    [Fact]
    public void Sweep_ignores_unrelated_processes()
    {
        var killed = OrphanSweeper.Sweep("Z:\\no-such-server.exe", "Z:\\no-such-scraper.exe");
        Assert.Empty(killed);
    }
}
