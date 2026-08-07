# Goal

Make the four Vibe Quant lane execution boundaries independently inspectable, exercise the supported
native validation/test paths in the app workflow, record an honest Done/Not Done matrix, and publish
the result without absorbing unrelated working-tree changes.

# Plan

- Audit the existing four-lane generation, validation, inspection, and exact-hash smoke boundaries.
- Correct the selected-lane readiness stages so source-review validation is not mislabeled as a
  failed TradeIR package check.
- Add app-facing regression coverage for all four boundary sequences.
- Record Done/Not Done claims and run focused generation/app tests plus the solution build.

# Blast radius

- `TradingTerminal.Settings` selected-candidate readiness presentation only.
- Avalonia authoring tests, four-lane workflow documentation, and this task record.
- No provider protocol, generated artifact bytes, package validator, runtime, broker, data ingestion,
  or live order-execution behavior changes.

# Build filter

- Focused `ParallelStrategyCandidateGeneratorV1Tests`.
- Focused `TradeIrBacktestAuthoringTests` and `CandidateAuthoringUxContractTests`.
- `TradingTerminal.Mac.slnx` after focused checks pass.

# Tests

- Prove every source-review lane reports its real deterministic validator as passed.
- Prove every source-review lane names its first missing lowerer/importer and locked runtime.
- Preserve the existing Graph exact-hash package/smoke tests and the four independently inspectable
  generation results.

# Findings

- Generation already produces four independently inspectable, hash-bound artifacts and preserves
  provider/parse/repair/validation failure stages.
- Graph already has the only installed executable test path: package validation plus the narrow
  exact-hash in-process QuoteL1 EMA smoke.
- The readiness panel incorrectly represented a validated Vibe/Rules/CSP artifact as immediately
  missing a TradeIR package, hiding the lane-native validator that actually passed.

# Diff summary

- Source-review readiness now reports the exact generated hash, the real lane-native validator that
  passed, the first missing deterministic lowerer/importer, and the locked native runtime/test stage.
- Graph retains its separate installed TradeIR package and exact-hash QuoteL1 EMA smoke stages.
- Added three app-facing boundary tests and an evidence-dated four-lane Done/Not Done matrix.
- Documented the family-neutral strategy lifecycle. Directional jump/overheat entry/exit is one
  specialization; pairs, rotation, market-making, arbitrage, execution, hedging, derivatives, event,
  and model-driven strategies use different decision details under the same abstract slots.

# Verification

- Focused Avalonia authoring workflow: 24 passed, 0 failed, 0 skipped.
- Focused four-lane generator: 69 passed, 0 failed, 0 skipped.
- `TradingTerminal.Mac.slnx`: build succeeded, 0 errors, 2 pre-existing nullable warnings in
  `DaxqIlLowerer.cs`.
- `git diff --check`: passed.
- App QA is deterministic view-model/XAML workflow coverage with the real validators and synthetic
  runner seam; no paid live-provider call was made.
- Context freshness check remains Not Done for this patch: the upstream commit already contains
  generated context from additional source files that are absent from its own clean tree, so a clean
  regeneration reports repository-wide drift unrelated to this five-file change. No generated
  context was overwritten.

# Risks/deferred

- Native Python, deterministic Rules lowering, CSP runtime/import, and historical worker backtesting
  remain intentionally Not Done and are not simulated by this presentation correction.
- The clean verification worktree needed an explicit solution restore before the solution build;
  the initial `--no-restore` failure was missing `project.assets.json`, not a source/build failure.
