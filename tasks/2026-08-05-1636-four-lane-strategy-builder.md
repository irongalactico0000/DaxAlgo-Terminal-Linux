# Status

Implemented and locally verified for four parallel AI-generated authoring artifacts.

One user prompt now starts four model requests concurrently. Every parsed artifact is preserved with
its exact request/candidate hashes. A structurally valid Vibe Python, Declarative Spec, or CSP Python
artifact is selectable as `Generated`; a valid Typed Graph artifact additionally passes the installed
TradeIR package validator and is `PackageValid`. No lane claims compilation, package tests, backtest,
import, or execution.

# Goal

Convert one user strategy prompt into four independently inspectable, editable alternatives inside
the existing Strategy Authoring terminal:

```text
User prompt
├─ VibeAgent  → editable ordinary Python
├─ SpecAgent  → declarative strategy JSON
├─ GraphAgent → canonical typed TradeIR JSON
└─ CspAgent   → editable CSP Python
```

Stop at strategy generation and deterministic authoring validation. Reuse the existing provider,
editor, package, test, and runtime systems; add no broker, venue, SDK, or execution behavior.

# Implemented behavior

- The fixed Vibe, Spec, Graph, and CSP agents all call the selected AI provider once and concurrently.
- Results always return in Vibe, Spec, Graph, CSP display order, independent of completion order.
- Each prompt includes an exact host-owned generation binding, candidate id, request hash, lane-native
  contract, assumptions/questions/parameters/flexibility metadata, and proposed-test requirements.
- Every candidate and edited artifact receives a canonical SHA-256 content hash.
- Provider failures remain isolated as `Failed`; malformed provider output is `Invalid`; one bad lane
  does not erase selectable sibling candidates.
- Caller cancellation is checked before fan-out and after all lanes join. Cooperative and
  non-cooperative agents cannot turn a canceled request into a successful batch, and live rows report
  `Canceled` rather than mislabeling Stop as a provider failure.
- Selection is exact-hash only and makes no additional model, compiler, import, test, or execution call.
- Local editor revalidation creates a new hash and reruns the same deterministic lane checks without
  calling the model.

# Lane contracts and honest readiness

| Lane | AI request | Artifact contract | Successful status | Package validator |
|---|---:|---|---|---|
| Vibe Python | Yes | `strategy.py`: `PARAMETERS`, `initialize_state()`, `on_event(event, state, parameters)` | `Generated` / selectable / not package-validated | None registered |
| Declarative Spec | Yes | `strategy.spec.json`: `declarative-strategy/v1` with strategy, parameters, data, indicators, entry/exit, and risk sections | `Generated` / selectable / not package-validated | None registered |
| Typed Graph | Yes | canonical `OperatorGraphModuleV1` in `strategy.tradeir.json` using only the installed operator manifest | `PackageValid` / selectable / not tested | `TradeIrModuleValidatorV1` |
| CSP Python | Yes | `strategy.csp.py`: `import csp`, `@csp.node`, `@csp.graph`, `ts[...]`, and no `csp.run` | `Generated` / selectable / not package-validated | None registered |

The Python/CSP checks are deterministic authoring-shape checks, not Python parsing, sandboxing,
dependency proof, or runtime proof. The declarative check proves required JSON sections and identity,
not that a lowerer exists. TradeIR package validity does not prove data binding, target admission,
package tests, backtest success, or execution readiness.

# UI behavior

- The authoring view says it is making four AI generation calls.
- While generation is active, a two-by-two live task board names VibeAgent, SpecAgent, GraphAgent,
  and CspAgent and reports each real request as waiting, generating, finished, or needing attention.
- Candidate cards show `PACKAGE VALID · NOT TESTED`, `GENERATED · NOT PACKAGE-VALIDATED`,
  `GENERATED · INVALID`, or a distinct provider-failure state.
- All structurally valid candidates can be previewed, chosen, and loaded into the existing editor.
- Choosing a generation-only artifact explicitly reports that no package validator/importer is
  registered and that nothing was tested or run.
