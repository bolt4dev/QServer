using Xunit;

namespace QServer.Tests;

/// <summary>
/// Guards the executable-path resolution every later lifecycle test depends on. If an output
/// layout changes (a project gains or loses a RuntimeIdentifier, say), these fail here with a
/// clear message instead of surfacing as a confusing "process exited immediately" elsewhere.
/// </summary>
public class TestPathsTests
{
    [Fact]
    public void Root_contains_the_solution_file()
    {
        Assert.True(
            File.Exists(Path.Combine(TestPaths.Root, "QServer.sln")),
            $"TestPaths.Root resolved to '{TestPaths.Root}', which holds no QServer.sln.");
    }

    public static TheoryData<string, string> HelperExecutables => new()
    {
        { nameof(TestPaths.ScraperExe), TestPaths.ScraperExe },
        { nameof(TestPaths.PanelExe), TestPaths.PanelExe },
        { nameof(TestPaths.FakeServerExe), TestPaths.FakeServerExe },
    };

    [Theory]
    [MemberData(nameof(HelperExecutables))]
    public void Helper_executable_exists(string name, string path)
    {
        Assert.True(File.Exists(path), $"TestPaths.{name} points at '{path}', which does not exist.");
    }
}
