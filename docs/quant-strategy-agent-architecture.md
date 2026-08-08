# DaxAlgo native strategy-agent architecture

Status: native backend and retained-run UI slice implemented; full chart-to-app workflow not yet complete
Decision date: 2026-08-08
Last corrected: 2026-08-08

## Product decision

DaxAlgo uses one research conversation and two independent native implementations. The fourth UI
panel is comparison evidence; it is not a fourth implementation and it never invents metrics.

The runtime backbone is the pinned local FinanceManus `QueryEngine`, `ContextManager`, `Session`,
`ToolRegistry`, and `Coordinator`. DaxAlgo fixes the worker profiles and tools. User or model text
cannot select a different profile or broaden a worker's tool registry.

The native authorities are:

- transcend-0/VibeQuant `TaskSpec.from_dict -> make_plan -> run_task`, which reaches genuine
  AKQuant for the trading backtest;
- Point72 CSP `@csp.node`, `@csp.graph`, `csp.curve`, and `csp.run` for typed reactive execution;
- HKUDS/Vibe-Trading as research guidance and a design reference only. It is not an execution lane.

Microsoft RD-Agent/Qlib, TradingAgents, ai-hedge-fund, LEAN, and NautilusTrader remain comparison
references. They are not silently installed as additional runtimes.

## User workflow

```text
1. OBSERVE
   The user selects a possible jump or breakdown on a primary chart
   and adds up to three confirmation series.
        │
        ▼
2. FREEZE CONTEXT
   DaxAlgo freezes the selected range, as-of time, OHLCV bars,
   declared indicators, comparison observations, provenance, and hashes.
        │
        ▼
3. RESEARCH
   One QueryEngine research session examines only that frozen evidence.
   It asks what confirms, rejects, enters, sizes, cancels, exits, or reverses.
        │
        ▼
4. CONFIRM
   The user reviews one readable strategy and concrete scenarios.
   Confirmation binds the exact context and intent hashes.
        │
        ▼
5. IMPLEMENT AND RUN
   The same immutable job fans out to two fixed QueryEngine workers.
        │
        ├── VibeQuant TaskSpec -> make_plan -> run_task -> AKQuant backtest
        │
        └── Point72 CSP source -> graph construction -> csp.run
        │
        ▼
6. COMPARE
   DaxAlgo compares only observable native evidence and reports
   pass, fail, or unproven with the exact stage and retained artifacts.
```

For the first FDAX case, the intended conversation starts from an FDAX move and compares FESX, ES,
and VDAX. The research agent must distinguish confirmed movement, rejected or stale confirmation,
entry timing, order type, size, lifecycle close, and any unsupported short behavior before the user
confirms a run.

## What the backbone does

```text
DaxAlgo API/service
    │
    ├── owns sessions, immutable manifests, hashes, events, retention, and cancellation requests
    ├── creates one research QueryEngine with no execution tools
    ├── creates one VibeQuant QueryEngine with only submit_vibequant_task_spec
    ├── creates one CSP QueryEngine with only submit_csp_source
    ├── invokes the real FinanceManus Coordinator for the two native workers
    ├── streams original QueryEngine and native-stage events
    └── retains source, native results, exact errors, and the comparison report
```

The backbone is not a strategy language, compiler, market simulator, broker, or substitute
backtester. It does not parse CSP with regexes, call AKQuant directly around VibeQuant, fabricate
fills, or describe `csp.run` as a market backtest.

## One immutable handoff

Both native workers receive the same canonical confirmed job containing:

- the confirmed readable intent and its SHA-256;
- the frozen run manifest and its SHA-256;
- primary and comparison instruments, venues, sources, timeframe, timezone, selected range, and
  as-of time;
- one hash-bound input file per series;
- timestamped expected situations that are declared exhaustive when exact stream comparison is
  required;
- pinned QueryEngine, VibeQuant, AKQuant, and CSP versions or source revisions; and
- the actual provider/model identity used to generate each native artifact.

VibeQuant derives its real TaskSpec from this handoff. CSP derives genuine Python graph source from
the same handoff. There is no artificial shared strategy DSL between them.

## Four inspectable UI panels

| Panel | Real authority | Honest output |
|---|---|---|
| Research | pinned FinanceManus QueryEngine plus Vibe-Trading-style questions | frozen evidence, transcript, assumptions, missing decisions, readable proposal |
| VibeQuant / AKQuant | VibeQuant TaskSpec, planner, runner, and AKQuant | source, native stages, public trade count, equity, metrics, artifacts, exact failure |
| Point72 CSP | genuine CSP source and `csp.run` | source, graph/type/runtime stages, timestamped outputs, exact failure; no native fills or P&L |
| Compare | deterministic host comparison of retained results | agreement, disagreement, pass/fail/unproven, hashes; no invented metric |

## Two DaxAlgo screens

```text
SCREEN 1 — DESIGN & CONFIRM

selected chart range │ primary + up to 3 comparisons │ research transcript
                     │                               │
                     └──── readable strategy + scenarios ────┐
                                                              ▼
                                                   explicit user confirmation

SCREEN 2 — BUILD, TEST & COMPARE

Research evidence │ VibeQuant / AKQuant │ Point72 CSP │ Comparison
                  │ source + backtest   │ graph run   │ pass/fail/unproven
```

