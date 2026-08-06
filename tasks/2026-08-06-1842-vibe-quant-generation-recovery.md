# Goal

Make four-lane Vibe Quant generation preserve the original strategy brief across follow-up requests,
accept model-authored typed parameter defaults without weakening the closed candidate contract, remove
the CSP package-binding prompt trap, and present generation and backtesting as distinct user actions.

# Plan

1. Trace the composer-to-generation request, candidate-envelope parser, lane prompts, and smoke-backtest UI.
2. Implement the smallest coherent fixes with deterministic regression coverage.
3. Update user-facing workflow documentation and this evidence record.
4. Run focused tests, full headless/app tests, solution build, context regeneration, and diff review.
5. Restart the simulated/offline terminal, commit, push to the existing feature branch, and update its draft PR.

# Blast radius

- `DaxAlgo.Codegen`: shared four-lane request/envelope parsing and lane prompt construction.
- `TradingTerminal.Settings`: Vibe Quant session context, candidate actions, and backtest affordance.
- Headless and Avalonia tests covering generation and UI behavior.
- Vibe Quant workflow documentation only; no broker, live-feed, or live-execution path.

# Build filter

- `src/linux/Tools/DaxAlgo.Codegen/DaxAlgo.Codegen.csproj`
- `src/linux/UI/TradingTerminal.Settings/TradingTerminal.Settings.csproj`
- `TradingTerminal.Mac.slnx`

# Tests

- Focused four-lane generator contract tests: 49 passed, 0 failed.
- Focused restore/backtest/UX tests: 43 passed, 0 failed.
- Full Avalonia app tests: 87 passed, 0 failed.
- Full headless tests: 767 passed, 0 failed, 6 platform-dependent worker tests skipped.
- Full `TradingTerminal.Mac.slnx` build: succeeded with 0 warnings and 0 errors.

# Findings

- The latest production batch persisted `userPrompt: "gow to backtest"`; the view model passed only the
  current composer text to all four agents, replacing the original momentum-breakout brief.
- Vibe and Spec emitted numeric outer-envelope parameter defaults (`20`) while the host required strings.
- CSP copied the illustrative `packageBinding.copy` placeholder instead of the exact host-owned binding.
- Graph deliberately failed closed for the underspecified follow-up; the installed smoke runtime remains
  narrower than a full OHLCV/ATR momentum strategy.

# Diff summary

- The session now stores a committed cumulative four-lane brief separately from an uncommitted
  prompt. A successful replacement atomically commits its new brief and batch; Stop, provider
  failure, and restart retain the last completed batch while restoring the pending prompt.
- Candidate selection, unchanged-brief regeneration, local revalidation, TradeIR synthesis/loading,
  and synthetic smoke are disabled while a newer prompt is pending. The Candidate panel labels the
  retained cards and offers explicit apply or discard actions.
- Backtest-navigation-only turns, including the phrases observed in the reported session, open the
  Candidate test guidance without issuing provider requests or changing the strategy brief/hash.
- Shared envelope parameter defaults accept string, numeric, and Boolean JSON scalars. Numeric tokens
  are RFC 8785-canonicalized before storage; objects, arrays, nulls, and unknown properties remain
  invalid.
- Every lane prompt embeds the exact host-owned package binding instead of showing a copy placeholder.
- Candidate UI now separates generation from the exact-hash synthetic smoke path, identifies each
  source lane's missing runtime/lowerer, and explains why the five-minute OHLCV/ATR momentum example
  cannot enter the current QuoteL1 EMA smoke runner.
- README and Vibe Quant workflow/contract documentation now describe the actual contracts, durable
  refinement behavior, stop recovery, synthesis boundary, and current synthetic-versus-historical
  test limits.
- macOS context generation was refreshed and its script was made compatible with the system Bash/awk,
  `realpath`, and an explicit `DOTNET_CLI` path.

# Verification

- `git diff --check`: passed.
- macOS context regenerated and `--check` passed: 56 projects, 1,099 files, 172,468 LOC.
- Independent integration review: PASS after stop/failure provenance, pending gates, navigation,
  discard behavior, and exact-hash testing were rechecked.
- Independent codegen-contract review: PASS; scalar closure, binding injection, request/binding
  validation, and tamper rejection remain enforced.

# Risks/deferred

- This change must not claim that generated Python, declarative JSON, or CSP is runnable without an importer.
- A preserved strategy brief does not expand the installed TradeIR operator catalog or smoke-runner capabilities.
- Direct historical generated-candidate backtesting is still unavailable. The only in-screen execution
  is the bounded, in-process, non-historical QuoteL1 TradeIR smoke path.
- The five-minute momentum example still needs OHLCV data binding and high/volume/ATR operators plus a
  compatible runtime target; the terminal now reports that boundary instead of substituting behavior.
- Provider output can still misunderstand a strategy while satisfying a structural contract. The
  interpretation, unresolved questions, parameters, artifact, and exact hash remain review gates.
- Stop ignores late provider output but does not prove process-tree termination for every external CLI.
