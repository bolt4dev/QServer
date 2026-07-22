// QServer.Scraper (helper process): server host + console bridge.
// It launches the server (B) itself with CREATE_NEW_CONSOLE, so it becomes B's PARENT -> AttachConsole works
// reliably (sibling attach does not; parent->child does, proven by the Faz 0 probe).
// It reads B's console SCREEN BUFFER incrementally and forwards lines to its OWN stdout (to the wrapper A).
// It injects commands received on its OWN stdin into B via WriteConsoleInput (CONIN$).
// Control/diagnostics go to stderr as "@CTRL PID <n>", "@CTRL EXIT <c>", "@CTRL READYHOST ...",
// "@CTRL KILLED <reason>", "@CTRL FAIL <code> <reason>".
//
// LIFECYCLE CONTRACT: B must never outlive this process. Every abnormal exit below kills B first, and a
// kill-on-close job object is the kernel backstop for the paths no code can catch (crash, Task Manager).
//
// Exit codes:
//    0 = server exited itself        2 = server exe not found        3 = CreateProcess failed
//    4 = attach timeout (B killed)   5 = console handles unavailable (B killed)
//   10 = watched parent died (B killed)                             11 = stdin EOF / stdout broken (B killed)
//
// Usage: QServer.Scraper.exe --server-exe <p> --server-args "<a>" --server-cwd <d>
//        [--hide] [--hide-delay-ms 1500] [--poll-ms 50] [--readback 1024] [--parent-pid <n>] [--attach-timeout-ms 15000]

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint CREATE_NEW_CONSOLE = 0x00000010, STILL_ACTIVE = 259;
    const uint SYNCHRONIZE = 0x00100000, INFINITE_WAIT = 0xFFFFFFFF;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const int JobObjectExtendedLimitInformation = 9;
    const ushort KEY_EVENT = 0x0001;
    const int SW_HIDE = 0;
    static readonly IntPtr INVALID = new(-1);

    static StreamWriter _toA = null!;   // stdout -> captured lines
    static StreamWriter _ctrl = null!;  // stderr -> control/diagnostics
    static StreamReader _fromA = null!; // stdin  -> commands

    static int Main(string[] argv)
    {
        string serverExe = "", serverArgs = "", serverCwd = "";
        bool hide = false;
        int pollMs = 50, readback = 1024, targetHeight = 30000, targetWidth = 320;
        int parentPid = 0, attachTimeoutMs = 15000, hideDelayMs = 1500;
        for (int i = 0; i < argv.Length; i++)
            switch (argv[i])
            {
                case "--server-exe": serverExe = argv[++i]; break;
                case "--server-args": serverArgs = argv[++i]; break;
                case "--server-cwd": serverCwd = argv[++i]; break;
                case "--hide": hide = true; break;
                case "--poll-ms": pollMs = int.Parse(argv[++i]); break;
                case "--readback": readback = int.Parse(argv[++i]); break;
                case "--buffer-height": targetHeight = int.Parse(argv[++i]); break;
                case "--buffer-width": targetWidth = int.Parse(argv[++i]); break;
                case "--parent-pid": parentPid = int.Parse(argv[++i]); break;
                case "--attach-timeout-ms": attachTimeoutMs = int.Parse(argv[++i]); break;
                case "--hide-delay-ms": hideDelayMs = int.Parse(argv[++i]); break;
            }

        // Channels to talk to A: AttachConsole may reset std handles, so capture the ORIGINAL handles first.
        _toA = new StreamWriter(new FileStream(new SafeFileHandle(GetStdHandle(STD_OUTPUT_HANDLE), false), FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
        _ctrl = new StreamWriter(new FileStream(new SafeFileHandle(GetStdHandle(STD_ERROR_HANDLE), false), FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
        _fromA = new StreamReader(new FileStream(new SafeFileHandle(GetStdHandle(STD_INPUT_HANDLE), false), FileAccess.Read), new UTF8Encoding(false));

        if (!File.Exists(serverExe)) { Ctrl($"ERROR: server exe not found: {serverExe}"); return 2; }

        // --- launch B (VISIBLE; hiding at spawn causes early exit -> the hide is deferred to the main loop) ---
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        string cmd = $"\"{serverExe}\" {serverArgs}";
        if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NEW_CONSOLE, IntPtr.Zero, serverCwd, ref si, out var pi))
        { Ctrl($"ERROR: CreateProcess winerr={Marshal.GetLastWin32Error()}"); return 3; }
        // Anchor the hide delay to B's SPAWN (not attach): B initializes with a VISIBLE console, and hiding it
        // inside its fragile early-init window makes it exit, so the actual hide is deferred to the main loop.
        long spawnTick = Environment.TickCount64;
        bool hidden = !hide;   // hiding disabled -> already "done"; the loop's hide check never fires
        uint pid = (uint)pi.dwProcessId;
        Ctrl($"PID {pid}");

        CaptureInKillOnCloseJob(pi.hProcess);
        WatchParent(parentPid, pi.hProcess);

        // --- attach to B's console (parent->child; retry until the budget runs out) ---
        // Cold or loaded machines can take several seconds to get B's console up, so the budget is a
        // wall-clock deadline the caller sets, not a fixed number of tries.
        bool attached = false;
        int attachErr = 0;   // most-recent AttachConsole failure code; captured before GetExitCodeProcess clobbers it
        long deadline = Environment.TickCount64 + attachTimeoutMs;
        var attachSw = Stopwatch.StartNew();
        while (Environment.TickCount64 < deadline)
        {
            FreeConsole(); // detach from any inherited/stale console before each attempt
            if (AttachConsole(pid)) { attached = true; break; }
            // Snapshot AttachConsole's real error NOW: the GetExitCodeProcess call below succeeds and would
            // overwrite the thread's last-error, so a later Marshal.GetLastWin32Error() would report 0, not
            // the attach failure the VDS operator actually needs to see.
            attachErr = Marshal.GetLastWin32Error();
            if (GetExitCodeProcess(pi.hProcess, out uint ec) && ec != STILL_ACTIVE)
            { ClaimNormalShutdownOrWait(); Ctrl($"EXIT {ec}"); return 0; }
            Thread.Sleep(100);
        }
        // Giving up while B runs would leave an invisible server holding the UDP port and the lobby
        // session, so the next start fails for a reason the user cannot see. Take B with us.
        if (!attached)
            return Fail(pi.hProcess, 4, $"attach timeout after {attachTimeoutMs}ms winerr={attachErr}");

        IntPtr hOut = CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        IntPtr hIn = CreateFile("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hOut == INVALID || hIn == INVALID)
            return Fail(pi.hProcess, 5, $"console handles unavailable winerr={Marshal.GetLastWin32Error()}");

        // Grow the scrollback buffer (best-effort): taller so fast spam wraps later, and WIDER so long lines
        // (e.g. "Couldn't find .dll: ...") stay on ONE row instead of wrapping into fragments.
        if (GetConsoleScreenBufferInfo(hOut, out var info0))
            SetConsoleScreenBufferSize(hOut, new COORD
            {
                X = (short)Math.Max((int)info0.dwSize.X, targetWidth),
                Y = (short)Math.Min(targetHeight, short.MaxValue - 1)
            });

        Ctrl($"READYHOST attached pid={pid} attach_ms={attachSw.ElapsedMilliseconds} poll={pollMs}ms readback={readback} hide={hide}");

        // Command injection thread.
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = _fromA.ReadLine()) is not null)
                {
                    if (line == "@@stop")
                    {
                        // A deliberate stop: B's own exit code is what the main loop reports, so claim the
                        // shutdown up front - otherwise the stdin EOF that usually follows would re-label
                        // this clean run as an abnormal 11.
                        ClaimNormalShutdown();
                        try { TerminateProcess(pi.hProcess, 0); } catch { }
                        continue;
                    }
                    InjectCommand(hIn, line);
                }
            }
            catch (Exception ex) { TryCtrl($"cmd thread ended: {ex.Message}"); }

            // Falling out of the loop means A closed (or broke) our stdin: the panel is gone. Historically
            // this thread just ended silently and B was left running forever.
            if (ClaimAbnormalShutdown()) FailAndExit(pi.hProcess, 11, "stdin closed (panel gone)");
        })
        { IsBackground = true }.Start();

        // Main read loop.
        var emittedTail = new List<string>(readback + 64);
        try
        {
            while (true)
            {
                if (GetExitCodeProcess(pi.hProcess, out uint code) && code != STILL_ACTIVE)
                { DrainOnce(hOut, emittedTail, readback); ClaimNormalShutdownOrWait(); Ctrl($"EXIT {code}"); break; }
                if (!GetConsoleScreenBufferInfo(hOut, out _)) { ClaimNormalShutdownOrWait(); Ctrl("EXIT -1"); break; }
                DrainOnce(hOut, emittedTail, readback);
                // Deferred window hide: fire once B has BOTH survived the configured delay AND produced its
                // first output rows - empirically the earliest point where hiding does not trip its fragile
                // early-init and kill it. Fires exactly once (guarded by `hidden`); A is alive here, so Ctrl.
                if (!hidden && Environment.TickCount64 - spawnTick >= hideDelayMs &&
                    GetConsoleScreenBufferInfo(hOut, out var hinfo) && hinfo.dwCursorPosition.Y > 0)
                {
                    IntPtr hwnd = GetConsoleWindow();
                    if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
                    hidden = true;
                    Ctrl($"HIDDEN after_ms={Environment.TickCount64 - spawnTick} rows={hinfo.dwCursorPosition.Y}");
                }
                Thread.Sleep(pollMs);
            }
        }
        catch (IOException)
        {
            // A closed its end of our stdout pipe, i.e. the panel is gone. No control message will be read
            // any more; all that matters is that B does not survive us.
            KillServer(pi.hProcess, "stdout pipe broken (panel gone)");
            TryCtrl("FAIL 11 stdout pipe broken (panel gone)");
            return 11;
        }

        CloseHandle(hOut); CloseHandle(hIn); FreeConsole();
        return 0;
    }

    // ---- lifecycle ----

    const int Running = 0, NormalShutdown = 1, AbnormalShutdown = 2;
    const int ExitHandoffMs = 5000;
    static int _shutdown;
    static int _killed;

    /// <summary>
    /// Records that B's exit is expected (it exited on its own, or <c>@@stop</c> terminated it), so the
    /// abnormal paths stay quiet. Returns false when an abnormal path already owns the exit code.
    /// </summary>
    static bool ClaimNormalShutdown() =>
        Interlocked.CompareExchange(ref _shutdown, NormalShutdown, Running) != AbnormalShutdown;

    /// <summary>Claims the exit for one abnormal path; later claimants (and normal ones) lose.</summary>
    static bool ClaimAbnormalShutdown() =>
        Interlocked.CompareExchange(ref _shutdown, AbnormalShutdown, Running) == Running;

    /// <summary>
    /// The main loop's version of <see cref="ClaimNormalShutdown"/>. An abnormal path kills B before it
    /// calls <see cref="Environment.Exit"/>, so the main loop always observes B's death in that tiny
    /// window; without parking here it would return 0 from Main and overtake the intended 10/11. The wait
    /// is bounded so a claimant that dies mid-exit cannot hang the scraper for ever.
    /// </summary>
    static void ClaimNormalShutdownOrWait()
    {
        if (!ClaimNormalShutdown()) Thread.Sleep(ExitHandoffMs);
    }

    /// <summary>
    /// Terminates B exactly once, whichever path gets there first (parent watchdog, command thread, main
    /// loop). Idempotent, because several of those can fire concurrently.
    /// </summary>
    static void KillServer(IntPtr hProcess, string reason)
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        try { TerminateProcess(hProcess, 0); } catch { /* already dead or handle refused */ }
        TryCtrl($"KILLED {reason}");
    }

    /// <summary>
    /// Kills B, reports the reason and ends the process. Used by the background threads, which cannot
    /// return an exit code and must not rely on the main loop (it may be blocked on a dead stdout pipe).
    /// </summary>
    static void FailAndExit(IntPtr hProcess, int code, string reason)
    {
        KillServer(hProcess, reason);
        TryCtrl($"FAIL {code} {reason}");
        Environment.Exit(code);
    }

    /// <summary>Same as <see cref="FailAndExit"/> for failures on the main thread, which returns the code itself.</summary>
    static int Fail(IntPtr hProcess, int code, string reason)
    {
        KillServer(hProcess, reason);
        TryCtrl($"FAIL {code} {reason}");
        return code;
    }

    /// <summary>
    /// Puts B in a job object that the kernel empties when the last handle to it closes - i.e. when THIS
    /// process dies, for any reason at all, including ones no catch block sees. The panel installs an
    /// equivalent job over this process, but only after its CreateProcess returns, so a fast scraper could
    /// otherwise spawn B outside it; this closes that window from the inside.
    /// <para>
    /// Deliberate duplication of <c>QServer.Hosting.KillOnCloseJob</c>: the scraper must stay
    /// dependency-free, so it cannot reference QServer.Core or the panel assembly.
    /// </para>
    /// </summary>
    static void CaptureInKillOnCloseJob(IntPtr hProcess)
    {
        // NOTE: the job handle is deliberately never closed while we run - it dying WITH this process is
        // the entire mechanism. It is also non-inheritable (null attributes), so B never gets a copy that
        // would keep the job alive after we are gone.
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { Ctrl($"job object unavailable winerr={Marshal.GetLastWin32Error()}; relying on cooperative kill only"); return; }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr mem = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(limits, mem, false);
            // Assigning to a job whose limits were not set would silently degrade the guarantee to nothing,
            // so only assign once the flag is in place, and say so when either step fails.
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, mem, (uint)len) ||
                !AssignProcessToJobObject(job, hProcess))
                Ctrl($"job object unavailable winerr={Marshal.GetLastWin32Error()}; relying on cooperative kill only");
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    /// <summary>
    /// Watches the panel by pid and takes B down with it. Without this the scraper only notices a dead
    /// panel when it next touches a broken pipe, which for a quiet server may be never.
    /// </summary>
    static void WatchParent(int parentPid, IntPtr hProcess)
    {
        if (parentPid <= 0) return;   // 0 = no parent to watch (standalone / test runs)
        new Thread(() =>
        {
            IntPtr hp = OpenProcess(SYNCHRONIZE, false, (uint)parentPid);
            if (hp == IntPtr.Zero)
            {
                // Already gone or not openable; the stdin-EOF path remains as the fallback signal.
                TryCtrl($"parent pid {parentPid} not observable winerr={Marshal.GetLastWin32Error()}");
                return;
            }
            WaitForSingleObject(hp, INFINITE_WAIT);
            if (ClaimAbnormalShutdown()) FailAndExit(hProcess, 10, "parent process exited");
        })
        { IsBackground = true }.Start();
    }

    // ---- console mirroring ----

    static void DrainOnce(IntPtr hOut, List<string> emittedTail, int readback)
    {
        if (!GetConsoleScreenBufferInfo(hOut, out var info)) return;
        int width = info.dwSize.X, endRow = info.dwCursorPosition.Y;
        if (endRow <= 0) return;
        int startRow = Math.Max(0, endRow - readback);
        var window = ReadRows(hOut, startRow, endRow, width);
        var fresh = DiffNew(emittedTail, window);
        foreach (var l in fresh) _toA.WriteLine(l);
        if (fresh.Count > 0)
        {
            emittedTail.AddRange(fresh);
            int overflow = emittedTail.Count - (readback + 64);
            if (overflow > 0) emittedTail.RemoveRange(0, overflow);
        }
    }

    // Return only the NEW lines: drop the prefix of "window" that overlaps the tail of what we already emitted.
    static List<string> DiffNew(List<string> tail, List<string> window)
    {
        int maxM = Math.Min(window.Count, tail.Count), overlap = 0;
        for (int m = maxM; m >= 1; m--)
        {
            bool ok = true; int baseT = tail.Count - m;
            for (int j = 0; j < m; j++) if (window[j] != tail[baseT + j]) { ok = false; break; }
            if (ok) { overlap = m; break; }
        }
        return window.GetRange(overlap, window.Count - overlap);
    }

    static List<string> ReadRows(IntPtr hOut, int startRow, int endRow, int width)
    {
        var list = new List<string>(endRow - startRow);
        var buf = new char[width];
        for (int y = startRow; y < endRow; y++)
        {
            var coord = new COORD { X = 0, Y = (short)y };
            list.Add(ReadConsoleOutputCharacter(hOut, buf, (uint)width, coord, out uint read)
                ? new string(buf, 0, (int)read).TrimEnd() : "");
        }
        return list;
    }

    static void InjectCommand(IntPtr hIn, string cmd)
    {
        string seq = cmd + "\r";
        var recs = new INPUT_RECORD[seq.Length * 2];
        int idx = 0;
        foreach (char c in seq) { recs[idx++] = KeyRec(c, true); recs[idx++] = KeyRec(c, false); }
        WriteConsoleInput(hIn, recs, (uint)recs.Length, out _);
    }

    static INPUT_RECORD KeyRec(char c, bool down) => new()
    { EventType = KEY_EVENT, Key = new KEY_EVENT_RECORD { bKeyDown = down ? 1 : 0, wRepeatCount = 1, UnicodeChar = c } };

    static void Ctrl(string m) => _ctrl.WriteLine("@CTRL " + m);

    /// <summary>
    /// Best-effort control message. The failure paths run precisely when A may already be gone, and an
    /// unhandled broken-pipe exception on a background thread would replace the exit code we mean to report.
    /// </summary>
    static void TryCtrl(string m) { try { Ctrl(m); } catch (IOException) { /* A is gone; nobody to tell */ } }

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X; public short Y; }
    [StructLayout(LayoutKind.Sequential)] struct SMALL_RECT { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct CONSOLE_SCREEN_BUFFER_INFO { public COORD dwSize; public COORD dwCursorPosition; public ushort wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }
    // Blittable (int/ushort only) so the INPUT_RECORD[] marshals as a flat copy for WriteConsoleInput.
    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD Key; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEY_EVENT_RECORD { public int bKeyDown; public ushort wRepeatCount, wVirtualKeyCode, wVirtualScanCode; public ushort UnicodeChar; public uint dwControlKeyState; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    // Same layout as QServer.Hosting.KillOnCloseJob (x64: LimitFlags at offset 16, UIntPtr for the
    // SIZE_T/ULONG_PTR fields, 144-byte extended struct). Copied, not shared, so the scraper stays dependency-free.
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                     ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleScreenBufferSize(IntPtr h, COORD size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ReadConsoleOutputCharacter(IntPtr h, [Out] char[] buf, uint len, COORD coord, out uint read);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] recs, uint len, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
}
