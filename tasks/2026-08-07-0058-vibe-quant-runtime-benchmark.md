# Goal

Benchmark the four Vibe Quant lanes against primary implementations and product documentation, then
turn that evidence into a decision-complete authoring, validation, data-binding, testing, historical
backtesting, results, and iteration workflow for DaxAlgo Terminal.

# Plan

1. Clone and pin the open-source references and record URL, commit, and license evidence.
2. Trace every reference from authoring through validation, data, execution, progress, results,
   iteration, and provenance; use official documentation for proprietary references.
3. Separate evidence from product inference and map the findings to each current Vibe Quant lane.
4. Specify the unified exact-hash lifecycle, UI gates, runtime/importer seams, failure states,
   phased delivery, and acceptance tests.
5. Run an independent professor-critic review, update the documentation index, verify the diff, and
   publish the result to the existing GitHub pull request.

# Blast radius

- Research and product contracts: `docs/research/`, `docs/README.md`, the two Vibe Quant workflow/
  lane-contract documents, and this task record.
- The paired host-owned generation task contains the compact-response and conservative repair-routing
  source/test changes; this report does not claim the historical runtimes are implemented.
- External repositories are read-only research clones under `/private/tmp/vq-reference-repos` and
  are not vendored or committed.
- No runtime, broker, market-data, UI, project, or generated context source is changed.

# Build filter

The research itself is documentation, but the same delivery includes the paired Codegen contract
fix. Run focused generator tests, the Codegen build, affected/full headless and Avalonia tests, then
the full macOS solution build in addition to Markdown/source-link checks.

# Tests

- Verify every cloned repository URL, exact HEAD commit, and license file.
- Verify cited repository paths and line anchors against the pinned clones.
- Verify local DaxAlgo citations against the current source tree.
- Run the autoresearch professor-critic completion gate.
- Run `git diff --check` and inspect the intentional Git scope.

# Findings

- The four outputs are authoring dialects, not interchangeable engines. Native simulations may show
  P&L, but only identity/deterministically lowered TradeIR under the same data/run/engine contracts
  is canonically comparable.
- Shared instrument/schema/time facts must be confirmed before the four model calls; run range,
  capital, costs, fills, benchmark, and seed belong to a separate immutable run configuration.
- Graph is the first direct historical path. Rules needs a host-owned Draft-to-Resolved transition
  and deterministic lowerer. Python and CSP need isolated native runtimes plus constrained,
  deterministic lowerers before joining a same-engine leaderboard.
- Parameter proposals require exact typed node/port bindings and child module hashes before sweeps.
  Repeatability compares a deterministic economic-result digest, not receipts containing job/time/
  machine telemetry.
- The current Parquet protocol does not prove instrument identity; historical admission needs a
  trusted, hash-bound data/ingestion manifest.

# Diff summary

- Added a primary-source benchmark of vectorbt, backtesting.py, Freqtrade/FreqUI, NautilusTrader,
  LEAN, and Point72 CSP at pinned revisions, plus official Composer and Capitalise.ai evidence.
- Defined the fail-closed generation, facts, native preview, deterministic conversion, historical
  admission, progress, results, experiment, provenance, and UI state machines.
- Defined the phased delivery order and measurable stop conditions through four-lane canonical
  comparison, with an explicit Graph+Rules fallback if constrained Python/CSP profiles are rejected.

# Verification

- Primary-source audit: 16 local links and 38 pinned GitHub source links checked; 0 missing paths,
  SHA mismatches, or invalid line anchors.
- Focused generation contract tests: 69 passed, 0 failed, 0 skipped.
- Full headless suite: 787 passed, 0 failed, 6 platform-process tests skipped.
- Avalonia application tests: 94 passed, 0 failed, 0 skipped.
- `DaxAlgo.Codegen` build: succeeded with 0 warnings and 0 errors.
- Full `TradingTerminal.Mac.slnx` build: succeeded with 0 errors and 2 pre-existing nullable warnings
  in `DaxqIlLowerer.cs`.
- macOS source context regenerated: 56 projects, 1,099 files, 174,375 LOC.
- Code review and source/document audit: PASS. The independent professor-critic verdict is recorded
  when the completion gate passes.

# Risks/deferred

- Composer and Capitalise.ai are proprietary; only their primary public product documentation can
  be inspected, and source-level claims must remain explicitly unavailable.
- Reference code licenses and architecture do not imply that code can be copied into DaxAlgo.
- The report will specify implementation phases; it will not falsely claim that missing Python,
  Rules, CSP, or historical TradeIR runtimes have been implemented by documentation alone.