- Invalid candidates retain their artifact and issue code/path for repair but cannot be selected.
- An above-fold outcome panel summarizes selectable and blocked lanes, shows the first blocked lane's
  concise issue code and exact path, and keeps the current backtest blocker visible before selection.

## Candidate decision UX

- All four lane results render together in a two-by-two card grid.
- The card being inspected carries a violet `PREVIEW` outline/badge; the exact artifact loaded into
  the editor independently carries a green `ACTIVE IN EDITOR` badge. Both states can coexist.
- The primary action says `Use selected in editor`, `Replace active candidate`, or
  `Using this candidate` from the actual selected/chosen hashes.
- The active lane is named in plain language; its full SHA-256 remains visible only as secondary
  provenance.
- Starting four-lane generation opens the Candidate tab. The generator emits transient
  `Queued → Running → Completed/Failed` events for each real provider request, so the UI shows a
  truthful `n/4 lanes finished` counter without inventing model percentages.
- Four-lane generation is the primary/default mode. Expert Code remains available through a small
  secondary mode action instead of an ambiguous on/off pill.
- Legacy saved sessions that omitted the lane-mode field migrate to four-lane generation on restore;
  explicit saved values remain preserved.
- Stopping a four-lane turn marks every queued/running lane `Canceled` before advancing the generation
  epoch. Late provider callbacks and results remain ignored and cannot repopulate candidates or usage.
- The Graph prompt names the closed `assetClass` wire tokens (`equity`, `future`, `forex`, `crypto`,
  `option`, `index`) and explicitly rejects plural `futures`, matching the package validator contract.
- Restoring a batch created under an older prompt/validation contract keeps its chat and editor files,
  discards stale candidate proofs, opens Candidate, and shows an explicit fresh-generation recovery.

## Strategy discovery

- New Strategy resets to a fresh identity and shows 22 curated, editable starter briefs instead of
  three oversized prompt pills.
- Every starter is backed by the canonical `StrategySpec` axes. Search and filters expose overlapping
  family, holding-horizon, and information/data lenses; cards also show topology and regime metadata.
- The catalog deliberately does not claim to enumerate every strategy. Named families are overlapping
  navigation lenses over objective, hypothesis, trigger, horizon, topology, exposure, information,
  model, construction, risk, execution, state, and adaptation axes.
- `New strategy` now creates a real fresh-session boundary: it saves the outgoing chat, deselects its
  rail row, and clears the draft, catalog filters, workbench tab, token usage, transient tasks, review
  state, diagnostics, and candidate state before restoring the editor template.

## Backtest readiness

- A selected artifact now shows four explicit gates: generated artifact, package validation,
  importer/runtime, and backtest target.
- `Backtest not ready` remains disabled for every current generated lane. This is intentional: no
  candidate binding has a registered importer, and TradeIR package validation alone does not perform
  data binding or target admission.
- The current runnable authored route is explicit in the outcome panel: switch to Expert C#, compile
  and register the reviewed `IBacktestStrategy`, then use Quick backtest in the strategy catalog or
  Tools → Backtest Studio. That route is separate from the generated candidate hashes.
- A future backtest action must bind the exact selected/revalidated hash to a registered importer and
  concrete runnable handle before it can open Backtest Studio.

# Main changed files

- `src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyGenerationContractsV1.cs`
- `src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyGenerationPromptV1.cs`
- `src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyCandidateGeneratorV1.cs`
- `src/linux/Tools/DaxAlgo.Codegen/StrategyGenerationPackageCatalogV1.cs`
- `src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs`
- `src/linux/UI/TradingTerminal.Settings/Authoring/StrategyStarterCatalog.cs`
- `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/StrategyAuthoringWindow.axaml`
- `src/linux/Shell/TradingTerminal.App.Avalonia/Settings/StrategyAuthoringWindow.axaml.cs`
- `tests/linux/TradingTerminal.Tests.Headless/Strategies/ParallelStrategyCandidateGeneratorV1Tests.cs`
- `tests/linux/TradingTerminal.App.Avalonia.Tests/MacPackagingConfigurationTests.cs`
- `tests/linux/TradingTerminal.App.Avalonia.Tests/AuthoringSessionMigrationTests.cs`
- `tests/linux/TradingTerminal.App.Avalonia.Tests/CandidateAuthoringUxContractTests.cs`
- `tests/linux/TradingTerminal.App.Avalonia.Tests/CandidateRestoreRecoveryTests.cs`
- `tests/linux/TradingTerminal.App.Avalonia.Tests/StrategyStarterCatalogTests.cs`
- `docs/vibe-quant-four-lane-workflow.md`

