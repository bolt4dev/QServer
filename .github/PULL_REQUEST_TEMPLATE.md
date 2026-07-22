<!-- Thanks for contributing! Keep this short. -->

## What this changes

<!-- One or two sentences. Link the issue it closes, e.g. "Closes #12". -->

## Why

<!-- The problem or motivation. -->

## How I tested it

<!-- A --replay / --synthetic result, a short live run, or the tests you ran. -->

## Checklist

- [ ] `dotnet build QServer.sln -c Release` is clean (0 warnings)
- [ ] `dotnet test src/QServer.Tests -c Debug` passes
- [ ] No orphaned server / scraper processes left after a manual run
- [ ] Updated `CHANGELOG.md` if this is user-visible
- [ ] Updated `README.md` / `qserver.sample.json` if config or behaviour changed
- [ ] New config keys are optional with safe defaults (existing `qserver.json` files keep working)
- [ ] Code is English-only and matches `.editorconfig`; any `ReadConsoleInput`/`WriteConsoleInput` structs stayed blittable
