# Native Strategy Agent .NET seam

## Goal

Add the smallest non-UI .NET integration for the proven Python strategy-agent service: a dedicated
loopback process host, typed HTTP client, dependency-injection registration, and focused tests.

## Plan

1. Add dedicated strategy-agent options and a loopback-only managed host on a port separate from
   the existing daxalgo-ml sidecar.
2. Add typed request/response records and client methods for research sessions, messages,
   confirmation, run start/cancel/status, bounded ordered event polling, and retained artifact
   retrieval.
3. Register the host and client in Infrastructure and the Avalonia composition root without wiring
   any view model or changing the existing authoring workflow.
4. Add focused HTTP, host-resolution, and DI tests; run the narrow suite and required repository
   checks.

## Blast radius

- `TradingTerminal.Infrastructure` only for the process/client implementation.
- Avalonia composition and non-secret defaults only for registration.
- `TradingTerminal.Tests.Headless` only for focused integration tests.
- No Python, strategy semantics, validator, simulator, broker, or UI changes.

## Build filter

`TradingTerminal.Tests.Headless` filtered to the new StrategyAgent tests first, followed by the
Infrastructure project and required headless suite if time permits.

## Tests

- `dotnet test ...TradingTerminal.Tests.Headless.csproj --filter FullyQualifiedName~StrategyAgent
  --no-restore`: **12 passed, 0 failed** after adding event pagination, artifact retrieval, and an
  outer-timeout regression.
- `dotnet build ...TradingTerminal.App.Avalonia.csproj --no-restore`: **passed, 0 warnings,
  0 errors**.
- `dotnet build TradingTerminal.Mac.slnx --no-restore`: **passed, 0 errors**; two existing nullable
  warnings remain in `DaxqIlLowerer.cs`.
- Full headless suite: **826 passed, 2 failed, 6 skipped**. Both failures are unrelated existing
  macOS `/var` reparse-path expectations in `ExpertContextRetrievalTests` and
  `WorkerClientTests.Real_worker_executes_exact_engine_from_active_immutable_bundle`.
- `jq empty appsettings.json` and `git diff --check`: passed.

## Findings

- The existing daxalgo-ml sidecar owns port 8765; the strategy agent now defaults to the dedicated
  loopback port 8766 and verifies the exact `daxalgo-native-strategy-agent` health identity.
- The real Python CLI shape is `python -m daxalgo_strategy_agent.cli serve --port <port>` (or the
  equivalent packaged executable with `serve --port`).
- The typed client now preserves the service's event-page `has_more` value, sends an explicit
  validated `limit` (1..500), and retrieves a retained run artifact by relative path with its
  encoding, size, and SHA-256 evidence.
- The client timeout now defaults to 300 seconds, which is greater than the Python research-stage
  timeout of 180 seconds. The previous 120-second client default could abort a still-valid research
  response before the Python service's actionable timeout.
- The Python service requires separately pinned FinanceManus, VibeQuant/AKQuant, and CSP paths.
  Dedicated non-secret .NET options forward those exact runtime paths without adding a strategy
  validator, DSL, simulator, or substitute execution engine.
- There is no existing self-contained Python release-packaging convention for this app. Copying
  `tools/strategy-agent` source without its Python 3.12 dependency closure would not be runnable, so
  this change supports configured, user-installed, app-resource, and Debug source-tree resolution
  but deliberately does not claim source copying as packaging.
- The first test build exhausted disk space. `uv cache clean` removed 3.1 GiB of recoverable UV
  cache only; no repository, retained proof, or user file was removed.

## Diff summary

- Added `TradingTerminal.Infrastructure.StrategyAgent`: options, managed process host, typed JSON
  contracts, exact API exception, typed lifecycle/event/artifact client, and DI registration.
- Registered the disabled-by-default backend in the Avalonia composition root and non-secret
  defaults; no view model, screen, or existing authoring workflow was changed.
- Added focused route/serialization/error, health identity, runtime-resolution, and DI tests.
- Regenerated `.claude/context/linux` after the Infrastructure and retained-run UI additions:
  56 projects, 1115 indexed files, 183736 LOC.

## Verification

The .NET backend seam and app composition compile, and all twelve focused tests pass. The generated
macOS context is current. Repository-wide headless verification is not fully green because of the
two pre-existing macOS temporary-path/reparse failures listed above; neither touches this seam.
`manage-context.ps1 check/deep-check` could not run because neither `powershell` nor `pwsh` is
installed in this environment.

## Risks/deferred

- Release packaging is only in scope if an existing self-contained Python packaging convention can
  be reused. Copying source without its pinned Python 3.12 dependencies would be misleading and is
  not sufficient packaging.
- Avalonia UX wiring is tracked separately in `2026-08-09-0145-native-strategy-run-ui.md`; that
  slice now consumes retained runs but does not add chart/research/confirmation creation.
- The service is disabled by default until local or packaged paths and provider configuration are
  supplied. Its HTTP client and process host are registered and ready for later UI consumption.
