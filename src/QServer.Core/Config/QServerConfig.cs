using System.Text.Json;
using System.Text.Json.Serialization;

namespace QServer.Config;

/// <summary>
/// Root configuration for QServer. Loaded from <c>qserver.json</c> placed next to the
/// executable. If the file is missing, the built-in defaults are used (which already give a clean console).
/// </summary>
public sealed class QServerConfig
{
    /// <summary>Which server executable to launch and how.</summary>
    public ServerOptions Server { get; set; } = new();

    /// <summary>Console-scraper (helper process) tuning.</summary>
    public ScraperOptions Scraper { get; set; } = new();

    /// <summary>Terminal UI behaviour.</summary>
    public UiOptions Ui { get; set; } = new();

    /// <summary>Automatic restart / watchdog behaviour.</summary>
    public RestartOptions Restart { get; set; } = new();

    /// <summary>Console noise control (hide / throttle / highlight rules).</summary>
    public NoiseOptions Noise { get; set; } = new();

    /// <summary>Filtered log file written by QServer (mirrors the on-screen output).</summary>
    public LoggingOptions Logging { get; set; } = new();

    public sealed class ServerOptions
    {
        /// <summary>Full path to the dedicated-server starter executable.</summary>
        public string ExePath { get; set; } =
            @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Dedicated Server\bin\Win64_Shipping_Server\DedicatedCustomServer.Starter.exe";

        /// <summary>Command-line arguments passed to the server (module block, port, config file, ...).</summary>
        public string Args { get; set; } =
            "/dedicatedcustomserverconfigfile server_config.txt _MODULES_*Native*Multiplayer*_MODULES_";

        /// <summary>
        /// Optional: path to an existing launch .bat. When set, its first <c>.exe</c> token and the rest of the
        /// line are parsed into <see cref="ExePath"/> / <see cref="Args"/> / <see cref="WorkingDirectory"/>,
        /// so you can point QServer at your current start script instead of duplicating the command.
        /// </summary>
        public string? ArgsFromBat { get; set; }

        /// <summary>Working directory for the server process.</summary>
        public string WorkingDirectory { get; set; } =
            @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Dedicated Server\bin\Win64_Shipping_Server";

        /// <summary>Console line that marks the server as ready (used to flip the UI state to Ready).</summary>
        public string ReadySentinel { get; set; } = "is ready! You can now enter console commands";

        /// <summary>
        /// Warn once in the UI if the ready sentinel has not appeared after this many seconds (a slow/stuck
        /// start). Optional key with a safe default, so older <c>qserver.json</c> files keep loading.
        /// </summary>
        public int StartupWarnSeconds { get; set; } = 90;

        /// <summary>Kill leftover server/scraper processes (same exe paths) from a previous run before starting.</summary>
        public bool KillOrphansOnStart { get; set; } = true;

        /// <summary>Refuse to start a second QServer for the same server exe (named-mutex guard).</summary>
        public bool SingleInstance { get; set; } = true;
    }

    public sealed class ScraperOptions
    {
        /// <summary>Path to QServer.Scraper.exe; empty = search next to the app executable.</summary>
        public string Path { get; set; } = "";

        /// <summary>How often (ms) the scraper polls the server console screen buffer.</summary>
        public int PollMs { get; set; } = 50;

        /// <summary>How many bottom rows the scraper re-reads each poll to detect new lines.</summary>
        public int ReadbackLines { get; set; } = 1024;

        /// <summary>Console buffer width; wider means long lines are not wrapped into fragments.</summary>
        public int BufferWidth { get; set; } = 320;

        /// <summary>Console scrollback height; taller means fast spam wraps (and is lost) later.</summary>
        public int BufferHeight { get; set; } = 30000;

        /// <summary>Hide the server's console window (recommended; only QServer's UI is shown).</summary>
        public bool HideServerWindow { get; set; } = true;

        /// <summary>
        /// Earliest moment (ms after the server spawns) its console window may be hidden. The hide is ALSO
        /// gated on the server having produced its first output rows, because hiding it inside its fragile
        /// early-init window makes it exit. Raise this if starts are flaky. Optional key with a safe default,
        /// so older <c>qserver.json</c> files keep loading.
        /// </summary>
        public int HideDelayMs { get; set; } = 1500;

        /// <summary>
        /// How long (ms) the scraper may keep retrying to attach to the server console before it gives up
        /// AND kills the server. Cold or loaded machines need more than the old fixed 3s budget.
        /// </summary>
        public int AttachTimeoutMs { get; set; } = 15000;
    }

