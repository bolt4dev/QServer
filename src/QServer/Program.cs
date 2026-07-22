// QServer - composition root.
// Wires the Windows console host (ScraperHost) and the Spectre TUI (SpectreTui) around the portable engine
// (QServer.Core.AppEngine). To swap the UI, implement IConsoleUi and construct it here; to support another
// OS, implement IServerHost and pass a different host factory. The engine and pipeline stay the same.
//
// Modes: (default) live | --replay <file> [--synthetic] | --write-config <path> | --about/--version | --show-server | --run-sec <n>

using System.Text;
using Spectre.Console;
using QServer;
using QServer.Config;
using QServer.Hosting;
using QServer.Logging;
using QServer.Pipeline;
using QServer.Tui;

const string ConfigFileName = "qserver.json";

Console.OutputEncoding = new UTF8Encoding(false);

string? replayFile = null, writeConfigPath = null;
bool synthetic = false, showServer = false;
int runSec = 0;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--replay": if (i + 1 < args.Length) replayFile = args[++i]; break;
        case "--synthetic": synthetic = true; break;
        case "--show-server": showServer = true; break;
        case "--run-sec": if (i + 1 < args.Length) int.TryParse(args[++i], out runSec); break;
        case "--write-config": if (i + 1 < args.Length) writeConfigPath = args[++i]; break;
    }

if (args.Contains("--about") || args.Contains("--version")) { ShowAbout(); return 0; }
if (writeConfigPath != null)
{
    new QServerConfig().Save(writeConfigPath);
    Console.WriteLine($"Wrote default config to {writeConfigPath}");
    return 0;
}

var cfg = QServerConfig.Load(Path.Combine(AppContext.BaseDirectory, ConfigFileName));
if (showServer) cfg.Scraper.HideServerWindow = false;

if (synthetic || replayFile != null) return RunReplay(cfg, replayFile, synthetic);

// ---- live ----
string scraper = ResolveScraper(cfg);

// A blank server.exePath would otherwise NRE on ExePath.ToLowerInvariant() below, or throw deep inside the
// sweep's Path.GetFullPath("") — a raw stack trace on the very startup-hardening path. Fail clean instead,
// and guarantee the mutex/sweep normalization below never sees an empty string.
if (string.IsNullOrWhiteSpace(cfg.Server.ExePath))
{
    Console.WriteLine("[QServer] ERROR: server.exePath is empty — edit qserver.json and set it to your DedicatedCustomServer.Starter.exe path.");
    Console.WriteLine("(Or set server.argsFromBat to your start.bat and QServer will read exePath/args/workingDirectory from it.)");
    return 2;
}

if (!File.Exists(scraper))
{
    Console.WriteLine($"[QServer] ERROR: scraper not found: {scraper}");
    Console.WriteLine("Put QServer.Scraper.exe next to QServer.exe, or set scraper.path in the config.");
    return 2;
}

// ---- startup hygiene: refuse duplicates, then sweep leftovers ----
// Order is deliberate: the single-instance check comes BEFORE the sweep. If another healthy panel is
// already running for the same server exe, createdNew is false and we bail WITHOUT sweeping — otherwise
// we would kill that panel's live server/scraper (their exe paths match ours). Only the sole instance sweeps.
Mutex? instanceLock = null;
if (cfg.Server.SingleInstance)
{
    // Hash the CANONICAL path so this single-instance key agrees with OrphanSweeper.Same (which matches on
    // Path.GetFullPath). Otherwise two spellings of the same exe (/ vs \, relative vs absolute) produce
    // different keys, the second panel is NOT refused, and its sweep canonicalizes and kills the first
    // panel's live server. The empty-exePath guard above ensures GetFullPath never sees "".
    string key = "Global\\QServer-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(cfg.Server.ExePath).ToLowerInvariant())))[..16];
    instanceLock = new Mutex(initiallyOwned: true, key, out bool createdNew);
    if (!createdNew)
    {
        Console.WriteLine("[QServer] ERROR: another QServer is already running for this server exe.");
        Console.WriteLine("Close it first, or set server.singleInstance=false to allow multiple panels.");
        return 3;
    }
}

var sweptAway = cfg.Server.KillOrphansOnStart
    ? QServer.Hosting.OrphanSweeper.Sweep(cfg.Server.ExePath, scraper)
    : Array.Empty<string>();

FileSink? file = cfg.Logging.Enabled
    ? new FileSink(Path.Combine(AppContext.BaseDirectory, cfg.Logging.Path.Replace('/', Path.DirectorySeparatorChar)),
        cfg.Logging.MaxFileSizeMb, cfg.Logging.RetainedFiles)
    : null;

// Lifecycle host log: the evidence trail for "it sometimes fails" reports. Unlike the filtered FileSink above,
// its noise rules can never hide the lines that explain a failed start. null/empty path disables it entirely.
var hostLog = new HostLog(cfg.Logging.HostLogPath is { Length: > 0 } hp
    ? Path.Combine(AppContext.BaseDirectory, hp.Replace('/', Path.DirectorySeparatorChar)) : null);
hostLog.Log($"===== panel start pid={Environment.ProcessId} v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} =====");
foreach (var s in sweptAway) hostLog.Log("SWEEP killed " + s);

