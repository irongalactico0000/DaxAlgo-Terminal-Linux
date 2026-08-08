# Goal

Implement Ultragoal G001: a host-owned, canonical research case and confirmed strategy-intent contract with family-aware completeness, hash-bound confirmation, app-visible review gating, exact four-lane handoff, and truthful non-runtime boundaries.

# Plan

- Extend the existing confirmed-candidate model with separate research-case and strategy-intent artifacts.
- Add topology-aware requirement templates, deterministic completeness questions, validation, canonical serialization, and hash binding.
- Integrate the review/confirmation result into the authoring view model and bind every four-lane request/result to the exact confirmed intent.
- Add directional and non-directional sentinel tests, documentation, and focused verification.

# Blast radius

- `TradingTerminal.Core` strategy-generation contracts and validators.
- `TradingTerminal.Settings` authoring review state and app-facing tests.
- `DaxAlgo.Codegen` request, prompt, candidate, and batch bindings for the confirmed-intent hash.
- Headless strategy contract tests and workflow documentation.
- No lowerer, runtime, broker, credential, or live-order behavior changes.

# Build filter

- Focused `StrategyCandidateV1Tests` and new research/intent contract tests.
- Focused Avalonia authoring workflow tests.
- `TradingTerminal.Mac.slnx` after focused checks pass.

# Tests

- Focused canonical intent and four-lane generator suite: 100 passed, 0 failed.
- Focused authoring review, restore/recovery, and TradeIR suite: 45 passed, 0 failed.
- Full headless suite: 818 passed, 6 process-isolation tests skipped, 0 failed. On macOS this was
  run with `TMPDIR=/private/tmp` so reparse-point security tests receive a real path rather than the
  `/var` alias.
- Full Avalonia app suite: 100 passed, 0 failed.

# Findings

- The previous first-chat action could start four implementation calls before the user confirmed strategy meaning.
- A shared prompt hash did not prove that four artifacts implemented the same confirmed strategy.
- Applying a four-lane batch cleared the semantic candidate and confirmation that should have remained its upstream authority.
- The product must keep internal hashes out of the primary review UI while still using them to reject stale or substituted implementation results.
- A governed extension needs an installed host registry owner; a syntactically valid but unowned
  extension identifier must fail closed.
- Expert C# compile approval is separate from strategy-intent approval. Returning to Strategy
  Builder or changing any script identity/content expires the pending registration review.
- The next executable milestone is a DaxAlgo-owned LangGraph service with direct native
  `akquant.run_backtest` and Point72 `csp.run` workers. FinanceManus is explicitly excluded, and
  Vibe-Trading/VibeQuant remain pinned reference sources rather than runtime harnesses.

# Diff summary

- Added the versioned research case, strategy-intent draft/confirmation, seven semantic stages, family/topology profiles, and canonical serialization.
- Added app state for a readable research and strategy review, local confirmation, persistence, invalidation, and downstream action gates.
- Changed candidate chat so it refines strategy meaning; four implementation calls require a separate confirmed action.
- Bound parallel generation, returned artifacts, and combined TradeIR synthesis to the exact
  canonical confirmed intent; stale, missing, substituted, or noncanonical bindings fail closed.
- Added a provider-dispatch preflight that replays confirmation against the exact canonical
  candidate, research case, classification, and reviewed draft. A self-hashed but incomplete or
  unsupported intent cannot reach a provider.
- Added role-scoped registry ownership for governed intent, requirement, and value extensions;
  preserved exact extension IDs across review edits, restore, and isolated answer stashes; and
  rejected inactive built-in requirements that contradict the selected topology/classification.
- Added an exact Core value-schema catalog, registry ownership for non-Core requirement/value
  schemas, and an explicit signal-only fill-handling `not_applicable` requirement.
- Closed the Expert C# compile/register consent escape by binding registration to the exact
  reviewed script identity and expiring review on mode or display-name changes.

# Verification

- Focused core/generator: 100 passed, 0 failed.
- Focused app/UX/recovery/TradeIR: 45 passed, 0 failed.
- `dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj
  --no-build --no-restore` with `TMPDIR=/private/tmp`: 818 passed, 6 skipped, 0 failed.
- `dotnet test tests/linux/TradingTerminal.App.Avalonia.Tests/TradingTerminal.App.Avalonia.Tests.csproj
  --no-build --no-restore`: 100 passed, 0 failed.
- Final `dotnet build TradingTerminal.Mac.slnx --no-restore`: passed with 0 warnings and 0 errors;
  an earlier cold build surfaced 2 existing nullable warnings in `DaxqIlLowerer.cs` outside this
  change.
- `.claude/context/gen-context-linux.sh --check`: current (56 projects, 1,104 files, 180,967 LOC).
- `git diff --check`: passed.

# Risks/deferred

- Behavioral equivalence is not claimed. This change proves shared confirmed-input binding and host validation, not equal execution traces.
- Deterministic lowering, historical evidence, paper qualification, promotion, and monitoring are later goals.
- The DaxAlgo-owned LangGraph, akquant, and Point72 CSP runtime is the next milestone; it is not
  implemented or silently simulated by this contract work.
- The rejected untracked FinanceManus/VibeQuant adapter prototype under `tools/strategy-agent/`
  is outside this task and must not be included in this change.
- Local authoring-session hashes provide integrity binding, not hostile-disk authentication. A
  process able to rewrite the complete session and every digest would require a separately keyed
  Keychain/HMAC receipt, which is deferred and not claimed here.
- No live execution path is permitted.
