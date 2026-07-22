namespace QServer.Logging;

/// <summary>A sink for the filtered (shown) log lines. <see cref="FileSink"/> is the default implementation.</summary>
public interface ILineSink
{
    /// <summary>Write one line.</summary>
    void Write(string line);

    /// <summary>Number of lines dropped because the sink could not keep up.</summary>
    long Dropped { get; }
}
