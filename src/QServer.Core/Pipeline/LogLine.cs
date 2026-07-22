namespace QServer.Pipeline;

/// <summary>Severity/colour of a displayed line.</summary>
public enum Severity { Info, Warn, Error }

/// <summary>A processed line ready to be shown in the TUI (and written to the filtered log).</summary>
public readonly record struct DisplayLine(DateTime Utc, string Text, Severity Sev, bool Highlight);
