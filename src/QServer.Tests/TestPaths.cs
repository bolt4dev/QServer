namespace QServer.Tests;

/// <summary>
/// Locates the built helper executables that the lifecycle tests drive.
/// <para>
/// The repository root is found by walking up from the test assembly until the solution file
/// appears, rather than by counting directory levels: the number of levels differs per project
/// because <c>QServer</c>, <c>QServer.Tui</c> and <c>QServer.Scraper</c> declare
/// a <c>RuntimeIdentifier</c> and therefore emit into <c>bin/&lt;Cfg&gt;/net8.0/win-x64/</c>, while
/// projects without one emit into <c>bin/&lt;Cfg&gt;/net8.0/</c>. A silently wrong path here would
/// make every lifecycle test fail for the wrong reason, so a missing root throws instead.
/// </para>
/// </summary>
public static class TestPaths
{
    const string SolutionFileName = "QServer.sln";

    /// <summary>Repository root: the nearest ancestor of the test assembly containing the solution file.</summary>
    public static string Root { get; } = FindRoot(AppContext.BaseDirectory);

    /// <summary>
    /// Build configuration the tests were compiled in, inferred from the test assembly's own
    /// output path. Only the part below <see cref="Root"/> is inspected, so a repository that
    /// happens to live under a folder named "Release" does not skew the result.
    /// </summary>
    public static string Cfg { get; } =
        Path.GetRelativePath(Root, AppContext.BaseDirectory).Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

    /// <summary>The scraper ('C') that launches the real server and mirrors it over stdio.</summary>
    public static string ScraperExe =>
        Path.Combine(Root, "src", "QServer.Scraper", "bin", Cfg, "net8.0", "win-x64", "QServer.Scraper.exe");

    /// <summary>The TUI panel ('A'), the composition root.</summary>
    public static string PanelExe =>
        Path.Combine(Root, "src", "QServer", "bin", Cfg, "net8.0", "win-x64", "QServer.exe");

    /// <summary>Stand-in for the Bannerlord dedicated server ('B'); no RuntimeIdentifier, so no RID sub-folder.</summary>
    public static string FakeServerExe =>
        Path.Combine(Root, "tools", "FakeServer", "bin", Cfg, "net8.0", "FakeServer.exe");

    static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                return dir.FullName;

        throw new DirectoryNotFoundException(
            $"Could not locate '{SolutionFileName}' in any ancestor of '{start}'.");
    }
}
