using System.Diagnostics;

namespace QServer.Hosting;

/// <summary>
/// Kills leftover server / scraper processes from a previous QServer run (crashed panel, closed
/// window before the job-object era, ...). Matching is by FULL executable path, so unrelated processes —
/// including another QServer instance pointed at a DIFFERENT server install — are never touched.
/// </summary>
public static class OrphanSweeper
{
    public static IReadOnlyList<string> Sweep(string serverExePath, string scraperExePath)
    {
        var report = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch { p.Dispose(); continue; }      // access denied / already exited

            bool match = Same(path, serverExePath) || Same(path, scraperExePath);
            if (!match || p.Id == Environment.ProcessId) { p.Dispose(); continue; }

            string name = Path.GetFileName(path!);
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                report.Add($"{name} (pid {p.Id})");
            }
            catch (Exception ex) { report.Add($"{name} (pid {p.Id}) KILL FAILED: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        return report;

        static bool Same(string? a, string b) =>
            a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