    public sealed class UiOptions
    {
        /// <summary>TUI render refresh interval in milliseconds.</summary>
        public int RefreshMs { get; set; } = 120;

        /// <summary>How many processed lines are kept in memory for scrollback.</summary>
        public int ScrollbackLines { get; set; } = 6000;

        /// <summary>Lines scrolled per mouse-wheel notch.</summary>
        public int WheelScrollLines { get; set; } = 3;

        /// <summary>Show the figlet splash screen on startup.</summary>
        public bool ShowSplash { get; set; } = true;

        /// <summary>Show the live "Server" strip (ServerName, GameType, Map, passwords, ...) captured from the console.</summary>
        public bool ShowServerInfo { get; set; } = true;
    }

    public sealed class RestartOptions
    {
        /// <summary>Automatically restart the server when it exits (QServer acts as a watchdog).</summary>
        public bool Enabled { get; set; } = false;

        public int InitialDelaySeconds { get; set; } = 3;
        public int MaxDelaySeconds { get; set; } = 60;
        public double Multiplier { get; set; } = 2.0;

        /// <summary>Give up after this many restarts inside <see cref="RestartWindowMinutes"/> (crash-loop guard).</summary>
        public int MaxRestartsPerWindow { get; set; } = 5;
        public int RestartWindowMinutes { get; set; } = 10;

        /// <summary>
        /// Grace (seconds) after the old server is confirmed dead before launching the next one. This lets the
        /// OS release the UDP port and the lobby drop the old session, so the replacement server does not
        /// collide with the one it is replacing. Optional key with a safe default, so older
        /// <c>qserver.json</c> files (which lack it) keep loading.
        /// </summary>
        public int RebindGraceSeconds { get; set; } = 2;
    }

    public sealed class NoiseOptions
    {
        /// <summary>Collapse consecutive identical lines (used by the <c>collapse</c> action).</summary>
        public bool DuplicateCollapse { get; set; } = true;

        /// <summary>Default throttle interval for rules that do not specify one.</summary>
        public int DefaultThrottleSeconds { get; set; } = 10;

        /// <summary>Sliding window (seconds) used to compute the spam-rate meter.</summary>
        public int SpamMeterWindowSeconds { get; set; } = 5;

        /// <summary>Ordered rules; the first one whose <see cref="NoiseRule.Match"/> matches a line wins.</summary>
        public List<NoiseRule> Rules { get; set; } = DefaultRules();

        /// <summary>Recommended defaults: hide classic startup spam, throttle runtime spam, highlight readiness.</summary>
        public static List<NoiseRule> DefaultRules() =>
        [
            // Keep passwords out of the shareable log; they still appear in the live Server panel (parsed separately).
            new() { Match = @"^--(Changed|Value of):\s*(Game|Admin)Password", Action = "hide" },
            // Startup / shutdown spam -> hidden from screen and from the QServer log.
            new() { Match = @"^\s*$", Action = "hide" },
            new() { Match = "^Loading xml file:", Action = "hide" },
            new() { Match = "^Loading assembly:", Action = "hide" },
            new() { Match = "^Assembly load result:", Action = "hide" },
            new() { Match = "^Unable to find item to add dependency", Action = "hide" },
            new() { Match = @"^Couldn't find \.dll:", Action = "hide" },
            new() { Match = "^LoadWithFullPath", Action = "hide" },
            new() { Match = "^Loading localized text xml:", Action = "hide" },
            new() { Match = "^opening ", Action = "hide" },
            new() { Match = "^reading .* xml files", Action = "hide" },
            new() { Match = "^file: ", Action = "hide" },
            new() { Match = "^Loading packages", Action = "hide" },
            new() { Match = "^Loading done", Action = "hide" },
            new() { Match = "Mono Loading Step::", Action = "hide" },
            new() { Match = "^Registering items", Action = "hide" },
            new() { Match = "^Initializing items", Action = "hide" },
            new() { Match = "^Creating module", Action = "hide" },
            new() { Match = "^Module Initialize", Action = "hide" },
            new() { Match = "^try_to_get_cache_save_privilege", Action = "hide" },
            new() { Match = "^Dumping resource usage", Action = "hide" },
            new() { Match = "^#(RT|DT|Data) textures", Action = "hide" },
            new() { Match = "^Deleting resources", Action = "hide" },
            new() { Match = @"^Messagebox \[ERROR\] message: Cannot load:", Action = "hide" },
            new() { Match = "Could not load file or assembly", Action = "hide" },
            new() { Match = "^Could not find the event index for:", Action = "hide" },
            new() { Match = "^Combat parameter added:", Action = "hide" },
            new() { Match = "reading combat parameters file", Action = "hide" },

            // Runtime spam -> throttled.
            new() { Match = "AliveMessage", Action = "throttle", ThrottleSeconds = 30, Severity = "info" },
            new() { Match = "^ClientRestSessionTask::", Action = "throttle", ThrottleSeconds = 30, Severity = "info" },
            new() { Match = "Handler not found for network message", Action = "throttle", ThrottleSeconds = 5, Severity = "warn", Meter = "spam" },
            new() { Match = "^error ", Action = "throttle", ThrottleSeconds = 5, Severity = "warn" },

            // Useful markers -> highlighted.
            new() { Match = "is ready! You can now enter console commands", Action = "highlight", Severity = "info" },
        ];
    }

