using QServer;
using QServer.Config;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// Proves startup failures stay legible: exit codes are explained, critical startup lines are shown even when
/// noise rules would hide them (but only before ready), and an exit that never reached ready dumps the raw tail.
/// </summary>
public class StartupVisibilityTests
{
    [Theory]
    [InlineData(4, "attach")]
    [InlineData(10, "panel")]
    [InlineData(11, "panel")]
    [InlineData(42, "42")]
    public void DescribeExit_maps_scraper_codes(int code, string expectedFragment)
        => Assert.Contains(expectedFragment, AppEngine.DescribeExit(code), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Critical_startup_line_surfaces_even_though_noise_rules_hide_it()
    {
        var cfg = new QServerConfig();          // default rules HIDE "Could not load file or assembly"
        cfg.Server.StartupWarnSeconds = 3600;
        var ui = new StubUi();
        StubHost? host = null;
        var engine = new AppEngine(cfg, () => host = new StubHost { OnStart = h =>
        {
            h.EmitPid(Environment.ProcessId);
            Task.Run(() =>
            {
                h.EmitLine("[00:00:01.000] Could not load file or assembly Foo.dll");
                h.EmitExit(1);
            });
        } }, ui);
        await engine.RunAsync(runSec: 8);
        lock (ui.Lines) Assert.Contains(ui.Lines, l => l.Contains("[startup]") && l.Contains("Could not load"));
    }

    [Fact]
    public async Task Exit_without_sentinel_dumps_raw_tail()
    {
        var cfg = new QServerConfig();
        var ui = new StubUi();
        var engine = new AppEngine(cfg, () => new StubHost { OnStart = h =>
        {
            h.EmitPid(Environment.ProcessId);
            Task.Run(() =>
            {
                h.EmitLine("[00:00:01.000] Loading xml file: a.xml");   // hidden by rules
                h.EmitLine("[00:00:01.100] something exploded");
                h.EmitExit(3);
            });
        } }, ui);
        await engine.RunAsync(runSec: 8);
        lock (ui.Lines)
        {
            Assert.Contains(ui.Lines, l => l.Contains("last raw lines"));
            Assert.Contains(ui.Lines, l => l.Contains("something exploded"));
        }
    }
}
