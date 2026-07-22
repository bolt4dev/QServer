using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Rendering;
using QServer.Config;
using QServer.Pipeline;
using QServer.Ui;

namespace QServer.Tui;

/// <summary>
/// Spectre.Console implementation of <see cref="IConsoleUi"/>: a status bar, an optional server-settings strip,
/// a scrollable log panel and a command line. A live render loop draws the layout while a separate input thread
/// reads keyboard AND mouse-wheel events via ReadConsoleInput. Scrolling (PgUp/PgDn, mouse wheel, Home/End)
/// freezes the view; new lines never force the bottom until End. In a redirected environment it falls back to a
/// plain "headless" printer. Swap this class for any other <see cref="IConsoleUi"/> to change the front-end.
/// </summary>
public sealed class SpectreTui : IConsoleUi
{
    readonly object _lock = new();
    readonly List<DisplayLine> _buf = new();
    readonly QServerConfig.UiOptions _ui;
    long _base;
    const int Trim = 1024;
    readonly int _cap;
    int _visibleRows = 10;

    bool _follow = true;
    long _topAbs;
    bool _showInfo;

    // status snapshot (updated via SetStatus)
    string _state = "Starting";
    double _cpu, _ram, _spam;
    long _in, _shown, _supp, _dropped;
    IReadOnlyList<KeyValuePair<string, string>> _settings = Array.Empty<KeyValuePair<string, string>>();

    string _input = "";
    readonly List<string> _history = new();
    int _histIdx = -1;

    public bool Headless { get; }
    public event Action<string>? ServerCommand;
    public event Action<string>? MetaCommand;
    public event Action? Quit;

    public SpectreTui(QServerConfig.UiOptions ui)
    {
        _ui = ui;
        _cap = Math.Max(200, ui.ScrollbackLines);
        _showInfo = ui.ShowServerInfo;
        Headless = Console.IsInputRedirected || Console.IsOutputRedirected;
    }

    public void SetStatus(UiStatus s)
    {
        _state = s.State; _cpu = s.CpuPercent; _ram = s.RamMb; _spam = s.SpamPerSecond;
        _in = s.LinesIn; _shown = s.LinesShown; _supp = s.LinesSuppressed; _dropped = s.LinesDropped;
    }

    public void SetServerInfo(IReadOnlyList<KeyValuePair<string, string>> settings) => _settings = settings;

    public void AddLine(DisplayLine dl)
    {
        if (Headless) { PrintPlain(dl); return; }
        lock (_lock)
        {
            _buf.Add(dl);
            if (_buf.Count > _cap + Trim)
            {
                int rm = _buf.Count - _cap;
                _buf.RemoveRange(0, rm);
                _base += rm;
                if (!_follow && _topAbs < _base) _topAbs = _base;
            }
        }
    }

    public void Clear() { lock (_lock) { _buf.Clear(); _base = 0; _follow = true; _topAbs = 0; } }

    /// <summary>External quit request (Ctrl+C via ShutdownGuard, legacy input mode); same as typing :quit.</summary>
    public void RequestQuit() => Quit?.Invoke();

