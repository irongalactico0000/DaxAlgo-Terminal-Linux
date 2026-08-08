# Native strategy-agent Done / Not Done matrix

Evidence date: 2026-08-08. “Done” means the named behavior ran through its real supported path. It
does not imply profitability, production deployment, live-broker authority, or support for every
strategy family.

| Layer | Requirement | Status | Fresh evidence or exact blocker |
|---|---|---|---|
| Backbone | pinned FinanceManus QueryEngine, ContextManager, Session, ToolRegistry | **Done** | source revision `f25fab79e611fd904280cabc97d9d2393a0922dc`; runtime origin and Python 3.12 gates pass |
| Research | structured primary/comparison evidence reaches provider-backed research | **Done headlessly** | A12 transcript cites FDAX/FESX/ES/VDAX OHLCV, returns, volume ratios, EMAs, and VDAX staleness |
| Confirmation | immutable readable intent and manifest hashes | **Done in API** | both native lanes retain identical manifest and intent hashes |
| Confirmation | explicit human review in DaxAlgo | **Not Done** | A12 uses `scripted_headless_fixture` |
| VibeQuant | genuine `TaskSpec.from_dict -> make_plan -> run_task` | **Done** | A12 native stages passed at pinned VibeQuant revision |
| AKQuant | genuine backtest reached through VibeQuant | **Done for first long case** | A12 public result reports one closed trade and retained equity/report/result/task artifacts |
| CSP | genuine source, graph construction, and `csp.run` | **Done for first long case** | A12 host-wrapper-observed ordered intent stream passed; generated Python output is not described as security-attested |
| Compare | no-trade, entry, and close observable comparison | **Partially Done** | CSP exact scenarios pass; VibeQuant scenarios are honestly `unproven` because public raw timestamps are absent |
| Failure stages | exact lane, framework, stage, and error | **Done for covered paths** | missing submission, native failure, comparison mismatch, and swallowed worker timeout have regression tests |
| Process custody | no terminal timeout while native child still mutates workspace | **Done for bounded runners** | cancellation waits for native runner/process-group cleanup before timeout propagation |
| User cancellation | promptly terminate provider and active native child | **Not Done** | `cancel_run` is cooperative and waits for terminal native work |
| Restart | completed-run reload | **Done** | retained completed run can be reconstructed from disk |
| Restart | active research/in-flight run resume | **Not Done** | QueryEngine/provider task is not reconstructed after process restart |
| .NET seam | dedicated process host and typed HTTP client | **Done** | focused StrategyAgent tests pass; status/events/artifacts are typed |
| Loopback trust | authenticate the exact app-owned service instance | **Not Done for release** | the disabled-by-default development slice checks a public health identity on fixed port 8766 but has no per-launch secret; a local process could impersonate or invoke the service |
| Screen 1 | chart range, comparisons, research chat, human confirmation | **Not Done** | current chart and Strategy Builder still have no frozen-context handoff |
| Screen 2 | retained Research/VibeQuant/CSP/Compare evidence | **Partially Done** | the app view model can load/start/refresh/request-cancel for an already confirmed run, page retained events, show exact stages/errors/hashes, and retrieve hash-checked artifacts; it cannot create the run from Screen 1 |
| Screen 2 | successful manual macOS interaction | **Not Done** | focused UI tests and the app build pass, but the attempted app launch stopped before window creation at Avalonia Native `RenderTimer` error `-6661` |
| Packaging | self-contained pinned Python runtime in release | **Not Done** | Debug/configured local paths work; dependency closure is not packaged |
| Directional family | stale no-trade, one long, explicit close | **Done headlessly** | A12 |
| Directional family | short, market/limit, staged entry, partial fill, stop, target, reversal | **Not Done** | unmodified VibeQuant short path is unsupported; remaining cases are unproven |
| Other families | pairs, portfolio, market making, execution algo, options | **Not Done** | no native family-specific proof exists |

## A12 result

```text
Research: provider-backed structured evidence review                 PASS
VibeQuant -> AKQuant: one public closed-trade aggregate             PASS
CSP host-observed stream: no_trade -> target 0.10 -> close         PASS
VibeQuant exact per-scenario timestamps                             UNPROVEN
Overall retained evidence                                           PARTIALLY_PROVEN
Human UI confirmation                                               NOT DONE
```

The overall status is intentionally not “fully proven”: each real native path completed, but the
current VibeQuant public result cannot prove exact order/fill timestamps and the proof did not use
the DaxAlgo chart or a human confirmation interaction.

The CSP wrapper captures the pinned `csp.run` entry point before loading generated source and hides
its real `__main__` module during graph execution; an adversarial regression proves the ordinary
collector-monkeypatch path cannot forge the retained stream. Because generated Python and its CSP
graph still share an interpreter, the output is labeled host-wrapper-observed, not hardened or
cryptographically attested.

## App verification boundary

The retained-run Screen 2 slice is covered by 5 focused view-model/XAML tests, the full Avalonia
test project (105 passed), and a zero-warning app build. The live loopback service also recovered
A12 and returned 371 terminal events plus the hash-checked comparison artifact. A real macOS window
could not be inspected in this environment: both `dotnet run` and the built app host failed before
window creation because Avalonia Native could not start the macOS render timer (`-6661`). This is
recorded as an app-runtime test blocker, not as UI success.

Before this service is enabled or shipped as trusted evidence, the app and Python service need a
per-launch random credential carried outside command-line arguments/logs. Accepting an externally
managed service while disabled must also require an explicit development opt-in. Loopback binding
alone is not service identity.

The older [four-lane matrix](vibe-quant-four-lane-done-matrix.md) documents the legacy synthetic
representation builder. It is not evidence for this native workflow.
