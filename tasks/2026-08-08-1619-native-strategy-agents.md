# Native Strategy Builder agent vertical slice

## Goal

Connect one frozen chart-shaped research case to the real local FinanceManus QueryEngine backbone,
genuine transcend-0/VibeQuant -> AKQuant execution, and genuine Point72 CSP execution. Retain every
native artifact and exact failure, then connect the proven API to DaxAlgo's existing two-screen
Strategy Builder without introducing a replacement DSL, validator, simulator, or backtester.

## Correct architecture

```text
frozen chart evidence
-> research QueryEngine (no execution tools)
-> explicit readable confirmation
-> hash-bound immutable job
-> FinanceManus Coordinator
     -> VibeQuant QueryEngine (submit_vibequant_task_spec only)
        -> TaskSpec.from_dict -> make_plan -> run_task -> AKQuant
     -> CSP QueryEngine (submit_csp_source only)
        -> @csp.node/@csp.graph -> csp.run
-> deterministic comparison of retained native evidence
```

HKUDS/Vibe-Trading is research guidance and a design reference only. RD-Agent/Qlib,
TradingAgents, ai-hedge-fund, LEAN, and NautilusTrader are benchmark references, not additional
runtime lanes.

## Implemented backend

- Python 3.12 service under `tools/strategy-agent`.
- Exact FinanceManus source-revision and interpreter gate.
- Fixed research, VibeQuant, and CSP QueryEngine profiles.
- Exactly one host-owned submission tool for each native worker.
- Real FinanceManus `Coordinator` fan-out with opaque single-use dispatch tokens.
- Hash-bound manifest, confirmed intent, research context, files, provider/model identity, and
  source revisions.
- Independent contained VibeQuant and CSP workspaces and native child processes.
- VibeQuant public path `TaskSpec.from_dict -> make_plan -> run_task`; no direct AKQuant bypass.
- Point72 CSP source-file path through real graph construction and `csp.run`.
- Append-only session/run events, terminal result retention, paged event replay, and hash-checked
  artifact retrieval.
- Deterministic comparison that treats VibeQuant exact scenarios as `unproven` when its public
  result lacks order/fill timestamps and requires the complete ordered CSP intent stream to match.
- Exact per-worker `agent_timeout` reporting even when FinanceManus retains rather than raises the
  timeout.
- Process custody preserved until bounded native child cleanup completes.
- Dedicated .NET process host and typed loopback client on port 8766.
- Partial Screen 2 bridge for retained Research, VibeQuant/AKQuant, CSP, and Compare evidence,
  including exact errors, bounded event replay, hashes, and hash-checked artifact viewing.

## Fresh native proof

Run A12 (`.runtime/live-proof-20260808-a12`) completed through the provider-backed production
composition with structured FDAX/FESX/ES/VDAX OHLCV and causal indicator context.

| Evidence | Result |
|---|---|
| Research QueryEngine used structured bars, returns, volume ratios, EMAs, and stale VDAX gap | passed |
| VibeQuant native stages | passed |
| AKQuant public closed-trade aggregate | expected 1, observed 1 |
| CSP native graph/run | passed |
| Complete CSP intent stream | host-wrapper-observed `09:05 no_trade -> 10:00 target 0.10 -> 10:30 close`; not security-attested |
| Overall evidence | `partially_proven` because VibeQuant public exact timestamps are unavailable |
| Confirmation mode | scripted headless fixture, not a human UI confirmation |

The proof report hash is
`0506301ff3266cc8e8e8c7626f3b755bd16fcf4cea841affccd598dd74a4eb99`.

## Verification

- Python suite without optional native configuration: 96 passed, 10 skipped.
- Python suite with QueryEngine, VibeQuant/AKQuant, and CSP paths configured: 106 passed.
- Ruff 0.12.7 over `daxalgo_strategy_agent` and `tests`: clean.
- Focused .NET StrategyAgent tests after timeout alignment: 12 passed.
- Focused retained-run UI tests: 5 passed; full Avalonia UI test project: 105 passed.
- Avalonia app build after UI wiring: passed with 0 warnings and 0 errors.
- Earlier full headless .NET run: 826 passed, 2 unrelated existing macOS reparse-path failures,
  6 skipped.
- Earlier app and macOS solution builds: passed; two existing nullable warnings remained in the
  full solution build.
- Manual app launch attempt: blocked before window creation by Avalonia Native render-timer error
  `-6661`; no manual Screen 2 interaction is claimed.

## Honest Done / Not Done

Done:

- real QueryEngine research and fixed native-worker coordination;
- genuine VibeQuant-to-AKQuant long backtest path;
- genuine Point72 CSP graph execution;
- immutable same-job fan-out, artifacts, exact stages, and comparison;
- structured headless FDAX proof; and
- registered .NET service host and typed client; and
- an honest retained-run Screen 2 viewer for an already confirmed run.

Not Done:

- chart drag/range selection and up-to-three comparison capture in the app;
- byte-identical chart-to-Strategy-Builder handoff;
- explicit human confirmation through the UI;
- Screen 1 creation of the backend research session and confirmed run;
- a successful manual macOS Screen 2 run (current launch stops at Avalonia `RenderTimer` `-6661`);
- release packaging of the pinned Python/runtime dependency closure;
- per-launch loopback authentication and explicit development opt-in for an externally managed
  service (the current fixed-port health identity is not sufficient for trusted release evidence);
- immediate cancellation of an active provider/native child (current cancellation is cooperative);
- in-flight research/run resumption after process restart;
- short execution through the unmodified VibeQuant adapter;
- market/limit selection, staged entries, partial fills, stops, targets, and reversal proof; and
- exact VibeQuant per-scenario timestamps or raw orders/fills through its public result.

## Product completion boundary

Do not call the product complete until a real macOS app run performs chart selection, frozen
context transfer, provider-backed research, explicit human confirmation, the immutable two-worker
run, and four inspectable Research / VibeQuant / CSP / Comparison panels. Until then the accurate
status is: native backend proven for one long-only case; complete DaxAlgo workflow Not Done.

See `docs/quant-strategy-agent-architecture.md` and
`docs/native-strategy-agent-done-matrix.md` for the maintained product boundary.
