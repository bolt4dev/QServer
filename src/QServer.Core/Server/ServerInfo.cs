using System.Text.RegularExpressions;

namespace QServer.Server;

/// <summary>
/// Captures live server settings from the console stream. The dedicated server logs its options while executing
/// its config as <c>--Changed: &lt;Key&gt;, to: &lt;Value&gt;</c> (and <c>--Value of: &lt;Key&gt;, is: &lt;Value&gt;</c>
/// when queried). This collects them into an ordered key/value map that the TUI renders as a persistent panel.
/// </summary>
public sealed class ServerInfo
{
    static readonly Regex Ts = new(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s?", RegexOptions.Compiled);
    static readonly Regex Changed = new(@"^--Changed:\s*(?<k>[^,]+),\s*to:\s*(?<v>.*)$", RegexOptions.Compiled);
    static readonly Regex ValueOf = new(@"^--Value of:\s*(?<k>[^,]+),\s*is:\s*(?<v>.*)$", RegexOptions.Compiled);

    readonly object _lock = new();
    readonly List<string> _order = new();
    readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    /// <summary>Inspect one raw console line and record any setting it reports.</summary>
    public void Observe(string line)
    {
        string s = Ts.Replace(line, "");
        var m = Changed.Match(s);
        if (!m.Success) m = ValueOf.Match(s);
        if (!m.Success) return;

        string key = m.Groups["k"].Value.Trim();
        string val = m.Groups["v"].Value.Trim();
        if (key.Length == 0) return;

        lock (_lock)
        {
            if (!_map.ContainsKey(key)) _order.Add(key);
            _map[key] = val;
        }
    }

    /// <summary>Current settings in first-seen (config) order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Snapshot()
    {
        lock (_lock)
            return _order.Select(k => new KeyValuePair<string, string>(k, _map[k])).ToList();
    }
}
