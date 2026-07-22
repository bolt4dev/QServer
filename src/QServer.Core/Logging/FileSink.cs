using System.Text;
using System.Threading.Channels;

namespace QServer.Logging;

/// <summary>
/// Size-rotating file sink for the SHOWN (filtered) output only - hidden/throttled lines are not written here
/// (the server keeps its own full raw log). Writes are batched on a background task; if the channel fills up the
/// oldest entries are dropped and counted in <see cref="Dropped"/>.
/// </summary>
public sealed class FileSink : ILineSink, IDisposable
{
    readonly string _path;
    readonly long _maxBytes;
    readonly int _retained;
    readonly Channel<string> _ch = Channel.CreateBounded<string>(
        new BoundedChannelOptions(262144) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    readonly Task _writer;
    long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    public FileSink(string path, int maxFileSizeMb, int retainedFiles)
    {
        _path = path; _maxBytes = (long)maxFileSizeMb * 1024 * 1024; _retained = retainedFiles;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = Task.Run(WriteLoop);
    }

    public void Write(string line) { if (!_ch.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped); }

    async Task WriteLoop()
    {
        var enc = new UTF8Encoding(false);
        var sw = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), enc);
        long size = new FileInfo(_path).Exists ? new FileInfo(_path).Length : 0;
        var reader = _ch.Reader;
        var batch = new List<string>(1024);
        try
        {
            while (await reader.WaitToReadAsync())
            {
                batch.Clear();
                while (batch.Count < 4096 && reader.TryRead(out var l)) batch.Add(l);
                foreach (var l in batch) { sw.WriteLine(l); size += l.Length + 2; }
                sw.Flush();
                if (size >= _maxBytes)
                {
                    sw.Dispose();
                    Rotate();
                    sw = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read), enc);
                    size = 0;
                }
            }
        }
        catch { /* shutting down */ }
        finally { sw.Dispose(); }
    }

    void Rotate()
    {
        try
        {
            var old = $"{_path}.{_retained}";
            if (File.Exists(old)) File.Delete(old);
            for (int i = _retained - 1; i >= 1; i--)
            {
                var src = $"{_path}.{i}"; var dst = $"{_path}.{i + 1}";
                if (File.Exists(src)) File.Move(src, dst, true);
            }
            if (File.Exists(_path)) File.Move(_path, $"{_path}.1", true);
        }
        catch { }
    }

    public void Dispose()
    {
        _ch.Writer.TryComplete();
        try { _writer.Wait(3000); } catch { }
    }
}
