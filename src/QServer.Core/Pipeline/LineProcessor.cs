using System.Text.RegularExpressions;
using QServer.Config;

namespace QServer.Pipeline;

/// <summary>
/// Core noise pipeline. Each raw server line goes through timestamp-twin de-duplication and the ordered
/// rule set (throttle / hide / collapse / highlight / pass); surviving lines are pushed to <c>emit</c>, and
/// the spam meter is updated. <see cref="Ingest"/> runs on the scraper-reader thread and <see cref="Tick"/>
/// on the render thread, so shared state is guarded by a lock.
/// </summary>
public sealed class LineProcessor
{
    sealed class Rule
    {
        public required Regex Re;
        public required string Action;
        public double IntervalSec;
        public Severity Sev;
        public string? Meter;
        public DateTime LastEmit = DateTime.MinValue;
        public long Suppressed;
        public string LastText = "";
    }

    static readonly Regex TsPrefix = new(@"^\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s?", RegexOptions.Compiled);

    readonly object _lock = new();
    readonly List<Rule> _rules;
    readonly bool _collapseDup;
    readonly Action<DisplayLine> _emit;
    readonly Action<string> _raw;
    readonly Func<DateTime> _now;
    readonly double _spamWindow;
    readonly List<string> _invalid = new();

    /// <summary>Rules that failed to compile (bad regex) and were skipped; the caller can surface these.</summary>
    public IReadOnlyList<string> InvalidRules => _invalid;

    string? _held; DateTime _heldAt;
    string? _collapseText; long _collapseCount;
    readonly Queue<DateTime> _spam = new();

    long _in, _shown, _suppressed;
    public long TotalIn => Interlocked.Read(ref _in);
    public long TotalShown => Interlocked.Read(ref _shown);
    public long TotalSuppressed => Interlocked.Read(ref _suppressed);
    public double SpamRate { get { lock (_lock) { Prune(_now()); return _spam.Count / _spamWindow; } } }

    public LineProcessor(QServerConfig.NoiseOptions cfg, Action<DisplayLine> emit, Action<string> rawSink, Func<DateTime>? clock = null)
    {
        _emit = emit; _raw = rawSink; _collapseDup = cfg.DuplicateCollapse;
        _now = clock ?? (() => DateTime.UtcNow);
        _spamWindow = Math.Max(1, cfg.SpamMeterWindowSeconds);
        var rules = new List<Rule>();
        foreach (var r in cfg.Rules)
        {
            Regex re;
            try { re = new Regex(r.Match, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
            catch (Exception ex) { _invalid.Add($"'{r.Match}' ({ex.Message})"); continue; }
            rules.Add(new Rule
            {
                Re = re,
                Action = r.Action?.ToLowerInvariant() ?? "throttle",
                IntervalSec = r.ThrottleSeconds ?? cfg.DefaultThrottleSeconds,
                Sev = ParseSev(r.Severity),
                Meter = r.Meter,
            });
        }
        _rules = rules;
    }

    public void Ingest(string raw)
    {
        _raw(raw);
        Interlocked.Increment(ref _in);
        lock (_lock)
        {
            if (_held is null) { _held = raw; _heldAt = _now(); return; }
            if (IsTwin(_held, raw))
            {
                string keep = HasTs(raw) ? raw : _held; // keep the timestamped one
                _held = null;
                Interlocked.Increment(ref _suppressed); // the dropped twin is not shown
                Process(keep);
            }
            else
            {
                var older = _held; _held = raw; _heldAt = _now();
                Process(older);
            }
        }
    }

    public void Tick()
    {
        lock (_lock)
        {
            var now = _now();
            if (_held is not null && (now - _heldAt).TotalMilliseconds > 200) { var h = _held; _held = null; Process(h); }
            foreach (var r in _rules)
                if (r.Action == "throttle" && r.Suppressed > 0 && (now - r.LastEmit).TotalSeconds >= r.IntervalSec)
                    FlushThrottle(r, now);
            Prune(now);
        }
    }

    void Process(string line)
    {
        string content = TsPrefix.Replace(line, "");
        Rule? rule = null;
        foreach (var r in _rules) { try { if (r.Re.IsMatch(content)) { rule = r; break; } } catch { /* timeout/backtracking */ } }
        var now = _now();
        if (rule?.Meter == "spam") _spam.Enqueue(now);

        if (rule is null || rule.Action == "pass") { Show(line, Severity.Info, false); return; }
        switch (rule.Action)
        {
            case "hide": Interlocked.Increment(ref _suppressed); return; // already in the file
            case "highlight": Show(line, rule.Sev, true); return;
            case "collapse": Collapse(line, rule.Sev); return;
            case "throttle": Throttle(rule, line, now); return;
            default: Show(line, rule.Sev, false); return;
        }
    }

    void Throttle(Rule r, string line, DateTime now)
    {
        if ((now - r.LastEmit).TotalSeconds >= r.IntervalSec)
        {
            string text = r.Suppressed > 0 ? $"{line}   (+{r.Suppressed} suppressed/{r.IntervalSec:F0}s)" : line;
            r.LastEmit = now; r.LastText = line; r.Suppressed = 0;
            Show(text, r.Sev, false);
        }
        else { r.Suppressed++; r.LastText = line; Interlocked.Increment(ref _suppressed); }
    }

    void FlushThrottle(Rule r, DateTime now)
    {
        Show($"{r.LastText}   (x{r.Suppressed} in last {r.IntervalSec:F0}s)", r.Sev, false);
        r.LastEmit = now; r.Suppressed = 0;
    }

    void Collapse(string line, Severity sev)
    {
        if (_collapseDup && line == _collapseText) { _collapseCount++; Interlocked.Increment(ref _suppressed); return; }
        FlushCollapse();
        Show(line, sev, false);
        _collapseText = line; _collapseCount = 1;
    }

    void FlushCollapse()
    {
        if (_collapseCount > 1) Show($"   (above line x{_collapseCount})", Severity.Info, false);
        _collapseText = null; _collapseCount = 0;
    }

    void Show(string text, Severity sev, bool hl)
    {
        Interlocked.Increment(ref _shown);
        _emit(new DisplayLine(_now(), text, sev, hl));
    }

    void Prune(DateTime now) { while (_spam.Count > 0 && (now - _spam.Peek()).TotalSeconds > _spamWindow) _spam.Dequeue(); }

    static bool HasTs(string s) => TsPrefix.IsMatch(s);
    static bool IsTwin(string a, string b) =>
        HasTs(a) != HasTs(b) && TsPrefix.Replace(a, "") == TsPrefix.Replace(b, "");
    static Severity ParseSev(string? s) => s?.ToLowerInvariant() switch
    { "warn" => Severity.Warn, "warning" => Severity.Warn, "error" => Severity.Error, _ => Severity.Info };
}
