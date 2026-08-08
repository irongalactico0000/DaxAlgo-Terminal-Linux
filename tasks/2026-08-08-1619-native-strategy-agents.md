# Native Strategy Builder agent vertical slice

## Goal

Prove one complete FDAX chart-to-native-results situation headlessly, then connect its existing
run/event API to the Strategy Builder. The runtime uses a narrow DaxAlgo-owned LangGraph graph,
real `akquant.run_backtest`, and real Point72 `csp.run`. FinanceManus is explicitly excluded.

## Plan

1. Finish and verify the canonical C# research/confirmed-intent handoff.
2. Pin a locally resolvable LangGraph/checkpoint set, `akquant==0.3.36`, and `csp==0.18.0`.
3. Implement frozen chart/scenario contracts and hash-bound worker inputs.
4. Implement the headless LangGraph research/confirmation/fan-out/join state machine.
5. Run the FDAX/FESX/ES/VDAX situation through genuine akquant and CSP APIs.
6. Expose monotonic CLI events and exact artifacts/failures.
7. Connect the same API to the existing Design & Confirm and Build, Test & Compare screens.

## Architecture decision

- LangGraph supplies the actual graph runtime: `StateGraph`, `create_react_agent`, `interrupt`,
  `Command`, `Send`, durable checkpoints, parallel joins, and streaming.
- DaxAlgo owns only finance-specific graph state, explicit tools, canonical contracts, adapters,
  isolation policy, and result taxonomy.
- Vibe-Trading commit `46465ac3cd8d0a35208f974704c5e801a1107a13` is an audited reference for
  memory, traces, manifests, cancellation, contained workspaces, and tool restrictions. Its broad
  product runtime is not imported.
- VibeQuant commit `1f5442d88ec97b6075ac73a3c4d0b42d1c00a640` is an audited reference for
  deterministic execution and a thin genuine akquant adapter. Its `TaskSpec` DSL and in-process
  unsandboxed generated-code execution are not adopted.
- akquant is the native portfolio backtester. CSP is a typed graph runner and is never presented as
  a market backtest.

## Concrete situation

- primary: five-minute FDAX;
- comparisons: FESX, ES, and inverse VDAX, each timestamped and provenance-bound;
- trigger: FDAX `+/-0.80%`;
- confirmation: at least two fresh comparisons—FESX `+/-0.35%`, ES `+/-0.25%`, inverse VDAX
  `-/+2.00%`—with five-minute staleness cutoff;
- fewer than two confirmations: `no_trade`;
- risk: 0.5% equity, 20% notional cap, 40/60 tranches;
- execution: confirmed market/limit/TIF, one-bar timeout, cancel then distinct residual order,
  partial-fill-driven remaining size;
- lifecycle: 0.6% stop, 1.2% target, six-bar time exit, evidence-loss invalidation,
  exit-fill-before-reverse, and final unwind.

The shared scenarios cover upward/downward continuation, unconfirmed movement, stale/missing data,
unfilled and partial-filled limits, cancellation/new residual order, stop, target, time exit,
invalidation, reversal, OCO sibling cancellation, and final unwind. Atomic replace/reverse remain
explicit native capability failures.

## Existing evidence

- `akquant==0.3.36`: native probes exercised `akquant.Strategy`, `akquant.run_backtest`, staged
  limits, partial fills, cancellation plus a distinct new order, exit-then-opposite-entry, OCO, and
  an FDAX futures bracket with explicit multiplier/margin configuration.
- `csp==0.18.0`: native probes exercised `@csp.node`, `@csp.graph`, `csp.curve`, and `csp.run` with
  timestamped outputs. CSP produced no fills, positions, equity, or P&L.
- VibeQuant's `src/adapters/akquant_engine.py` calls the genuine `aq.run_backtest`; its upstream
  pipeline tests passed during the audit.
- The C# canonical-intent and authoring review suites are tracked in
  `tasks/2026-08-08-1013-quant-research-intent-v1.md`.

## Blast radius

- future `tools/strategy-agent/` Python package and contained native-worker images;
- frozen chart-context and scenario contracts;
- CLI/run-event service and client;
- existing Strategy Builder view-model/presentation only after headless proof;
- focused Python and C# tests and architecture documentation.

No broker, credential, live-order, Windows, or Professional-overlay path is in scope.

## Verification status

Architecture and native capability audit complete. The connected LangGraph/akquant/CSP headless
vertical slice is pending and must not be described as implemented.

## Risks and deferred work

- Lock only package versions resolvable and tested in the target Python environment.
- Generated code requires rootless OCI or equivalent OS isolation; a virtual environment is not a
  security boundary.
- Comparison is limited to decisions and order intents common to both engines. Native fills,
  trades, positions, equity, and P&L belong only to akquant.
- RD-Agent/Qlib experimentation, multi-agent research debate, paper/live promotion, and monitoring
  are later milestones.

## Documentation

The authoritative architecture, diagrams, API, and acceptance boundary are in
`docs/quant-strategy-agent-architecture.md`.
