using QServer.Hosting;
using Xunit;

namespace QServer.Tests;

/// <summary>
/// Exercises the cooperative-cleanup contract directly (calling <see cref="ShutdownGuard.RunOnce"/>) rather than
/// through a real console signal: under <c>dotnet test</c> there is no console to deliver CTRL_CLOSE, so the
/// dedup + exception-swallowing logic is verified without depending on the OS control handler firing.
/// </summary>
public class ShutdownGuardTests
{
    [Fact]
    public void RunOnce_runs_each_action_exactly_once_and_swallows_exceptions()
    {
        int a = 0, b = 0;
        ShutdownGuard.Register(() => a++);
        ShutdownGuard.Register(() => { b++; throw new InvalidOperationException("must not escape"); });
        ShutdownGuard.RunOnce();
        ShutdownGuard.RunOnce();   // second call is a no-op
        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }
}