var ui = new SpectreTui(cfg.Ui);
foreach (var line in Banner()) ui.AddLine(new DisplayLine(DateTime.UtcNow, line, Severity.Info, true));
foreach (var s in sweptAway)
    ui.AddLine(new DisplayLine(DateTime.UtcNow, $"[cleanup] killed leftover {s}", Severity.Warn, true));

var engine = new AppEngine(cfg, () => new ScraperHost(scraper, cfg.Server, cfg.Scraper), ui, file, hostLog);
ShutdownGuard.QuitRequested += ui.RequestQuit;
ShutdownGuard.Register(() =>
{
    engine.EmergencyStop();
    file?.Dispose();                            // flush the filtered log inside the OS grace window
    hostLog.Log("shutdown"); hostLog.Dispose(); // ...and the lifecycle log (Dispose is idempotent)
});
var result = await engine.RunAsync(runSec);
file?.Dispose();
hostLog.Dispose();

if (ui.Headless)
{
    Console.WriteLine($"[QServer] in/shown/supp = {result.In}/{result.Shown}/{result.Suppressed}  " +
                      $"sentinel={(result.SentinelSeconds >= 0 ? $"{result.SentinelSeconds:F1}s" : "none")}  " +
                      $"exit={result.LastExit}  drop={result.Dropped}");
    Console.WriteLine($"[QServer] captured {result.SettingKeys.Count} server settings: {string.Join(", ", result.SettingKeys)}");
}
GC.KeepAlive(instanceLock);   // hold the single-instance lock for the whole run
return result.In > 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------------------------
static int RunReplay(QServerConfig cfg, string? file, bool synthetic)
{
    if (!synthetic && (file is null || !File.Exists(file)))
    { Console.WriteLine($"[QServer] replay file not found: {file}"); return 2; }
    var lines = synthetic ? Synthetic() : File.ReadLines(file!).ToList();
    var vnow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var proc = new LineProcessor(cfg.Noise, PrintDisplay, _ => { }, () => vnow);

    double perLineMs = synthetic ? 2 : 7;
    foreach (var line in lines) { proc.Ingest(line); vnow = vnow.AddMilliseconds(perLineMs); proc.Tick(); }
    vnow = vnow.AddSeconds(120); proc.Tick();

    Console.ResetColor();
    double reduce = proc.TotalIn > 0 ? 100.0 * (1 - (double)proc.TotalShown / proc.TotalIn) : 0;
    Console.WriteLine();
    Console.WriteLine("================ REPLAY RESULT ================");
    Console.WriteLine($"Input (raw)      : {proc.TotalIn}");
    Console.WriteLine($"Shown            : {proc.TotalShown}");
    Console.WriteLine($"Suppressed       : {proc.TotalSuppressed}");
    Console.WriteLine($"Display reduction: {reduce:F1}%");
    Console.WriteLine("==============================================");
    return 0;
}

static List<string> Synthetic()
{
    var l = new List<string> { "[00:00:00.100] Custom Server is ready! You can now enter console commands (Enter 'list' ...)." };
    for (int i = 0; i < 300; i++)
    {
        l.Add($"[00:00:0{i % 9}.{i:000}] Handler not found for network message 55");
        if (i % 20 == 0) l.Add($"[00:00:0{i % 9}.{i:000}] Http post request(TaleWorlds.Diamond.Rest.AliveMessage) is successful");
        if (i % 50 == 0) l.Add($"[00:00:0{i % 9}.{i:000}] Player Bob joined the game");
    }
    return l;
}

static void PrintDisplay(DisplayLine dl)
{
    Console.ForegroundColor = dl.Highlight ? ConsoleColor.Green
        : dl.Sev switch { Severity.Error => ConsoleColor.Red, Severity.Warn => ConsoleColor.Yellow, _ => ConsoleColor.Gray };
    Console.WriteLine(dl.Text);
    Console.ResetColor();
}

static string ResolveScraper(QServerConfig cfg)
{
    if (!string.IsNullOrWhiteSpace(cfg.Scraper.Path) && File.Exists(cfg.Scraper.Path)) return cfg.Scraper.Path;
    return Path.Combine(AppContext.BaseDirectory, "QServer.Scraper.exe");
}

static void ShowAbout()
{
    string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    try
    {
        int w = 80; try { w = Console.WindowWidth; } catch { }
        AnsiConsole.Write(new FigletText(w >= 104 ? "QServer" : "QS").Color(Color.Aqua));
        AnsiConsole.MarkupLine("  [grey]a better console for the Bannerlord dedicated server[/]");
        AnsiConsole.MarkupLine($"  [grey]made by[/] [bold]qruz[/] [grey]for the Bannerlord MP community[/]  [grey]v{version}[/]");
        AnsiConsole.MarkupLine("  [grey]support:[/] patreon.com/qruz   [grey]·[/]   [grey]discord:[/] 96u2");
    }
    catch { foreach (var b in Banner()) Console.WriteLine(b); }
}

static string[] Banner()
{
    string[] content =
    {
        "QServer  -  a better console for the Bannerlord server",
        "made by qruz   -   for the Bannerlord MP community",
    };
    int inner = content.Max(c => c.Length) + 2;
    string bar = "+" + new string('-', inner) + "+";
    var lines = new List<string> { bar };
    foreach (var c in content) lines.Add("| " + c.PadRight(inner - 2) + " |");
    lines.Add(bar);
    return lines.ToArray();
}
