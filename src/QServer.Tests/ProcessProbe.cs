using System.Diagnostics;

namespace QServer.Tests;

/// <summary>Observes processes the tests did not start themselves, and therefore can only reach by pid.</summary>
static class ProcessProbe
{
    /// <summary>
    /// Polls until <paramref name="pid"/> no longer resolves, or <paramref name="timeout"/> elapses.
    /// <c>Process.GetProcessById</c> throwing <see cref="ArgumentException"/> is how .NET reports
    /// "no such process", so that exception is the success signal here, not an error.
    /// </summary>
    public static bool DiedWithin(int pid, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            try { using (Process.GetProcessById(pid)) { } }
            catch (ArgumentException) { return true; }
            Thread.Sleep(50);
        }
        return false;
    }
}
