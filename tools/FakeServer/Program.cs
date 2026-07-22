// FakeServer: a stand-in for DedicatedCustomServer.Starter.exe in lifecycle tests.
//
// The real server needs a full Bannerlord install, so the lifecycle tests cannot use it. This tool
// reproduces only the traits those tests care about: it writes to its own console like the real
// server, can hold a UDP port (to simulate the port-conflict startup failures orphans cause), can
// spawn a grandchild process (to prove job-object kills reach the whole tree), prints the ready
// sentinel the scraper looks for, then heartbeats forever until something kills it.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

const int BindFailedExitCode = 42;
const int BadArgumentsExitCode = 2;

int readyAfterMs = 500, bindUdp = 0, spawnChildAfterMs = 0;
bool spawnChild = false, isChild = false;
int? exitCode = null;

for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--ready-after-ms": readyAfterMs = ParseValue(ref i); break;
        case "--bind-udp": bindUdp = ParseValue(ref i); break;
        case "--spawn-child": spawnChild = true; break;
        // Task 1 needs the grandchild to appear only after a job object has been assigned to us,
        // so the delay form implies the spawn.
        case "--spawn-child-after-ms": spawnChildAfterMs = ParseValue(ref i); spawnChild = true; break;
        case "--child": isChild = true; break;
        // Exit with a specific (possibly negative) code shortly after becoming ready, so the
        // faithful-exit-code test can drive a large NTSTATUS-style code through the real C -> A path.
        case "--exit-code": exitCode = ParseValue(ref i); break;
        default:
            // Silently ignoring an unknown flag would turn a typo in a test into a mysterious
            // "the child never died" failure, so fail loudly instead.
            Console.Error.WriteLine($"FAKESERVER: unknown argument '{args[i]}'");
            return BadArgumentsExitCode;
    }

// The grandchild: no ports, no sentinel, just proof-of-life until killed.
if (isChild)
{
    while (true)
    {
        Console.WriteLine($"[child] alive {DateTime.UtcNow:HH:mm:ss}");
        Thread.Sleep(1000);
    }
}

UdpClient? sock = null;
if (bindUdp > 0)
{
    try
    {
        sock = new UdpClient(new IPEndPoint(IPAddress.Any, bindUdp));
    }
    catch (SocketException)
    {
        Console.Error.WriteLine("FAKESERVER: bind failed");
        return BindFailedExitCode;
    }
}

if (spawnChild)
{
    // Deliberately inline rather than on a background thread: the ready sentinel then always
    // follows CHILDPID, so tests get one deterministic ordering to wait on.
    if (spawnChildAfterMs > 0) Thread.Sleep(spawnChildAfterMs);
    var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child") { UseShellExecute = false });
    Console.WriteLine($"CHILDPID {child!.Id}");
}

for (int i = 0; i < 20; i++) Console.WriteLine($"Loading xml file: fake_{i}.xml");
Thread.Sleep(readyAfterMs);
Console.WriteLine("Custom Server is ready! You can now enter console commands (Enter 'list' ...).");

// Deliberate self-exit for the faithful-exit-code test. Waiting ~400ms past the sentinel lets the scraper
// reach its MAIN read loop and emit "@CTRL EXIT {(uint)n}", rather than catching the exit during attach.
if (exitCode is int ec)
{
    Thread.Sleep(400);
    Environment.Exit(ec);
}

while (true)
{
    Console.WriteLine($"Http post request(TaleWorlds.Diamond.Rest.AliveMessage) is successful {DateTime.UtcNow:HH:mm:ss}");
    // Holds the UDP binding open for as long as we run; anything after this loop is unreachable.
    GC.KeepAlive(sock);
    Thread.Sleep(1000);
}

int ParseValue(ref int i)
{
    string flag = args[i];
    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int value))
    {
        // Same contract as the unknown-argument path: a clear stderr line and exit code 2, not
        // an unhandled exception. A bare `return` here would only return from this local
        // function, not the top-level program, so terminate the process directly instead.
        Console.Error.WriteLine($"FAKESERVER: '{flag}' needs an integer value.");
        Environment.Exit(BadArgumentsExitCode);
        return default; // unreachable: Environment.Exit terminates the process immediately.
    }
    i++;
    return value;
}
