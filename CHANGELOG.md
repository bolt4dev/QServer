# Changelog

All notable changes to QServer are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-07-22

### Fixed
- Closing the panel (window **X**, `Ctrl+C`, `taskkill`, or a crash) could leave the real server running as an orphan.
  Children now live inside `KILL_ON_JOB_CLOSE` job objects (panel → scraper → server), so the kernel reaps the whole
  tree however the panel dies; the scraper also watches the panel and kills the server on every abnormal path.
- Flaky starts caused by leftover orphan servers holding the UDP port / lobby session (the "close and reopen once or
  twice" workaround). The panel now sweeps matching leftovers at startup and refuses duplicate instances per server exe.

### Added
- Startup-failure visibility: critical lines are never hidden before the ready sentinel, the raw console tail is shown
  on a failed start, and non-zero exit codes get plain-English explanations.
- Lifecycle log at `logs/host.log` recording spawn / attach / ready / exit / kill evidence, separate from the filtered
  log.
- New config keys: `server.killOrphansOnStart`, `server.singleInstance`, `server.startupWarnSeconds`,
  `scraper.attachTimeoutMs`, `scraper.hideDelayMs`, `restart.rebindGraceSeconds`, `logging.hostLogPath`.
- Verification scripts `tests/kill-matrix.ps1` (no survivor after any way of closing the panel) and
  `tests/rapid-cycle.ps1` (back-to-back headless starts must each reach ready and leave nothing behind).

### Changed
- Restart now verifies the old server is actually dead and waits `restart.rebindGraceSeconds` before the next start, so
  the replacement does not collide with the process it replaces.
- The scraper's console-attach budget is configurable (`scraper.attachTimeoutMs`, was a hard-coded 3 s), and hiding the
  server window is delayed and gated on its first output (`scraper.hideDelayMs`) to avoid killing fragile early init.

## [1.0.0] - 2026-07-11

### Added
- Initial public release.
- Runs the Bannerlord dedicated server hidden in the background with a Spectre.Console TUI in front.
- Console-noise control (hide / throttle / collapse / highlight) driven entirely by `qserver.json`.
- Scrollable log (mouse wheel / PgUp / PgDn / Home / End) with follow-vs-scroll modes.
- Live "Server" strip showing settings captured from the console (ServerName, GameType, Map, passwords, ...).
- Command line to the server plus meta-commands (`:stop`, `:restart`, `:info`, `:clear`, `:quit`).
- Auto-restart watchdog with exponential backoff and a crash-loop guard.
- Modular architecture: portable `QServer.Core` with `IServerHost` / `IConsoleUi` extension seams.

### Security
- Default noise rules keep `AdminPassword` / `GamePassword` out of the filtered log file (they still appear in the
  live Server strip).
