# Candidate fidelity and smoke guidance

Status: Complete

## Goal

Make Candidate-tab smoke-test guidance distinguish an invalid/unsupported Typed Graph from a
package-valid graph whose compatibility with the narrow synthetic target is not yet proven. Keep
explicit strategy clauses mandatory in every generation prompt and document why the five-minute
momentum starter cannot enter the installed runner.

## Plan

- Derive guidance from the previewed/chosen lane and the latest smoke-admission result.
- Name the installed runner's QuoteL1 EMA scope and provide a shortcut to its known starter.
- Surface the Graph blocker even while a non-Graph source-review draft is previewed.
- Harden prompt fidelity for explicit user rules and correct the momentum starter's exposure axis.
- Add focused ViewModel and XAML contract tests.

## Blast radius

`DaxAlgo.Codegen` prompt contracts, `TradingTerminal.Settings` authoring ViewModel/catalog, the
Avalonia authoring window, focused tests, and workflow documentation. No runner, compiler, package,
or execution semantics.

## Build filter

`tests/linux/TradingTerminal.App.Avalonia.Tests/TradingTerminal.App.Avalonia.Tests.csproj`

## Tests

- Focused Candidate/ViewModel contract filter: 14 passed.
- Focused four-lane prompt/generator tests: 47 passed.
- Focused starter-catalog tests: 28 passed.
- Full `TradingTerminal.App.Avalonia.Tests` project: 63 passed.
- Full isolated-commit headless suite: 765 passed, 6 skipped.
- Full isolated-commit solution build: 0 errors; 2 existing nullable warnings in `DaxqIlLowerer.cs`
  and 4 existing async-without-await warnings in `SimulatedBrokerClientTests.cs`.

## Findings

The existing catalog already contains a one-click QuoteL1 EMA smoke-compatible starter. The
Candidate outcome copy nevertheless directs every result toward the smoke action, and the
ViewModel calls every active package-valid graph "ready" before closed-target admission runs.

## Diff summary

- Replaced the unconditional Graph-to-smoke instruction with state-specific availability copy.
- Kept package validation distinct from closed-target smoke admission and surfaced the rejection
  diagnostic after an exact-hash run.
- Added a Candidate-panel action that loads the existing QuoteL1 EMA smoke starter into the composer.
- Kept the selected Vibe/Rules/CSP runtime boundary visible while also reporting the batch Graph's
  exact failure code and message.
- Required model output defaults to preserve every explicit direction, threshold, lookback, filter,
  exit, sizing, and timing clause; Vibe trailing stops must maintain ratcheted state.
- Marked empty Spec/Graph template arrays as invalid completed output and prohibited invented Graph
  semantics when the installed manifest cannot express the request.
- Classified the upside-only momentum starter as long-only and documented its missing OHLCV/ATR
  runner capabilities.
- Added XAML and ViewModel tests for invalid, unsupported, package-valid/unproven, and
  smoke-incompatible states.

## Verification

```text
PATH="$HOME/.dotnet:$PATH" dotnet test \
  tests/linux/TradingTerminal.App.Avalonia.Tests/TradingTerminal.App.Avalonia.Tests.csproj \
  --filter "FullyQualifiedName~TradeIrBacktestAuthoringTests|FullyQualifiedName~CandidateAuthoringUxContractTests" \
  --no-restore
Passed: 14, Failed: 0, Skipped: 0

PATH="$HOME/.dotnet:$PATH" dotnet test \
  tests/linux/TradingTerminal.App.Avalonia.Tests/TradingTerminal.App.Avalonia.Tests.csproj \
  --no-restore
Passed: 63, Failed: 0, Skipped: 0

PATH="$HOME/.dotnet:$PATH" dotnet test \
  tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj \
  --filter FullyQualifiedName~ParallelStrategyCandidateGeneratorV1Tests --no-restore
Passed: 47, Failed: 0, Skipped: 0

PATH="$HOME/.dotnet:$PATH" TMPDIR="$PWD/.tmp/test-tmp" dotnet test \
  tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj \
  --no-build --no-restore
Passed: 765, Failed: 0, Skipped: 6

PATH="$HOME/.dotnet:$PATH" dotnet build TradingTerminal.Mac.slnx --no-restore
Succeeded: 0 errors, 2 pre-existing CS8604 warnings, 4 pre-existing CS1998 warnings

git diff --check -- <changed product/test files>
Passed (no output)
```

## Risks/deferred

The smoke runner remains the authority for exact target compatibility; the UI does not duplicate
its Engine-owned admission rules. No project/source-path changes were made, so context regeneration
was not required. Prompt fidelity is still not deterministic semantic equivalence. The installed
catalog/runner still cannot execute the five-minute OHLCV/volume/ATR momentum brief. Existing
unrelated dirty-tree content was left untouched.
