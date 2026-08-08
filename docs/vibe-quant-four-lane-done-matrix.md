# Legacy Vibe Quant four-representation Done / Not Done matrix

Evidence date: 2026-08-08. “Done” means implemented and covered by the named local validation path;
it does not mean semantic correctness, profitability, historical readiness, or live safety.

This matrix describes the legacy four-representation subsystem only. It is not evidence for the
native strategy-agent workflow and must not appear under the native Research / VibeQuant / CSP /
Comparison readiness labels. The native product uses one research QueryEngine, one
transcend-0/VibeQuant worker that reaches AKQuant, and one Point72 CSP worker. See the maintained
[native architecture](quant-strategy-agent-architecture.md) and
[native Done / Not Done matrix](native-strategy-agent-done-matrix.md).

| Lane | Inspectable artifact | Native validation/test path | First actionable stop | Status |
|---|---|---|---|---|
| Vibe · Python | `strategy.py`, exact candidate SHA-256, source preview and diagnostics | Host-owned `vibe-quant/python-strategy/v1` deterministic authoring-profile validator; rerunnable with **Revalidate edit** | Deterministic Python-to-TradeIR lowerer/importer is not registered; a constrained Python runtime is also absent | **Done:** generation, inspection, hash binding, authoring validation. **Not Done:** import, native execution, canonical/historical backtest |
| Spec · Rules | `strategy.spec.json`, exact candidate SHA-256, JSON preview and JSON-path diagnostics | Closed `vibe-quant/declarative-rules/v1` schema/contract validator; rerunnable with **Revalidate edit** | Deterministic Rules-to-TradeIR lowerer is not registered; Rules has no independent runtime target | **Done:** generation, inspection, hash binding, closed-schema validation. **Not Done:** lowering, execution, canonical/historical backtest |
| Graph · Typed | `strategy.tradeir.json`, exact candidate SHA-256, canonical JSON preview and diagnostics | Installed TradeIR package/catalog validator; exact-hash in-process QuoteL1 EMA smoke runs the evaluator, risk gateway, simulated order book, and portfolio | Non-QuoteL1/unsupported graphs stop at data or closed-target admission with code and JSON path; historical worker/data admission is absent | **Done:** generation, inspection, package validation, supported exact-hash synthetic smoke. **Not Done:** general graph execution and historical worker backtest |
| CSP · Events | `strategy.csp.py`, exact candidate SHA-256, source preview and diagnostics | Host-owned `vibe-quant/csp-authoring-profile/v1` deterministic shape validator; rerunnable with **Revalidate edit** | CSP-to-TradeIR lowerer/importer, pinned CSP dependency, and CSP runtime host are not registered; Point72 compatibility remains unverified | **Done:** generation, inspection, hash binding, authoring validation. **Not Done:** verified CSP compatibility, import, native execution, canonical/historical backtest |

## Shared workflow boundary

Strategy chat and a local family-aware review produce one confirmed request first. A separate
explicit implementation action then starts four independent provider requests with the identical
canonical confirmed-request payload and hash. Immediately before provider dispatch, the host also
revalidates that payload against the exact candidate, research case, classification, and reviewed
draft; this context stays host-side. Every candidate and persisted batch must bind the confirmed
hash; missing, changed, incomplete, unsupported, noncanonical, legacy, or stale bindings are
nonactionable. Each terminal lane
result becomes inspectable without waiting for the other three, while selection, persistence,
synthesis, and testing remain gated on the complete validated four-lane batch. Provider, parse,
repair, and contract-validation failures retain their exact stage, code, path, message, and available
raw/artifact evidence.

The app readiness panel reports four separate gates for the selected lane: exact hash, lane-native
validation, canonical lowering/import, and native runtime/test. A successful earlier gate never
borrows evidence from a later gate or another lane.

## Verification commands

```bash
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj \
  --no-restore --filter FullyQualifiedName~ParallelStrategyCandidateGeneratorV1Tests --nologo

dotnet test tests/linux/TradingTerminal.App.Avalonia.Tests/TradingTerminal.App.Avalonia.Tests.csproj \
  --no-restore --filter 'FullyQualifiedName~TradeIrBacktestAuthoringTests|FullyQualifiedName~CandidateAuthoringUxContractTests' --nologo

dotnet build TradingTerminal.Mac.slnx --no-restore --nologo
```

The focused app tests exercise the view-model and XAML workflow without calling a paid model. A
live provider run still depends on locally configured provider credentials and is not implied by
these deterministic results.
