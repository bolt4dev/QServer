using System.Diagnostics;
using QServer.Config;
using QServer.Hosting;
using QServer.Pipeline;
using QServer.Ui;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// A headless <see cref="IConsoleUi"/> for the supervisor tests. It records the lines the engine emits and
/// blocks <see cref="Run"/> until the run's cancellation token fires; the test ends the run through that
/// token (passed as <c>externalCt</c> to <see cref="AppEngine.RunAsync"/>), not through a UI command.
/// </summary>
sealed class StubUi : IConsoleUi
{
    public readonly List<string> Lines = new();
    public bool Headless => true;

    // The engine subscribes to these, but this stub never raises them (the test drives the run via the
    // host events and an external cancellation token). Silence CS0067 rather than adding a method that
    // fires them - firing an event would invoke any real subscriber, a landmine if reused elsewhere.
#pragma warning disable CS0067
    public event Action<string>? ServerCommand;
    public event Action<string>? MetaCommand;
    public event Action? Quit;
#pragma warning restore CS0067

    public void AddLine(DisplayLine l) { lock (Lines) Lines.Add(l.Text); }
    public void SetStatus(UiStatus s) { }
    public void SetServerInfo(IReadOnlyList<KeyValuePair<string, string>> s) { }
    public void Clear() { }
    public void Run(CancellationToken ct) => ct.WaitHandle.WaitOne();
}

/// <summary>
/// A scriptable <see cref="IServerHost"/>. The test wires <see cref="OnStart"/> to decide, per launch, what
/// the "server" does: emit a pid and then die (leaving the pid alive), or simply record that a later launch
/// happened. <c>EmitLine</c> raises <see cref="LineReceived"/> so a test can feed console output (used by the
/// Task 6 startup-visibility tests).
/// </summary>
sealed class StubHost : IServerHost
{
    // Raised by the test through EmitPid / EmitExit / EmitLine.
    public event Action<int>? ServerPid;
    public event Action<int>? ServerExited;
    public event Action<string>? LineReceived;

    // Part of the interface but not exercised by this test; silence CS0067 (see StubUi for the rationale).
#pragma warning disable CS0067
    public event Action? Ready;
    public event Action<string>? Diagnostic;
    public event Action<string>? HostFailed;
#pragma warning restore CS0067

    public Action<StubHost>? OnStart;

    public void Start() => OnStart?.Invoke(this);
    public void EmitPid(int pid) => ServerPid?.Invoke(pid);
    public void EmitExit(int code) => ServerExited?.Invoke(code);
    public void EmitLine(string line) => LineReceived?.Invoke(line);
    public void Send(string command) { }
    public void Stop() { }
    public void Dispose() { }
}

public class SupervisorRestartTests
{
    /// <summary>
    /// The leak this task fixes: the supervisor must not launch the next server while the previous server
    /// pid is still alive (the two would fight over the UDP port and lobby session). A real, name-matching
    /// FakeServer stands in for a server that outlived its host; the engine must reap it before restarting.
    /// </summary>
    [Fact]
    public async Task Next_host_does_not_start_until_previous_server_pid_is_dead()
    {
        // A plain no-arg FakeServer heartbeats forever (like OrphanSweeperTests' orphan), so it faithfully
        // plays a server that OUTLIVES its host. Its process name is "FakeServer", which the name-verified
        // kill in EnsureServerDead must match (cfg.Server.ExePath below).
        using var zombie = Process.Start(new ProcessStartInfo(TestPaths.FakeServerExe)
        { UseShellExecute = false, CreateNoWindow = true })!;

        var cfg = new QServerConfig();
        cfg.Restart.Enabled = true;
        cfg.Restart.InitialDelaySeconds = 0;
        cfg.Restart.RebindGraceSeconds = 0;
        cfg.Server.ExePath = TestPaths.FakeServerExe;   // so the name check matches the real "FakeServer" zombie

        bool zombieWasAliveAtSecondStart = true;
        int starts = 0;
        var secondStarted = new TaskCompletionSource();

        var engine = new AppEngine(cfg, () =>
        {
            var h = new StubHost();
            h.OnStart = self =>
            {
                int n = Interlocked.Increment(ref starts);
                if (n == 1)
                {
                    self.EmitPid(zombie.Id);          // supervisor now believes this pid is the server
                    Task.Run(() => self.EmitExit(1)); // ...and the HOST dies while the pid lives on
                }
                else
                {
                    zombieWasAliveAtSecondStart = !zombie.HasExited;
                    secondStarted.TrySetResult();
                }
            };
            return h;
        }, new StubUi());

        // RunAsync blocks synchronously inside _ui.Run until cancelled, so drive it on a background task and
        // end it promptly through the external token once the assertion has been observed (runSec is only a
        // backstop). This keeps the run from hanging for the full runSec.
        using var extCts = new CancellationTokenSource();
        var run = Task.Run(() => engine.RunAsync(runSec: 30, externalCt: extCts.Token));
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(zombieWasAliveAtSecondStart, "old server pid must be dead before the next host starts");
        }
        finally
        {
            extCts.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* teardown is best-effort */ }
            // Belt-and-braces: a failing assertion (RED) means the engine did NOT reap the zombie, so the
            // test must, or a live FakeServer would leak into the rest of the suite / the machine.
            try { if (!zombie.HasExited) zombie.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }
}
