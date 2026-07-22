# Contributing to QServer

Thanks for your interest in improving QServer! It's a small, focused tool, so contributions of any size are welcome.

By participating, you agree to abide by the [Code of Conduct](CODE_OF_CONDUCT.md).

## Getting started

```powershell
git clone https://github.com/bolt4dev/QServer.git
cd QServer
dotnet build QServer.sln -c Debug
```

The app project (`src/QServer`) references the scraper project only for build ordering and copies
`QServer.Scraper.exe` next to the app output, so a plain `dotnet build` gives you a runnable layout in
`src/QServer/bin/Debug/net8.0/win-x64/`.

## Testing without a live server

The noise pipeline is fully testable offline with a deterministic virtual clock:

```powershell
# synthetic spam -> should show a large display reduction
QServer.exe --synthetic

# replay a real captured log through the current rules
QServer.exe --replay path\to\server.log
```

`--run-sec <n>` runs the live mode headless (no TUI) and prints a summary, which is handy for quick end-to-end checks.

## Automated tests

```powershell
dotnet test src/QServer.Tests -c Debug
```

The suite (xunit) drives real processes through a `FakeServer` stand-in (`tools/FakeServer`) — it
does **not** need a Bannerlord install — and covers the process-lifecycle guarantees: kill-on-close
job objects, the scraper's abnormal-exit kills, orphan sweeping, restart death-verification, and the
exit-code plumbing. If you touch anything under `src/QServer.Scraper`, `ScraperHost`,
`ShutdownGuard`, `OrphanSweeper`, or the `AppEngine` supervisor, run it. Tests spawn real processes,
so the suite runs single-threaded (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`).

Two PowerShell scripts under `tests/` (`kill-matrix.ps1`, `rapid-cycle.ps1`) verify end-to-end that
closing the panel any way leaves no orphaned server — see the comments at the top of each for setup.

> `logs/server.log` is the filtered output. The default rules keep `AdminPassword`/`GamePassword` out of it, but if
> you changed the noise rules, scrub the file before attaching it to an issue or PR.

## Guidelines

- Target framework is **.NET 8**, platform is **Windows x64** (the console-capture layer is Win32-specific).
- Keep the code **English-only** and match the existing style (see `.editorconfig`). 4-space indent, file-scoped namespaces.
- The Win32 interop structs that back `ReadConsoleInput` / `WriteConsoleInput` must stay **blittable** (int/ushort, no `bool`/`char`), or keyboard/mouse input silently corrupts.
- New user-facing behaviour should be **configurable** via `qserver.json` rather than hard-coded.
- Please describe how you tested your change in the PR (a `--replay`/`--synthetic` result or a short live run is great).

## Ideas / good first issues

- A cross-platform capture path (Linux dedicated server).
- A small rule editor / live rule reload.
- Packaging as a single self-contained executable.