The ViewModel/XAML files contain earlier authoring work in the pre-existing dirty tree; unrelated
changes were preserved.

# Verification

- Focused four-lane generator tests: 32 passed, 0 failed, 0 skipped.
- Strategy namespace: 195 passed, 0 failed, 0 skipped.
- Full dirty-worktree headless suite using a workspace `TMPDIR`: 918 passed, 0 failed, 6 skipped.
- Clean synthetic checkout of the exact publish scope: 669 headless passed, 0 failed, 6 skipped;
  53 Avalonia app tests passed, 0 failed.
- Avalonia app tests in the dirty working tree before isolation: 52 passed, 0 failed, 0 skipped.
- Full dirty-worktree `TradingTerminal.Mac.slnx` build: succeeded with 0 warnings and 0 errors.
- Clean publish-scope `TradingTerminal.Mac.slnx` build: succeeded with 0 errors and 6 baseline warnings
  (2 CS8604 warnings in `DaxqIlLowerer.cs`; 4 CS1998 warnings in `SimulatedBrokerClientTests.cs`).
- Codegen project: succeeded with 0 warnings and 0 errors.
- macOS source context regenerated: 56 projects, 1,137 files, 184,010 LOC.
- `git diff --check`: passed; only pre-existing line-ending notices were reported.

# Manual terminal trial

Run the Debug simulated/offline profile while preserving the shell PATH so installed agent CLIs are
visible:

```bash
/Users/kimsunghyun/.dotnet/dotnet run \
  --project src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj \
  -c Debug --no-restore \
  --launch-profile "Dev — Simulated (offline)" \
  -- --bypass-login
```

Open Strategy Studio → Vibe Code → Vibe Quant, create a strategy, keep `4 AI strategy lanes`, choose
an available provider/model, enter a prompt, and press `Check & generate`. The Candidate tab should
show four results. Provider authentication and live model behavior are external to the deterministic
test suite.

The updated terminal was relaunched in the simulated/offline profile. A starter was selected and a
real four-lane request was submitted manually; the live Candidate task board showed all four agents
generating their distinct artifacts.

The redesigned composer was also rendered at the window's default compact size (`1060×768`) and its
declared minimum (`1000×680`). Mode, Expert switch, model, Build, Reasoning, and Send remained visible;
secondary controls wrap instead of sliding underneath the workbench. Workspace-launch buttons now wrap
inside a bounded footer instead of clipping the Codex shortcut at compact widths.

# Next phase: Vibe Quant tool and visualization integration

The next phase is intentionally separate from this candidate-selection fix: inventory the terminal's
research, data, charting, backtest, validation, and visualization tools; define their permissions and
typed inputs/outputs; then expose them through Vibe Quant as reviewable tool calls and visual artifacts.
That phase must preserve the current boundary: generating or inspecting a strategy never silently
authorizes import, backtest, broker access, registration, or execution.

# Deferred / risks

- No package importer/runtime/validator is registered yet for Vibe Python, Declarative Spec, or CSP.
- Structural generation checks do not prove Python syntax, module safety, or executable semantics.
- Proposed parameters and variation axes are comparison metadata; binding them into every artifact is
  not yet semantically proven.
- Prompt fidelity is human-reviewable but not fully machine-provable; an unrelated structurally valid
  candidate can still pass its lane checks.
- Actual provider/model identity is recorded in `AgentRun` but is not yet included in the candidate
  content hash.
- A live provider trial can still fail because of CLI authentication, model JSON compliance, timeout,
  or unavailable data/operator facts. Those failures are reported per lane and do not authorize any
  execution.
- The UI ignores late output after Stop, but the one-shot CLI adapter does not yet provide a proven
  process-tree termination guarantee; do not treat Stop as hard subprocess cancellation.
