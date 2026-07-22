# Hide-timing experiment

> Requires a real `DedicatedCustomServer.Starter.exe` (a full Bannerlord install); it cannot be
> automated with FakeServer. Hiding the window is a visual behaviour — the unit test only verifies
> the delayed-hide PATH (the `@CTRL HIDDEN` telemetry). Whether hiding causes an early exit can only
> be measured against a real server; this protocol describes that measurement.

## Goal

Measure whether the TIMING of hiding the server window is correlated with the sporadic early exit
(startup needing 1-2 retries). Hypothesis: hiding the window inside B's fragile early-init window can
close it prematurely; `scraper.hideDelayMs` plus the first-output gate defers the hide to the safe
moment where B has both cleared the delay and written its first output rows.

## The triple to record (source: `logs/host.log`)

On every run the panel writes UTC-stamped event lines to `logs/host.log`. The relevant lines:

| Field | Line | Meaning |
|-------|------|---------|
| Attach | `@CTRL READYHOST ... attach_ms=<n>` *(scraper stderr)* | Time to attach to B's console. This line does not reach host.log (the panel reduces READYHOST to a payload-less Ready event); approximate it with the `HOST START` → `READY after` delta. |
| Hide | `DIAG HIDDEN after_ms=<n> rows=<n>` | The moment the window was hidden: ms since spawn, and how many rows B had produced by then. |
| Exit | `HOST EXIT code=<c> (<description>) sentinel=<yes\|no>` | The run's exit code and whether the READY sentinel was reached. |

Supporting lines: `HOST START`, `SERVER PID <pid>`, `READY after <n>s`, and (only when startup fails)
the raw console tail `TAIL <line>`.

## Protocol

1. On the VDS, with `scraper.hideDelayMs = 1500` (default) and `scraper.hideServerWindow = true` in
   `qserver.json`:
   **20 times in a row**, open the panel → once `READY` appears, close it with `:stop` then `:quit`.
2. For each run, collect the triple from host.log; in particular record the `DIAG HIDDEN after_ms/rows`
   and `HOST EXIT ... sentinel=` lines.
3. **Success criterion:** `sentinel=yes` on 20/20 runs AND no early exit — i.e. no run shows an
   unexpected `HOST EXIT` (a close before reaching READY) around the `HIDDEN` moment.

## If an early exit is seen (escalation)

1. Set `scraper.hideDelayMs = 3000` and repeat the 20 runs.
2. If it still happens, **control group:** 20 runs with `scraper.hideServerWindow = false`.
   - If the early exit DISAPPEARS with hiding off → the cause is the hide itself: raise `hideDelayMs`
     further, or re-evaluate the hide on the native side.
   - If the early exit PERSISTS with hiding off → the cause is independent of hiding: use the
     `HOST EXIT` description in host.log and the failed startup's `TAIL` lines (the panel's
     "STARTUP FAILED" dump) to report the real cause.

## Results

To be filled in on a session with VDS access. The code side is complete for this task: the hide is
now gated on the delay plus the first-output condition, and every hide moment is reported via the
`@CTRL HIDDEN after_ms/rows` telemetry.

| # | attach_ms | HIDDEN after_ms | HIDDEN rows | EXIT code | sentinel |
|---|-----------|-----------------|-------------|-----------|----------|
| 1 | | | | | |
| … | | | | | |
| 20 | | | | | |