The screens consume the same backend session and run. The UI must not preserve the old synthetic
four-lane generator as though it were this workflow. If the legacy generator remains available, it
must be separately named and must not share readiness labels with native evidence.

## Native evidence boundary

| Evidence | VibeQuant / AKQuant | CSP |
|---|---:|---:|
| generated native artifact | yes | yes |
| native import/schema or graph construction | yes | yes |
| native execution | yes | yes |
| public aggregate trade count and metrics | yes | no |
| public raw order/fill timestamps through VibeQuant | no | no |
| typed output ticks with exact timestamps | no | yes |
| exact per-scenario comparison in the current integration | unproven | yes |

“Yes” for CSP means the host wrapper captured the values returned by the pinned native `csp.run`
call. The runner snapshots its host callables and hides its actual `__main__` module while generated
source executes, preventing the direct collector/result-writer monkeypatch path. Generated Python
still shares the CSP interpreter, so this is inspectable host-observed evidence, not a hardened or
cryptographic attestation claim.

The current unmodified VibeQuant result exposes aggregate trade and metric evidence but not raw
AKQuant order/fill timestamps. Therefore a VibeQuant run can prove a declared aggregate such as one
closed trade, while exact entry/exit timestamp behavior remains `unproven`. CSP can prove its full
ordered `intent` stream, but cannot prove fills, equity, P&L, or portfolio metrics.

## Failure reporting

Every lane terminates with its actual framework and stage. Examples include QueryEngine provider
failure, agent timeout, missing native submission, TaskSpec, planner, VibeQuant run, CSP import,
graph construction, `csp.run`, artifact retention, or comparison mismatch. A sibling failure does
not erase the other lane's evidence.

If the upstream FinanceManus Coordinator records an internal worker timeout instead of raising it,
DaxAlgo reads that retained worker status and reports `agent_timeout` for only the affected lane.
A synchronous native runner remains under process custody until its bounded child process is reaped;
the host does not publish a terminal timeout while that child continues writing the workspace.

## Current first-case boundary

The implemented frozen fixture currently proves only:

- one FDAX primary series with FESX, ES, and VDAX comparisons;
- structured causal OHLCV and indicator evidence in the research turn;
- stale-confirmation `no_trade`;
- one confirmed 10% long target;
- one explicit time-based close;
- genuine VibeQuant-to-AKQuant execution; and
- genuine CSP graph execution with exact ordered intent comparison.

It does not yet prove the full directional product requested by the user: short execution, market
versus limit selection, staged entries, partial fills, stop/target behavior, reversal, human UI
confirmation, or chart selection inside the app. The unmodified VibeQuant adapter has not
demonstrated working short execution, so the product must not claim general long/short support.

## Service and app boundary

The Python service exposes loopback session, message, confirmation, run, cancellation, paged-event,
status, and hash-checked artifact operations. The .NET app has a dedicated process host and typed
client on port 8766, separate from the existing ML sidecar. The service is disabled by default until
the pinned local or packaged runtimes and provider configuration are supplied.

The present development seam verifies only the public health payload on that fixed loopback port;
it does not authenticate the process instance. That is acceptable only for this disabled-by-default
local slice. Enabling or shipping it as trusted evidence requires a per-launch random credential
shared through a non-logged channel, plus explicit opt-in before accepting an externally managed
service. Otherwise another local process could impersonate the backend or invoke provider-backed
work.

The Strategy Builder now has a deliberately partial Screen 2 bridge. It can open without the old
synthetic four-lane confirmation, load an already confirmed retained run, page bounded session/run
events, show all four retained panels, surface exact errors, and retrieve hash-checked artifacts.
It does not create sessions, capture chart context, conduct the research conversation, or bind a
human confirmation. Those missing Screen 1 operations are still required for product completion.

The focused UI suite and app build pass. The attempted real macOS launch did not create a window:
Avalonia Native failed while starting the platform render timer with native error `-6661`. Therefore
manual in-app evidence remains unavailable and must not be inferred from the view-model/XAML tests.

## Safety boundary

Only the minimum host boundary is product-critical:

- generated native workers have no broker or live-order authority;
- contained child environments do not inherit provider or broker secrets;
- native work uses staged hash-bound files, bounded output, wall-time limits, and disabled network;
- VibeQuant and CSP execute independently in their real supported runtimes; and
- only retained native evidence may be shown as passed.

These controls do not limit strategy creativity in the research conversation. They limit what can
be called a completed, reproducible native run.

## Honest completion rule

The product is Done only when one real macOS app flow performs:

```text
chart selection
-> frozen primary/comparison context
-> provider-backed research conversation
-> explicit human confirmation
-> one immutable two-worker run
-> genuine VibeQuant/AKQuant and CSP results
-> four inspectable panels with exact failures
```

Until that evidence exists, the correct status is: native backend and .NET seam implemented;
chart-to-agent and complete two-screen product workflow not complete.
