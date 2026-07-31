# Windows UI parity screenshots and profile completion
Status: complete

## Goal
Capture the Windows Professional and Avalonia shells in matching states, close the highest-value visual differences in the Mac product, and add every applicable Windows launch/runtime profile while keeping concrete strategies external.

## Plan
1. Inventory profile/configuration parity and deterministic launch states.
2. Capture and inspect matched Windows/Avalonia screenshots.
3. Implement bounded shell/theme/profile parity changes.
4. Re-capture and verify the complete Mac graph.

## Blast radius
- Destination shell/theme/configuration/tests and `tmp/ui-parity/` evidence.
- Windows repositories are read-only comparison inputs explicitly authorized by the request.
- No strategy implementations and no external actions.

## Build filter
- Focused `TradingTerminal.App.Avalonia` and app tests first; final `TradingTerminal.Mac.slnx`.

## Tests
- Profile inventory/schema parity.
- Avalonia app/theme tests.
- Full headless/app test suites and named solution build.
- Matched-state screenshot comparison.
- Context regeneration/checks after routed source changes.

## Findings
- Matched account, empty-shell, connected-simulated, and support-window captures were taken from the current Windows Pro and Avalonia binaries.
- Account-gate content was already source-equivalent; native window chrome remains platform-owned.
- The largest shell gaps were the crowded top-level menu, missing recorder/count chips, disconnected DevSim state, support layout, static theme resources, and compact strategy-card structure.
- All eight Windows development modes already had destination-local configuration files, but only one had a launch entry and the Mac startup path ignored environment fallback, plugin-disable, bypass, and broker auto-connect controls.

## Diff summary
- Added the six Pro profiles plus shared DevSim and DevReplay modes; removed WSL; the all-strategies label is intentionally `installed plugins` and no concrete strategies were added.
- Added ASP.NET environment fallback, `DisableStrategyPlugins`, and Windows-equivalent bypass/auto-connect behavior.
- Matched the Professional title, nine-menu hierarchy, API/REC/plugin chips, simulated banner, catalog header, empty-state hero, activity/status behavior, compact three-column card layout, and support dialog.
- Converted theme-facing shell/control references to dynamic resources so open windows follow palette changes.
- Added profile/startup and shell-visual contract coverage to the Avalonia app tests.

## Verification
- `TradingTerminal.App.Avalonia` build: passed, 0 warnings / 0 errors.
- `TradingTerminal.App.Avalonia.Tests`: 7/7 passed.
- Matched 1240x700 shell and 650x750 support screenshots visually inspected; evidence is under `tmp/ui-parity/{baseline,after}/`.
- `TradingTerminal.Mac.slnx`: passed, 0 warnings / 0 errors.
- `TradingTerminal.Tests.Headless`: 599/599 passed.
- Context regenerated at 55 projects / 1034 files / 143689 LOC; structural `check` and byte-for-byte `deep-check` passed.

## Risks / deferred
- Native macOS font metrics, title-bar chrome, and Retina scaling still require final Mac hardware verification.
- A notification-dispatcher double-dispose was observed only when the automation requested a graceful shutdown; it is outside this UI/profile slice and remains deferred.