    /// <summary>A single console-noise rule.</summary>
    public sealed class NoiseRule
    {
        /// <summary>Regular expression matched against each line (after its timestamp prefix is stripped).</summary>
        public string Match { get; set; } = "";

        /// <summary><c>throttle</c> | <c>hide</c> | <c>collapse</c> | <c>highlight</c> | <c>pass</c>.</summary>
        public string Action { get; set; } = "throttle";

        /// <summary>For <c>throttle</c>: minimum seconds between shown lines; null = use the default.</summary>
        public int? ThrottleSeconds { get; set; }

        /// <summary><c>info</c> | <c>warn</c> | <c>error</c> (colour).</summary>
        public string Severity { get; set; } = "info";

        /// <summary>Optional meter tag; <c>spam</c> feeds the spam-rate indicator.</summary>
        public string? Meter { get; set; }
    }

    public sealed class LoggingOptions
    {
        /// <summary>Write a filtered log file (the shown output). The server keeps its own full raw log regardless.</summary>
        public bool Enabled { get; set; } = true;

        public string Path { get; set; } = "logs/server.log";
        public int MaxFileSizeMb { get; set; } = 50;
        public int RetainedFiles { get; set; } = 14;

        /// <summary>
        /// Lifecycle/host event log (panel start, sweep kills, host start/pid/ready/exits/fails). This is the
        /// evidence trail for intermittent-failure reports, separate from the filtered server log above.
        /// null/empty = disabled. Optional key with a safe default, so older config files keep loading.
        /// </summary>
        public string? HostLogPath { get; set; } = "logs/host.log";
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Load the config from <paramref name="path"/>, or return defaults if it does not exist.</summary>
    public static QServerConfig Load(string path)
    {
        if (!File.Exists(path)) return new QServerConfig();
        var cfg = JsonSerializer.Deserialize<QServerConfig>(File.ReadAllText(path), JsonOpts) ?? new QServerConfig();
        cfg.ApplyBatOverride();
        return cfg;
    }

    /// <summary>Serialize the config (used to write the sample file).</summary>
    public void Save(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>If <see cref="ServerOptions.ArgsFromBat"/> is set, parse that .bat into ExePath/Args/WorkingDirectory.</summary>
    void ApplyBatOverride()
    {
        if (string.IsNullOrWhiteSpace(Server.ArgsFromBat)) return;
        if (TryParseBat(Server.ArgsFromBat!, out var exe, out var args, out var work))
        {
            Server.ExePath = exe;
            Server.Args = args;
            Server.WorkingDirectory = work;
        }
    }

    /// <summary>Best-effort parse of a launch .bat: the first <c>.exe</c> token is the exe, the rest is args.</summary>
    public static bool TryParseBat(string batPath, out string exePath, out string args, out string workDir)
    {
        exePath = ""; args = ""; workDir = "";
        if (!File.Exists(batPath)) return false;
        foreach (var raw in File.ReadAllLines(batPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("::") || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;
            int exeIdx = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx < 0) continue;
            string exeTok = line[..(exeIdx + 4)].Trim().TrimStart('.', '\\', '/', '"').Trim('"');
            workDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(batPath)) ?? "";
            exePath = System.IO.Path.IsPathRooted(exeTok) ? exeTok : System.IO.Path.Combine(workDir, exeTok);
            args = line[(exeIdx + 4)..].Trim();
            return true;
        }
        return false;
    }
}
