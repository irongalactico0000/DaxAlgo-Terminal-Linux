# Goal

Turn the four Vibe Quant lanes into explicitly governed representations, then define a fail-closed
path from their reviewed evidence to one canonical TradeIR artifact and eventual backtest admission.

# Status

Implemented for authoring and extended with an exact-hash, in-process synthetic QuoteL1 smoke test
for package-valid Typed Graph and combined TradeIR artifacts. The historical/worker-isolated
Backtest Studio handoff remains fail-closed and separate.

# Acceptance criteria

- Vibe Python and Declarative Rules have Vibe Quant-owned, versioned normative contracts.
- CSP distinguishes the Vibe Quant inert authoring profile from the external Point72 CSP API.
- Typed Graph continues to bind the installed DaxAlgo TradeIR schema and operator catalog.
- Candidate bindings expose normative authority, specification reference, semantic role, and lowering
  target without implying runtime compatibility.
- Combination produces a new hash-bound TradeIR artifact with source-candidate provenance; it never
  treats arbitrary Python or CSP as deterministically lowerable.
- Synthetic smoke action enables only for an exact, unchanged, package-valid active TradeIR
  artifact. Success additionally requires the synthetic-data, closed-target, and runtime gates that
  run after the click; other formats and historical runs remain disabled.

# Plan

1. Audit current prompts, validators, primary-source references, and backtest target/runtime seams.
2. Add versioned contract metadata and normative specifications for all four lanes.
3. Strengthen deterministic Vibe Python and Declarative Rules validation.
4. Add canonical TradeIR synthesis/provenance and honest preparation state.
5. Surface contract authority and preparation gates in Candidate UI.
6. Run focused tests, full affected suites, build, and compact-window visual QA.

# Blast radius

- `TradingTerminal.Core`: representation/provenance contracts only; no host or Avalonia dependency.
- `DaxAlgo.Codegen`: lane prompts, validators, synthesis coordination.
- `TradingTerminal.Settings` and Avalonia shell: candidate authority/preparation UX.
- Backtest projects: a bounded Core-owned runner seam and Engine implementation over the existing
  TradeIR evaluator/gateway/order-book/portfolio path.
- Documentation and JSON schemas: normative Vibe Quant lane specifications.

# Build filter

```bash
dotnet build src/linux/Tools/DaxAlgo.Codegen/DaxAlgo.Codegen.csproj
dotnet build src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj
```

# Tests

- Four-lane candidate generator/contract tests.
- Candidate authoring and restore UI tests.
- TradeIR validation/admission tests relevant to the exact synthesized target.
- Full app/headless regression after focused checks.

# Findings

- The four outputs are governed representations, not interchangeable runtime modules. Vibe Python,
  Declarative Rules, and CSP Events are source/review profiles; Typed Graph is the canonical
  DaxAlgo TradeIR candidate.
- Vibe Quant owns `vibe-quant/python-strategy/v1`,
  `vibe-quant/declarative-rules/v1`, and `vibe-quant/csp-authoring-profile/v1`. Point72 CSP
  compatibility remains explicitly unverified.
- Combining reviewed candidates requires another AI call and creates a fifth artifact. The
  synthesis receipt binds ordered source hashes/contracts, prompt/request hashes, target binding,
  synthesized hash, and provider/model identity.
- TradeIR package validation is not execution admission. Authoritative point-in-time data binding,
  target/operator capability admission, installed importer/runtime admission, and an exact-hash
  backtest receipt remain independent required gates.
- The existing Expert C# flow is a separate reimplementation path; it is not evidence that a
  generated or synthesized candidate was converted or backtested.

# Diff summary

- Added versioned authority/semantic-role metadata to four-lane candidate bindings.
- Added strict Vibe Python, Declarative Rules, and CSP authoring contracts; Typed Graph remains bound
  to the installed TradeIR package/catalog.
- Added a hash-bound reviewed-AI synthesis contract that returns a new package-validated TradeIR
  candidate and synthesis receipt without mutating its sources.
- Added the normative lane specification and closed Declarative Rules JSON Schema.
- Expanded the workflow documentation with the synthesis boundary, two-receipt distinction, locked
  backtest gates, and exact future user path.
- Added Candidate UI for per-lane generation phases, authority, explicit selection, and the separate
  combined TradeIR artifact. The UI reports real host-observable phases and elapsed time without a
  fake percentage or ETA.
- Added fail-closed stale-session recovery that restores the brief without making an AI request;
  the user explicitly presses `Regenerate 4 candidates` to start a fresh batch.
- Preserved the last completed candidate batch transactionally in memory while a replacement is in
  flight, so cancellation or shutdown cannot overwrite it with an empty batch.
- Added a Candidate-tab synthetic smoke command that recomputes persisted TradeIR identity, performs
  synthetic data and closed-target admission, and returns a normal report or path-addressed blocker.

# Verification

- Focused four-lane/synthesis tests passed **47/47** and the `DaxAlgo.Codegen` project built with
  **0 warnings / 0 errors**.
- Declarative Rules schema parsed successfully with `jq`; referenced lane-contract/schema files
  exist; documentation whitespace and targeted `git diff --check` checks passed.
- Avalonia app tests passed **59/59**, including exact active-editor hash invalidation for the
  synthetic smoke action.
- The isolated staged-tree headless regression passed **765**, skipped **6**, failed **0**
  (**771** total). This clean checkout excludes unrelated uncommitted tests in the shared workspace.
- Full solution build succeeded with **0 errors**; it retained two unrelated existing CS8604
  warnings in `DaxqIlLowerer.cs`.
- The simulated/offline terminal was restarted from the verified Debug build. Compact-window visual
  QA confirmed the Candidate tab exposes all four independently running agents and lane status.

# Risks / deferred

- Arbitrary Python and CSP cannot be truthfully lowered to TradeIR. AI synthesis is a new reviewed
  artifact, not a deterministic compiler proof.
- External CSP compatibility must remain unverified until a specific upstream revision and package
  are pinned and validated.
- The bounded TradeIR runtime and execution subset used by the synthetic smoke is required and is
  intentionally part of this change. Broader unrelated runtime work remains outside its scope.
- A synthesis receipt is not a historical backtest admission receipt. The synthetic smoke evidence
  explicitly records non-historical, non-worker-isolated execution.
- The combined fifth artifact is not yet persisted across an application restart; its source batch
  can be regenerated, but synthesis must then be run again.
