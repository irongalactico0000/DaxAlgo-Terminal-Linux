# Goal

Make every four-lane generation result inspectable as soon as that individual lane finishes, while
keeping candidate selection, persistence, synthesis, and testing gated on the complete validated
four-lane batch.

# Plan

- Publish each coordinator-validated terminal lane result through the existing progress channel.
- Stage terminal results only in transient progress rows; never construct or persist a partial batch.
- Let the user select a live lane and read its exact generated source/JSON or blocking response.
- Put the selected final artifact preview directly beneath the committed two-by-two candidate grid.
- Make Expert C# mode visibly reversible and suppress the C# compile action for Python/JSON drafts.
- Recover one unambiguous JSON object, allow one validation-aware lane repair, and expose that repair
  as a real progress state.
- Distinguish restored candidate snapshots from newly generated results.
- Add regression tests for streaming, failure visibility, cancellation, atomic commit, and XAML UX.

# Blast radius

- `DaxAlgo.Codegen`: transient progress payload and coordinator emission only.
- `TradingTerminal.Settings`: transient lane-row inspection state; committed batch semantics unchanged.
- `TradingTerminal.App.Avalonia`: Candidate-tab layout and bindings.
- Headless/app tests and user-facing workflow documentation.

# Build filter

- `TradingTerminal.Tests.Headless` focused four-lane generator tests.
- `TradingTerminal.App.Avalonia.Tests` focused Candidate UX/session tests.
- Full affected test projects and `TradingTerminal.Mac.slnx` after focused checks pass.

# Tests

- Coordinator tests prove terminal results stream before `Task.WhenAll`, failures remain inspectable,
  and cancellation cannot leak a late result.
- View-model tests prove streamed results remain transient, raw invalid responses are not persisted,
  stopped generations are ignored, and a new session clears transient payloads.
- UI contract tests cover live/final exact previews, lane diagnostics, scrolling, accessibility names,
  reversible Expert C# navigation, and the non-C# compile boundary.

# Findings

- Today the coordinator has a validated lane result before `Task.WhenAll`, but terminal progress drops
  that result and exposes only a status string.
- The UI therefore cannot show source/JSON until all four requests finish and the complete batch is
  committed.
- The full-batch validator requires exactly four ordered lanes; partial results must remain transient
  and non-actionable.
- The prior mode toggle changed a Boolean without navigating tabs, so returning from Expert C# could
  leave the user looking at Code. Compile visibility also depended only on mode, not file type.

# Diff summary

- Added an optional validated lane result to generation progress without changing the complete-batch
  contract used by selection, persistence, synthesis, or testing.
- Added selectable live lane rows and exact read-only previews for generated source, canonical JSON,
  invalid raw responses, and code/path/message diagnostics.
- Added strict-parse-first recovery for one unambiguous embedded JSON object and one bounded repair
  request containing the original envelope plus exact validator diagnostics. Ambiguous multiple
  objects remain invalid; cancellation and provider failures remain fail-closed.
- Added Claude CLI root-object structured output for JSON candidate requests and a visible
  `REPAIRING RESPONSE` lane state.
- Added a restored-result banner and explicit fresh-generation action so saved invalid bytes cannot
  look like a new run under the current parser.
- Moved the committed selected-candidate preview directly under the two-by-two candidate grid and
  made its `PREVIEW ONLY` versus `ACTIVE IN EDITOR` state explicit.
- Made Expert C# mode return directly to the Candidate tab, preserved all candidate state, and hid
  and independently guarded `Compile & Register` whenever any editor file is not C#.
- Updated README/workflow documentation and regenerated the macOS source context.

# Verification

- Focused four-lane generator tests: 55 passed.
- Focused candidate/session/UI tests: 44 passed.
- Full headless suite: 773 passed, 6 skipped, 0 failed.
- Full Avalonia app suite: 94 passed, 0 failed.
- Full `TradingTerminal.Mac.slnx` build: succeeded with 0 warnings and 0 errors.
- macOS source context: current at 56 projects, 1,099 files, and 173,786 LOC.
- Architecture review: PASS; UI/accessibility review: PASS.

# Risks/deferred

- Raw invalid model responses are useful for immediate diagnosis but must remain transient and must
  continue to be removed from persisted session snapshots.
- This change exposes generation output; it does not make any lane executable or backtest-ready.