    static void Splash()
    {
        try
        {
            int w = 80; try { w = Console.WindowWidth; } catch { }
            AnsiConsole.Clear();
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new FigletText(w >= 104 ? "QServer" : "QS").Color(Color.Aqua).LeftJustified());
            AnsiConsole.MarkupLine("   [bold aqua]QServer[/] [grey]- a better console for the Bannerlord dedicated server[/]");
            AnsiConsole.MarkupLine("   [grey]made by[/] [bold]qruz[/] [grey]for the Bannerlord MP community[/]");
            Thread.Sleep(1600);
            AnsiConsole.Clear();
        }
        catch { }
    }

    public void Run(CancellationToken ct)
    {
        if (Headless) { while (!ct.IsCancellationRequested) Thread.Sleep(150); return; }

        if (_ui.ShowSplash) Splash();

        IntPtr hIn = GetStdHandle(STD_INPUT_HANDLE);
        bool rawMode = false; uint origMode = 0;
        if (GetConsoleMode(hIn, out origMode))
            rawMode = SetConsoleMode(hIn, ENABLE_EXTENDED_FLAGS | ENABLE_MOUSE_INPUT | ENABLE_WINDOW_INPUT);

        new Thread(() => InputLoop(hIn, ct, rawMode)) { IsBackground = true }.Start();
        try
        {
            AnsiConsole.Live(BuildLayout())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Start(ctx =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(BuildLayout());
                        ctx.Refresh();
                        Thread.Sleep(Math.Max(30, _ui.RefreshMs));
                    }
                });
        }
        finally { if (rawMode) SetConsoleMode(hIn, origMode); }
    }

    IRenderable BuildLayout()
    {
        // --- server info strip (top, horizontal) ---
        var settings = _settings;
        bool showInfo = _showInfo && settings.Count > 0;
        int infoReserve = 0;
        IRenderable? infoBar = null;
        if (showInfo)
        {
            int w = 100; try { w = Math.Max(40, Console.WindowWidth); } catch { }
            var (markup, plainLen) = BuildInfoMarkup(settings);
            int usable = Math.Max(20, w - 4);
            int lines = Math.Clamp((int)Math.Ceiling(plainLen / (double)usable) + 1, 1, 8);
            infoReserve = lines + 2;
            infoBar = new Panel(new Markup(markup)).Expand().Header("[bold aqua]Server[/]").Border(BoxBorder.Rounded);
        }

        // --- log body (fills the remaining height) ---
        int height = 25; try { height = Math.Max(12, Console.WindowHeight); } catch { }
        int bodyRows = Math.Max(3, height - 6 - infoReserve - 2);
        _visibleRows = bodyRows;

        var grid = new Grid().AddColumn();
        long total, top;
        lock (_lock)
        {
            total = _buf.Count;
            if (total == 0) { top = _base; }
            else
            {
                long newest = _base + total - 1;
                long lastTop = Math.Max(_base, newest - bodyRows + 1);
                top = _follow ? lastTop : Math.Clamp(_topAbs, _base, lastTop);
                int startIdx = (int)(top - _base);
                for (int i = startIdx; i < _buf.Count && i < startIdx + bodyRows; i++)
                {
                    var dl = _buf[i];
                    string color = dl.Highlight ? "green" : dl.Sev switch { Severity.Error => "red", Severity.Warn => "yellow", _ => "grey78" };
                    grid.AddRow(new Markup($"[{color}]{Markup.Escape(dl.Text)}[/]"));
                }
            }
        }

        string bodyHeader = _follow
            ? "[bold]Server Log[/] [grey](live / following)[/]"
            : $"[bold]Server Log[/] [yellow]## SCROLL ## End=live[/] [grey]line {top - _base + 1}-{Math.Min(top - _base + bodyRows, total)}/{total}[/]";
        var logPanel = new Panel(grid).Expand().Header(bodyHeader).Border(BoxBorder.Rounded);

        string status =
            $"[bold]{Markup.Escape(_state)}[/]   CPU [aqua]{_cpu,4:F0}%[/]  RAM [aqua]{_ram,5:F0}MB[/]  " +
            $"spam [red]{_spam,4:F0}/s[/]  in/shown/supp [grey]{_in}/{_shown}/{_supp}[/]  drop {_dropped}";
        var statusBar = new Panel(new Markup(status)).Expand().Border(BoxBorder.Square)
            .Header("[bold aqua]QServer[/] [grey]- made by qruz for the Bannerlord MP community[/]");

        var inputBar = new Panel(new Markup($"[green]>[/] {Markup.Escape(_input)}[grey]_[/]"))
            .Expand().Border(BoxBorder.Square)
            .Header("[grey]command  |  mouse/PgUp-PgDn scroll  End live  Home top  |  meta: :quit :stop :restart :info :clear[/]");

        if (!showInfo)
            return new Layout().SplitRows(
                new Layout("status").Update(statusBar).Size(3),
                new Layout("log").Update(logPanel),
                new Layout("input").Update(inputBar).Size(3));
        return new Layout().SplitRows(
            new Layout("status").Update(statusBar).Size(3),
            new Layout("info").Update(infoBar!).Size(infoReserve),
            new Layout("log").Update(logPanel),
            new Layout("input").Update(inputBar).Size(3));
    }

    static (string Markup, int PlainLen) BuildInfoMarkup(IReadOnlyList<KeyValuePair<string, string>> settings)
    {
        var sb = new System.Text.StringBuilder();
        int plain = 0; bool first = true;
        foreach (var kv in settings)
        {
            if (!first) { sb.Append("  [grey]|[/]  "); plain += 5; }
            first = false;
            bool secret = kv.Key.Contains("Password", StringComparison.OrdinalIgnoreCase);
            string color = secret ? "yellow" : "white";
            sb.Append($"[grey]{Markup.Escape(kv.Key)}:[/] [{color}]{Markup.Escape(kv.Value)}[/]");
            plain += kv.Key.Length + 2 + kv.Value.Length;
        }
        return (sb.ToString(), plain);
    }

    // ---- input ----
    void InputLoop(IntPtr hIn, CancellationToken ct, bool rawMode)
    {
        if (!rawMode) { LegacyKeyLoop(ct); return; }
        var rec = new INPUT_RECORD[1];
        while (!ct.IsCancellationRequested)
        {
            if (!ReadConsoleInput(hIn, rec, 1, out uint read) || read == 0) { Thread.Sleep(10); continue; }
            var r = rec[0];
            try
            {
                if (r.EventType == KEY_EVENT && r.Key.bKeyDown != 0)
                    HandleKey(r.Key.wVirtualKeyCode, (char)r.Key.UnicodeChar);
                else if (r.EventType == MOUSE_EVENT && (r.Mouse.dwEventFlags & MOUSE_WHEELED) != 0)
                {
                    short delta = (short)(r.Mouse.dwButtonState >> 16);
                    int step = Math.Max(1, _ui.WheelScrollLines);
                    if (delta > 0) ScrollUp(step); else ScrollDown(step);
                }
            }
            catch { /* a command handler must never kill the input thread */ }
        }
    }

    void LegacyKeyLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!Console.KeyAvailable) { Thread.Sleep(15); continue; } }
            catch { Thread.Sleep(200); continue; }
            var k = Console.ReadKey(true);
            try { HandleKey((ushort)k.Key, k.KeyChar); } catch { }
        }
    }

    void HandleKey(ushort vk, char ch)
    {
        // Raw input mode does not set ENABLE_PROCESSED_INPUT, so Ctrl+C / Ctrl+Break arrive here as a key event
        // with UnicodeChar 0x03 instead of firing the console ctrl handler. Quit gracefully, exactly like :quit.
        // Must sit BEFORE the switch: the `default: if (ch >= ' ')` branch would otherwise silently drop 0x03.
        if (ch == (char)3) { Quit?.Invoke(); return; }
        int step = Math.Max(1, _visibleRows - 1);
        switch (vk)
        {
            case VK_RETURN: SubmitInput(); break;
            case VK_BACK: if (_input.Length > 0) _input = _input[..^1]; break;
            case VK_ESCAPE: Quit?.Invoke(); break;
            case VK_PRIOR: ScrollUp(step); break;
            case VK_NEXT: ScrollDown(step); break;
            case VK_HOME: lock (_lock) { _follow = false; _topAbs = _base; } break;
            case VK_END: lock (_lock) { _follow = true; } break;
            case VK_UP:
                if (_history.Count > 0) { _histIdx = _histIdx < 0 ? _history.Count - 1 : Math.Max(0, _histIdx - 1); _input = _history[_histIdx]; }
                break;
            case VK_DOWN:
                if (_histIdx >= 0 && _histIdx < _history.Count - 1) { _histIdx++; _input = _history[_histIdx]; }
                else { _histIdx = -1; _input = ""; }
                break;
            default: if (ch >= ' ') _input += ch; break;
        }
    }

    void SubmitInput()
    {
        var cmd = _input.Trim(); _input = ""; _histIdx = -1;
        if (cmd.Length == 0) return;
        _history.Add(cmd);
        if (cmd.StartsWith(':'))
        {
            if (cmd is ":quit" or ":q") Quit?.Invoke();
            else if (cmd == ":clear") Clear();
            else if (cmd == ":info") _showInfo = !_showInfo;   // toggle the server-settings strip
            else MetaCommand?.Invoke(cmd);
        }
        else ServerCommand?.Invoke(cmd);
    }

    void ScrollUp(int step)
    {
        lock (_lock)
        {
            if (_buf.Count == 0) return;
            long newest = _base + _buf.Count - 1;
            long lastTop = Math.Max(_base, newest - _visibleRows + 1);
            if (_follow) { _follow = false; _topAbs = lastTop; }
            _topAbs = Math.Max(_base, _topAbs - step);
        }
    }

    void ScrollDown(int step)
    {
        lock (_lock)
        {
            if (_buf.Count == 0) return;
            long newest = _base + _buf.Count - 1;
            long lastTop = Math.Max(_base, newest - _visibleRows + 1);
            _topAbs = Math.Min(lastTop, _topAbs + step);
            if (_topAbs >= lastTop) _follow = true;
        }
    }

    static void PrintPlain(DisplayLine dl)
    {
        var c = dl.Highlight ? ConsoleColor.Green
            : dl.Sev switch { Severity.Error => ConsoleColor.Red, Severity.Warn => ConsoleColor.Yellow, _ => ConsoleColor.Gray };
        Console.ForegroundColor = c;
        Console.WriteLine(dl.Text);
        Console.ResetColor();
    }

    // ---- Win32 (mouse + raw keyboard) ----
    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_MOUSE_INPUT = 0x0010, ENABLE_WINDOW_INPUT = 0x0008, ENABLE_EXTENDED_FLAGS = 0x0080;
    const ushort KEY_EVENT = 0x0001, MOUSE_EVENT = 0x0002;
    const uint MOUSE_WHEELED = 0x0004;
    const ushort VK_BACK = 0x08, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_PRIOR = 0x21, VK_NEXT = 0x22,
                 VK_END = 0x23, VK_HOME = 0x24, VK_UP = 0x26, VK_DOWN = 0x28;

    // Blittable structs (int/ushort only) so the [Out] INPUT_RECORD[] marshals as a flat memory copy; a
    // non-blittable struct (bool/char) with the explicit Key/Mouse union corrupts the read (wrong vk/char).
    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount, wVirtualKeyCode, wVirtualScanCode;
        public ushort UnicodeChar;
        public uint dwControlKeyState;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSE_EVENT_RECORD { public COORD dwMousePosition; public uint dwButtonState, dwControlKeyState, dwEventFlags; }
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD Key;
        [FieldOffset(4)] public MOUSE_EVENT_RECORD Mouse;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ReadConsoleInput(IntPtr h, [Out] INPUT_RECORD[] buf, uint len, out uint read);
}
