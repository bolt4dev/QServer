using QServer.Pipeline;

namespace QServer.Ui;

/// <summary>Snapshot of the counters/metrics shown in the UI status bar.</summary>
public readonly record struct UiStatus(
    string State,
    double CpuPercent,
    double RamMb,
    double SpamPerSecond,
    long LinesIn,
    long LinesShown,
    long LinesSuppressed,
    long LinesDropped);

/// <summary>
/// The front-end contract. <see cref="QServer.AppEngine"/> drives any implementation of this interface, so
/// a new UI (a different terminal renderer, a WPF window, a web dashboard, ...) only needs to implement this - it
/// receives lines/status/settings to display and raises the user's commands back.
/// </summary>
public interface IConsoleUi
{
    /// <summary>True if this UI is non-interactive (e.g. output is redirected); the engine then self-terminates.</summary>
    bool Headless { get; }

    /// <summary>Append a processed line to the display.</summary>
    void AddLine(DisplayLine line);

    /// <summary>Update the status bar.</summary>
    void SetStatus(UiStatus status);

    /// <summary>Update the live server-settings panel.</summary>
    void SetServerInfo(IReadOnlyList<KeyValuePair<string, string>> settings);

    /// <summary>Clear the on-screen log.</summary>
    void Clear();

    /// <summary>A plain command the user wants sent to the server.</summary>
    event Action<string>? ServerCommand;

    /// <summary>A meta command (e.g. <c>:stop</c>, <c>:restart</c>) for the engine to act on.</summary>
    event Action<string>? MetaCommand;

    /// <summary>The user asked to quit.</summary>
    event Action? Quit;

    /// <summary>Run the UI loop; blocks until quit (interactive) or returns immediately/soon (headless).</summary>
    void Run(CancellationToken ct);
}
