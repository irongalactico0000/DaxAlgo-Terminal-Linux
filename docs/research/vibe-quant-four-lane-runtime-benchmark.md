# Vibe Quant four-lane runtime and backtest benchmark

Status: architecture decision record and implementation roadmap
Evidence date: 2026-08-07 (Asia/Seoul)

## Decision

Vibe Quant's four lanes are four **authoring dialects**, not four interchangeable backtest engines:

| Lane | Native meaning | Native review/test | Comparable historical backtest |
|---|---|---|---|
| Vibe Python | Rapid, editable event/vector strategy source | Isolated Python preflight and preview | Deterministically import or lower to canonical TradeIR, then use DaxAlgo's engine |
| Declarative Rules | Closed position-lifecycle rules (allocation is a separate future profile) | AST validation and rule trace | Deterministically lower the supported profile to TradeIR, then use DaxAlgo's engine |
| Typed Graph | Canonical DaxAlgo TradeIR | Package validation and exact-target smoke | Run the exact TradeIR hash in DaxAlgo's historical worker |
| CSP Events | Typed event graph | Pinned CSP graph build or native simulation | Only a deterministic supported-subset CSP-to-TradeIR conversion can enter canonical comparison |

The product must expose both kinds of evidence without confusing them:

1. **Native preview/simulation** answers, “Does this artifact parse and behave in its own authoring
   model?”
2. **Canonical backtest** answers, “How does this exact strategy behave on the same data, fills,
   fees, risk, and engine as another strategy?”

Native simulation returns are not a fair cross-lane comparison. A fair comparison starts only after
the strategy has a reviewed, deterministic mapping to the same canonical TradeIR semantics and each
strategy-specific run binds the same immutable comparison scenario. AI synthesis can propose
another graph, but cannot prove that it is economically equivalent to Python, Rules, or CSP source.

### Four evidence levels

The UI and result store must preserve four different proof boundaries:

| Level | Evidence | Can show P&L? | Comparable across lanes? |
|---|---|---:|---:|
| Format validation | Closed native shape and exact source hash | No | No |
| Synthetic compatibility smoke | A narrow known input passes a pinned bridge/runtime | Not as historical evidence | No |
| Lane-native historical simulation | Native evaluator uses an exact historical snapshot and explicit assumptions | Yes, labeled native | No; evaluator semantics can differ |
| Canonical historical backtest | Deterministically converted/identity TradeIR runs on one DaxAlgo data/execution contract | Yes | Yes, when run contracts match |

Direct Python or CSP intent adapters belong to lane-native simulation. They may reuse DaxAlgo risk,
book, fills, costs, and reporting, but they still do not prove that native event evaluation is the
same as a canonical TradeIR graph. Only a deterministic conversion receipt (or Graph identity) can
cross the canonical-comparison boundary.

The first executable product slice should therefore be:

```text
Graph · Typed candidate
  -> package validation
  -> resolve instrument and historical data
  -> exact data/capability admission
  -> worker-isolated DaxAlgo run
  -> immutable report and provenance
```

The other three lanes should gain honest native previews and deterministic importer/lowerer seams in
later slices. They must never borrow Graph's readiness or result.

## What the failed four-candidate screen revealed

The observed `$.artifact` conversion failures occurred before lane code was meaningfully reviewed.
The affected model prompt asked every provider to serialize DaxAlgo's complete internal
`StrategyGenerationCandidateV1`, including candidate ID, lane, request hash, filename, language,
and package binding. A harmless omission or wrapper variation then invalidates the entire candidate.

That was the wrong trust boundary. The implemented correction is:

```text
Model-owned response                 Host-owned canonical candidate
--------------------                 ------------------------------
review metadata              ->      schema version
lane-native source or JSON   ->      candidate ID and expected lane
                                      request hash
                                      contract/package binding
                                      canonical filename/language
                                      content hash
                                      deterministic validation result
```

The model should never be responsible for echoing security- or identity-bearing host metadata. The
host may accept the older wrapper during migration, but must ignore and reconstruct its identity
fields. This makes malformed **strategy content** invalid without making internal envelope trivia a
reason to hide otherwise inspectable code.

### Generation issues are routed, not flattened into `Invalid`

`Invalid` is not an actionable diagnosis. Generation must classify every issue before deciding
whether another model call can help:

| `GenerationIssueCategoryV1` | Examples | `RepairDispositionV1` | Automatic model repair? |
|---|---|---|---:|
| `TransportSyntax` | malformed outer JSON, prose around the compact response | `AutomaticOnce` | Yes, once |
| `DraftShape` | missing required native property, forbidden wrapper, source marker typo | `AutomaticOnce` | Yes, once, if it is the only blocking category |
| `NeedsFacts` | instrument, observation schema, interval, session, or decision time absent | `ReturnToFacts` | No |
| `FactsMismatch` | artifact contradicts a confirmed instrument/schema/time value | `ReviewFactsOrRegenerate` | No |
| `UnsupportedSemantic` | operator, order type, dynamic code, or event behavior outside an installed bridge | `RefineOrInstallCapability` | No |
| `SemanticContradiction` | incompatible entry/exit/risk clauses | `UserReview` | No |
| `CapabilityBlocked` | missing runtime/lowerer/importer or target-profile rejection | `InstallOrChooseCapability` | No |
| `DataOrEnvironment` | unavailable snapshot, credentials, worker, package, or catalog service | `FixEnvironment` | No |
| `IntegrityOrProvenance` | hash, signature, request lineage, or stale-editor mismatch | `HardFail` | No |
| `ProviderFailure` | authentication, unavailable CLI, timeout, or provider process failure | `UserRetry` or provider setup | No |
| `Canceled` | explicit user cancellation | `AwaitUserAction` | No |

The repair allowlist is closed and versioned. A lane gets at most one automatic repair only when
**all** blocking issues are `TransportSyntax` or `DraftShape`; the request contains the stable
codes/paths and the prior raw response, and the second response receives a new attempt receipt.
Missing facts never become invented facts, unsupported behavior is never silently approximated,
and runtime/data failures are never sent to a language model. The current
[`StrategyGenerationLaneAgentV1.GenerateAsync`](../../src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyCandidateGeneratorV1.cs)
implements a conservative first slice: it does not retry known missing TradeIR data facts,
operators absent from the installed TradeIR catalog, or the currently enumerated Declarative Rules
clock/data-fact paths. Completing the closed taxonomy across every Rules path, Python, CSP,
contradictions, capabilities, data, and provenance remains a Phase 0A production gate.

### Full generation provenance is separate from artifact identity

An exact source hash is necessary but does not answer which prompt, facts, model, or repair attempt
produced it. Persist one `GenerationProvenanceV1` per initial or repair call containing:

- batch/lane/attempt identity and parent-attempt hash for repairs;
- `AuthoringFactsV1` hash, candidate request hash, agent/prompt-contract version, exact system and
  user-message hashes plus content-addressed pointers to their retained bytes, and output-contract
  version;
- provider ID, model ID, reasoning effort, provider request ID when available, and every effective
  generation option (temperature/seed/tool mode or an explicit `unspecified`);
- request time, completion time, duration, token usage, provider status/error, raw-response byte
  hash, and an access-controlled pointer to the retained raw bytes;
- parser, native validator, package, operator/rules catalog, and host-wrapper implementation hashes;
- resulting native-artifact and canonical-candidate hashes, or the ordered classified issue list
  and repair disposition.

The content hash remains the identity of identical candidate bytes; the provenance receipt has its
own hash and lineage. Wall-clock/token telemetry can make two provenance receipts different and is
never used as evidence that two artifacts differ semantically. Today
[`StrategyGenerationAgentRunV1`](../../src/linux/Tools/DaxAlgo.Codegen/StrategyCandidateGenerationOrchestratorV1.cs#L21-L28)
stores agent/provider/request IDs, success/error, raw response, and usage, while the provider seam
also exposes model and effort
([`IStrategyCodegenClient`](../../src/linux/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCodegenClient.cs#L187-L206));
the durable combined receipt above does not yet exist.

### The next boundary: confirm shared facts before artifact generation

Fixing the envelope is necessary but not sufficient. Canonical TradeIR and the current closed Rules
document require concrete, nonempty instrument/data/time bindings. An underspecified idea such as
“5-minute momentum breakout” cannot truthfully produce a package-valid Graph while the instrument,
venue, data schema, timezone, and session remain unknown. Asking GraphAgent to “preserve the gap” and
then rejecting its empty `dataRequirements` merely converts a useful question into a technical
validation failure.

The four-artifact action must therefore have a small host-owned **Shared facts** gate before any of
the four model requests:

```text
Idea or starter
  -> extract candidate facts from the brief/current chart
  -> show every value as Proposed, Confirmed, or Missing
  -> user confirms instrument, venue/asset class/currency, data kind and interval,
     timezone/session, and decision timing
  -> host resolves canonical instrument/schema/catalog identities
  -> start four AI artifact requests with the same confirmed facts
```

Current-chart values may prefill the form but are never silently authoritative. A user may continue
exploring starters without these facts, but **Build 4 candidates** must remain gated; otherwise at
least the Graph lane is being asked to satisfy an impossible contract. This preserves the user's
requirement that candidate generation starts four real AI calls: once the shared-facts gate passes,
all four calls start. The gate itself spends no model call.

An eventual partial-draft schema could support pre-fact AI interpretations, but it must be a
different contract and state from canonical TradeIR. Do not call a partial graph package-valid and
do not materialize missing facts by silently patching model output after the fact.

### Authoring facts, comparison scenario, and run configuration are different contracts

Do not let “facts” become a mutable bag shared by generation and execution.
`AuthoringFactsV1` is confirmed **before** the four calls and contains only semantics that all four
artifacts must share: canonical instrument key, venue, asset class and currency; observation kind,
schema/catalog identity and interval; timezone, session calendar/boundary; and decision timing. Each
field records `Proposed | Confirmed | Missing`, value provenance, confirmer, and revision. The host
canonicalizes it and gives every lane the same immutable `authoringFactsHashSha256`.

`ComparisonScenarioV1` is strategy-neutral and is created during run setup. It binds the historical-
data-manifest hash, requested range and warmup, initial capital, sizing limits, fill timing, spread/
slippage/fees, partial-fill and end-of-run position policy, benchmark, deterministic seed, and the
required target/engine profile. A new range, cost model, seed, or capital value creates a new
scenario hash; it does not rewrite the authored strategy or its confirmed semantic facts.

`BacktestRunConfigV1` is strategy-specific. It binds one exact source/canonical module hash, optional
parameter-application and conversion receipts, one `ComparisonScenarioV1` hash, and the resolved
compiler/runtime/host identities for that run. Different strategies therefore have different run-
config hashes by design. Cross-lane comparison requires equal scenario hashes and equal admitted
data/target/engine identities, never equal strategy-bound run-config hashes.

The UI therefore has two gates: **Confirm strategy facts** before `Build 4 candidates`, then
**Configure this scenario** before `Run historical backtest`. A value common to these contracts (for
example instrument) must be exact-hash equal; run setup may narrow a date range but may not silently
change the instrument, observation schema, interval, session, or decision timing embedded in the
artifact.

## Research method and source ledger

Open-source references were cloned read-only under `/private/tmp/vq-reference-repos`, pinned to the
exact commits below, and not vendored. Proprietary products were evaluated only from official public
documentation and API descriptions.

| Reference | Exact revision | License / availability | Why it was inspected |
|---|---|---|---|
| [vectorbt](https://github.com/polakowo/vectorbt/tree/34b6d5935e3ea3eccd549e2592bc0f455b8045f5) | `34b6d5935e3ea3eccd549e2592bc0f455b8045f5` | Apache-2.0 plus Commons Clause | Vectorized exploration, parameter grids, records, data alignment |
| [backtesting.py](https://github.com/kernc/backtesting.py/tree/ca2e2611621e472542ba90f7243a1fa06a7d7108) | `ca2e2611621e472542ba90f7243a1fa06a7d7108` | AGPL-3.0 | Minimal Python strategy lifecycle, causality, optimization, results |
| [Freqtrade](https://github.com/freqtrade/freqtrade/tree/834f7e5365713feb6530ad4df474fe5e800709d9) | `834f7e5365713feb6530ad4df474fe5e800709d9` | GPL-3.0 | Strategy validation, backtest jobs, progress, persisted evidence |
| [FreqUI](https://github.com/freqtrade/frequi/tree/5478bf0568359a02c9d37aa9e891342609a2fec8) | `5478bf0568359a02c9d37aa9e891342609a2fec8` | GPL-3.0 | Run/configure/stop/analyze/compare workflow |
| [NautilusTrader](https://github.com/nautechsystems/nautilus_trader/tree/05b709b36edbe9a6a0d26a1bb77677dcb5051856) | `05b709b36edbe9a6a0d26a1bb77677dcb5051856` | LGPL-3.0 | Typed configuration, data catalog admission, event ordering, deterministic results |
| [QuantConnect LEAN](https://github.com/QuantConnect/Lean/tree/ea470761aa7c9908495f42e4637f93a779a254f7) | `ea470761aa7c9908495f42e4637f93a779a254f7` | Apache-2.0 | Job packets, engine isolation, progress, reports, child-run optimization |
| [Point72 CSP](https://github.com/Point72/csp/tree/3ab39b299e8605419486c53f10f33a87c6c41363) | `3ab39b299e8605419486c53f10f33a87c6c41363` | Apache-2.0 | Typed event graphs, simulation/realtime boundary, graph inspection, cancellation |
| [Composer](https://help.composer.trade/) | official help + [Swagger](https://api.composer.trade/docs/swagger.json), accessed 2026-08-07 | Proprietary; no accessible official source repository | Portfolio-allocation tree, versioned editor, backtest assumptions/results |
| [Capitalise.ai](https://support.capitalise.ai/) | official help, accessed 2026-08-07 | Proprietary; no official public source repository found | Constrained-English entry/exit lifecycle, confirmation, hit/trade review |

Composer's Swagger response was 299,969 bytes with SHA-256
`85acd786192d3adcf7ed9a3eedc3d57822ee393580a6c94b5ea33348591edf41` at inspection.
Capitalise's official support certificate had expired on 2026-07-05, so those pages were retrieved
with certificate verification disabled and are recorded as weaker transport evidence. No
unofficial replacement repository was treated as product evidence.

This report adopts concepts and interaction patterns. It does not copy source code or add these
projects as dependencies.

## Lane 1: Vibe Python

### What the references actually do

vectorbt's strength is broad numerical exploration. Its public example evaluates large moving-
average grids, visualizes the whole surface, and drills into an exact combination rather than
pretending one generated draft is “the winner” ([README](https://github.com/polakowo/vectorbt/blob/34b6d5935e3ea3eccd549e2592bc0f455b8045f5/README.md#L149-L175)). Its portfolio API explicitly binds fees, slippage, sizing, rejection probability,
and random seed ([portfolio/base.py](https://github.com/polakowo/vectorbt/blob/34b6d5935e3ea3eccd549e2592bc0f455b8045f5/vectorbt/portfolio/base.py#L1617-L1651)), while its documentation warns that
future-valued timestamps can create cheating strategies
([portfolio/base.py](https://github.com/polakowo/vectorbt/blob/34b6d5935e3ea3eccd549e2592bc0f455b8045f5/vectorbt/portfolio/base.py#L1708-L1726)).

backtesting.py provides a compact stateful lifecycle. Indicators declare warmup/shape constraints,
and `next()` sees one progressively revealed completed candle
([backtesting.py](https://github.com/kernc/backtesting.py/blob/ca2e2611621e472542ba90f7243a1fa06a7d7108/backtesting/backtesting.py#L77-L213)). Orders normally fill at the next bar's open unless trade-on-close semantics are explicitly selected
([backtesting.py](https://github.com/kernc/backtesting.py/blob/ca2e2611621e472542ba90f7243a1fa06a7d7108/backtesting/backtesting.py#L219-L240)). Optimization is a separate operation over declared parameters
([backtesting.py](https://github.com/kernc/backtesting.py/blob/ca2e2611621e472542ba90f7243a1fa06a7d7108/backtesting/backtesting.py#L1386-L1453)), not an implicit side effect of compilation.

Freqtrade separates strategy validation, execution, progress, cancellation, and stored evidence:

- versioned strategy interface and typed parameters
  ([interface.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/strategy/interface.py#L51-L145),
  [parameters.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/strategy/parameters.py#L30-L176));
- validation of strategy outputs
  ([strategy_validation.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/strategy/strategy_validation.py#L12-L42));
- shifting signals to prevent use of future candles
  ([backtesting.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/optimize/backtesting.py#L545-L572));
- asynchronous job start and progress
  ([api_backtest.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/rpc/api_server/api_backtest.py#L155-L234))
  plus explicit abort
  ([api_backtest.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/rpc/api_server/api_backtest.py#L291-L308));
- stored result, configuration, source, parameters, and market comparison
  ([bt_storage.py](https://github.com/freqtrade/freqtrade/blob/834f7e5365713feb6530ad4df474fe5e800709d9/freqtrade/optimize/optimize_reports/bt_storage.py#L49-L115)).

FreqUI makes the lifecycle visible: configure and run, stop/reset or load, then analyze, compare,
summarize, and visualize
([backtest.vue](https://github.com/freqtrade/frequi/blob/5478bf0568359a02c9d37aa9e891342609a2fec8/src/pages/backtest.vue#L72-L121),
[BacktestRun.vue](https://github.com/freqtrade/frequi/blob/5478bf0568359a02c9d37aa9e891342609a2fec8/src/components/ftbot/BacktestRun.vue#L50-L178)).

### DaxAlgo product meaning

Vibe Python should be the quickest editable lane, but “Python source exists” must not imply “the
terminal ran it.” The current generated ABI (`initialize_state`, `on_event`) is not the same as the
existing disconnected `PythonStrategyKernel` line protocol. The backtest worker also has no Python
artifact strategy source. Therefore the current artifact is correctly a review draft, but the UI
needs to explain the missing bridge rather than merely label it invalid or non-package-valid.

The native preview/simulation target should be a pinned, isolated Python worker with:

1. exact source hash and Python/runtime/bridge identities;
2. AST/import preflight and a closed module allowlist;
3. a host-owned event ABI using completed, point-in-time observations;
4. deterministic seed, timeout, memory, and output limits;
5. NDJSON signals/intents only—no broker, filesystem, network, or engine ownership;
6. prefix-invariance and future-leak tests;
7. DaxAlgo-owned fills, costs, risk, portfolio, progress, and report.

Even with DaxAlgo-owned accounting, this remains a Python-native historical simulation. Its result
is useful evidence for that artifact but is not a canonical cross-lane result until a deterministic
Python-to-TradeIR conversion exists.

Parameter exploration should become an explicit parent experiment containing immutable child runs,
an objective, constraints, train/out-of-sample split, and every failure—not an opaque “AI optimize”
button.

## Lane 2: Declarative Rules

### Composer and Capitalise are different products

Composer represents a scheduled **multi-asset target-allocation tree**. Assets sit under nested
weight, conditional, filter, and group nodes; a daily-to-yearly rebalance evaluates the tree and
produces target weights. Its AI feature inserts generated structure into the same editable visual
tree and can then backtest it ([Create with AI](https://help.composer.trade/article/108-create-with-ai)). The public API exposes closed, versioned tree schemas and accepts explicit capital, fee,
slippage, broker, date, benchmark, and engine settings
([API documentation](https://api.composer.trade/docs/)).

Capitalise represents a **single-asset entry-to-exit lifecycle**. The user writes constrained
English, reviews the system's broken-down conditions/actions, chooses once/loop and risk limits, and
then selects live, simulate, or backtest
([Confirming the Strategy](https://support.capitalise.ai/en/articles/5982066-confirming-the-strategy)). Its backtest uses documented 1-minute close assumptions, requires an exit, and distinguishes complete hits from an open final
position ([Running a Backtest](https://support.capitalise.ai/en/articles/4287696-running-a-backtest),
[Analyzing a backtest](https://support.capitalise.ai/en/articles/4290312-analyzing-a-backtest)). It also explicitly documents full-fill/no-slippage/no-fee limitations; DaxAlgo should copy that clarity, not those assumptions.

DaxAlgo Rules v1 is shaped like Capitalise's position lifecycle, not Composer's allocation tree. It
should be labeled `positionLifecycle`. A future Composer-like profile should be a separate
`portfolioAllocation` schema, validator, lowerer, clock, and result view. Combining both into an
undiscriminated object would leave entry/exit and portfolio-weight semantics ambiguous.

### Draft-to-resolved is an explicit trust transition

The model produces `RulesDraftV1`, a semantic AST with rule/parameter references and review text.
It does **not** own strategy identity, instrument/event-schema identity, or catalog IDs, versions,
and hashes. If the migration schema still carries those fields, they are non-authoritative draft
claims; a future draft schema should omit them entirely.

The host deterministically creates `RulesResolvedV1` from exactly:

```text
RulesDraftV1 hash
+ AuthoringFactsV1 hash
+ installed RulesSemanticCatalogV1 hash
+ resolver implementation hash
```

Resolution inserts the host strategy ID and exact installed catalog, resolves every instrument,
event field, parameter, indicator and rule reference, then checks type, unit, clock, causality and
lifecycle precedence. Any fact-bearing value present in the draft must equal the confirmed
`AuthoringFactsV1` value; a contradiction produces `FactsMismatch` at the draft path rather than
being overwritten and executed. A legacy draft catalog/schema claim must likewise equal the exact
installed value or fail with `CatalogFactsMismatch`; an equal claim is discarded and the host value
is inserted during the explicit Draft-to-Resolved projection. No unequal model claim is silently
patched into an executable document.

`RulesResolutionReceiptV1` binds the four inputs above, the resolved hash, and a complete
draft-path-to-resolved-path map. Only `RulesResolvedValid` may enter the lowerer. The current Rules
prompt/schema asks the model for an `operatorCatalog` object and the structural validator can accept
a well-shaped invented hash, so this split is a required contract revision rather than current
evidence.

### Required deterministic lowerer

The current closed Rules shape is valuable, but structural validity is not executability. It still
lacks reference resolution, type/unit checks, causal proof, lifecycle precedence, exact installed
catalog ownership, and a deterministic Rules-to-TradeIR lowerer. Its permitted rules and order types
also exceed the currently installed TradeIR operators and narrow runtime target.

The executable boundary must accept:

```text
resolved Rules hash
+ host-resolved facts hash
+ exact Rules semantic catalog
+ exact installed TradeIR operator registry
+ lowerer implementation hash
```

It must return either one canonical TradeIR module plus a source-path-to-node receipt, or a stable
ordered issue list and no graph. No AI call occurs during lowering. Unsupported `any`, crosses,
limit/stop orders, arbitrary operators, unsafe revisions, or missing facts must fail at their exact
Rules paths rather than be approximated or silently omitted.

The default Rules UI should show semantic cards—`WHEN`, `ENTER`, `UNTIL`, `EXIT`, `RISK`, and `RUN
MODE`—plus the parsed interpretation and unresolved facts. Raw JSON and lowering receipts belong in
Advanced view.

## Lane 3: Typed Graph

### What NautilusTrader and LEAN establish

NautilusTrader binds much more than strategy code. Its run config includes venue/account/book/fill/
latency/fee behavior
([venue config](https://github.com/nautechsystems/nautilus_trader/blob/05b709b36edbe9a6a0d26a1bb77677dcb5051856/crates/backtest/src/config.rs#L430-L534)),
while its data config binds catalog URI, instrument IDs, data type and time bounds
([data config](https://github.com/nautechsystems/nautilus_trader/blob/05b709b36edbe9a6a0d26a1bb77677dcb5051856/crates/backtest/src/config.rs#L781-L876)). Its node rejects incompatible venue/instrument/time/book-data configurations before replay
([node.rs](https://github.com/nautechsystems/nautilus_trader/blob/05b709b36edbe9a6a0d26a1bb77677dcb5051856/crates/backtest/src/node.rs#L284-L398)). Timestamp-aligned chunks are deliberately extended so equal-timestamp events are not split
([node.rs](https://github.com/nautechsystems/nautilus_trader/blob/05b709b36edbe9a6a0d26a1bb77677dcb5051856/crates/backtest/src/node.rs#L443-L510)). Its workload test runs the same catalog twice, asserts byte equality and equal canonical digests, and refuses overwrite of the first result
([workload test](https://github.com/nautechsystems/nautilus_trader/blob/05b709b36edbe9a6a0d26a1bb77677dcb5051856/crates/backtest/tests/backtest_node_workload.rs#L277-L327)).

LEAN expresses a backtest as a job packet containing algorithm/project/compile/engine/parameter/
period/resource identity
([AlgorithmNodePacket](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Common/Packets/AlgorithmNodePacket.cs#L24-L138),
[BacktestNodePacket](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Common/Packets/BacktestNodePacket.cs#L28-L93)). It isolates loading and setup before the engine loop
([Loader.cs](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/AlgorithmFactory/Loader.cs#L120-L197)), uses cancellation-aware synchronized time slices
([Synchronizer.cs](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Engine/DataFeeds/Synchronizer.cs#L72-L160)), emits measured progress and intermediate results
([BacktestProgressMonitor.cs](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Engine/Results/BacktestProgressMonitor.cs#L23-L91)), and produces result packets and reports containing charts, orders, P&L, statistics, runtime state, and configuration
([BacktestResultPacket](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Common/Packets/BacktestResultPacket.cs#L25-L179)). Separately, LEAN's optimizer packet declares optimization ID, maximum child concurrency, objective, constraints, parameter definitions and out-of-sample settings
([OptimizationNodePacket.cs](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Optimizer/OptimizationNodePacket.cs#L56-L110)). `LeanOptimizer` maps parameter sets to child backtest IDs, queues above the concurrency limit, counts missing/failed results, publishes updates, and aborts children during cleanup
([LeanOptimizer.cs](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Optimizer/LeanOptimizer.cs#L68-L160),
[result/queue handling](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Optimizer/LeanOptimizer.cs#L234-L300),
[abort/concurrency](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Optimizer/LeanOptimizer.cs#L356-L466)). Its step strategy applies constraints before updating the best solution
([StepBaseOptimizationStrategy.cs](https://github.com/QuantConnect/Lean/blob/ea470761aa7c9908495f42e4637f93a779a254f7/Optimizer/Strategies/StepBaseOptimizationStrategy.cs#L148-L185)). These are specific optimizer components, not evidence that an arbitrary DaxAlgo parameter proposal is already executable.

### DaxAlgo is closer than the UI suggests

DaxAlgo already has canonical TradeIR validation, exact synthetic data binding, a frozen target
admission manifest, compiler/runtime/host identity, a real evaluator/risk/book/portfolio path,
Parquet path/SHA/length binding, worker phases, progress, cancel, and result manifests. The missing
historical bridge is that the worker protocol currently accepts only native or installed-bundle
strategy sources; it cannot yet carry an exact TradeIR artifact reference.

The local boundary is concrete:

- [`TradeIrModuleValidatorV1`](../../src/linux/Core/TradingTerminal.Core/Strategies/Definition/TradeIrModuleValidatorV1.cs#L10-L67)
  already performs pure pre-compilation validation, and
  [`BacktestTradeIrTargetV1`](../../src/linux/Backtest/TradingTerminal.Backtest.Engine/TradeIr/BacktestTradeIrTargetV1.cs#L28-L174)
  pins compiler/runtime/host identities and freezes closed-target admission for the synthetic path.
- The synthetic runner already hashes a stable report projection without engine timing
  ([`HashRuntimeReceipt`](../../src/linux/Backtest/TradingTerminal.Backtest.Engine/TradeIr/TradeIrSimulatedBacktestRunnerV1.cs#L494-L550));
  `EconomicResultDigestV1` generalizes that sound boundary to historical worker artifacts and full
  economic ledgers.
- [`BacktestStrategySource`](../../src/linux/Backtest/TradingTerminal.Backtest.Protocol/BacktestJobContracts.cs#L13-L17)
  contains only `Native` and `InstalledBundle`; `TradeIr` is not a worker source yet.
- [`BacktestInputReference`](../../src/linux/Backtest/TradingTerminal.Backtest.Protocol/BacktestJobContracts.cs#L127-L169)
  binds a Parquet path, byte length, SHA-256 and free-form schema/provenance strings, but no trusted
  instrument identity or ingestion receipt.
- [`ParquetMarketDataFeed`](../../src/linux/Backtest/TradingTerminal.Backtest.Worker/ParquetMarketDataFeed.cs#L11-L46)
  reads timestamp/bid/ask rows and assigns every row the instrument from `RunSpec`; therefore those
  Parquet bytes alone do not prove which instrument they represent.
- [`BacktestResultManifest`](../../src/linux/Backtest/TradingTerminal.Backtest.Protocol/BacktestJobContracts.cs#L293-L329)
  binds request/engine/strategy/parameter/input/artifact identity plus job and wall-clock times, and
  [`WorkerArtifactPublisher`](../../src/linux/Backtest/TradingTerminal.Backtest.Worker/WorkerArtifactPublisher.cs#L101-L153)
  publishes it last by atomic rename.
- [`BacktestReport`](../../src/linux/Core/TradingTerminal.Core/Backtesting/BacktestReport.cs#L39-L113)
  carries economic series and trades, but `RunSummary` also contains nondeterministic
  `EngineMilliseconds`.

Add a protocol-versioned `TradeIr` source that binds at least:

```text
source candidate hash
canonical module hash and staged relative path
schema version
target profile and revision
expected data-binding-manifest hash
compiler, runtime, and execution-host artifact hashes
```

Worker order must be:

```text
verify request and staged module
  -> canonical deserialize and rehash
  -> package validate
  -> verify trusted HistoricalDataBindingManifestV1
  -> open and hold its exact staged Parquet input
  -> verify bytes, physical/event schema, row bounds, range, and manifest equality
  -> freeze target admission
  -> compile only the admitted module
  -> replay through evaluator, risk, book, and portfolio
  -> compute EconomicResultDigestV1
  -> atomically publish report and unique run receipt
```

### Historical data identity requires a trusted manifest

A file hash proves byte identity, not market identity. Introduce
`HistoricalDataBindingManifestV1`, issued by a configured trusted ingestion/catalog boundary rather
than copied from model text or a user-supplied `Provenance` string. Its canonical bytes bind:

```text
manifest schema/version and dataset ID
canonical instrument ID, venue, asset class, quote/base currency
event kind, logical schema ID/version/hash, interval and ordering policy
timezone/session calendar and [from, to) coverage
revision/as-of policy and source/ingestion-receipt hash
materialized relative path, byte length and SHA-256
Parquet physical-schema hash, row count, min/max timestamp
producer/catalog ID, version, implementation hash and trust-key ID/signature
```

The client stages the manifest and file together. The worker verifies an allowlisted issuer/signature
(or an equivalently authenticated local catalog receipt), canonical manifest hash, materialized file
bytes, Parquet schema and observed row/time bounds, then requires exact instrument/schema/interval/
session equality with `AuthoringFactsV1` and containment of the requested
`ComparisonScenarioV1` range. A hash cannot prove the external world's truth, so the trust claim is
precisely “these bytes were attributed to this instrument by this pinned ingestion producer,” not
“the market data is infallible.” Until this manifest exists, current Parquet runs must not display
`instrument verified`.

### Repeatability uses an economic digest, not a whole receipt hash

`EconomicResultDigestV1` is the SHA-256 of a versioned canonical projection containing the canonical
run-identity hash and deterministic economic outputs: ordered simulated-time equity/balance/
drawdown samples; ordered orders, fills, positions and round trips (required lists, empty when none);
cash, quantity, fees, spread/slippage and per-instrument attribution; and a closed, sorted set of
stable metrics and economics-affecting warnings. The projection defines decimal/float encoding,
negative zero, non-finite-value rejection, timestamps, key ordering and list tie-breakers.

The current report exposes round trips and equity but not complete order/fill/position ledgers, so
Phase 1 must extend the published economic artifact before claiming this digest or reconciliation.

It explicitly excludes job ID, real start/completion timestamps, engine milliseconds, machine/PID,
working paths, progress/heartbeat messages, wall-clock duration and backend telemetry. Those values
belong in a unique `BacktestRunReceiptV1`, which binds terminal status, request/config/data/module/
engine hashes, artifact descriptors, the `EconomicResultDigestV1`, and execution telemetry. Two
executions with different job IDs should therefore have distinct receipt hashes but the same
economic digest when deterministic inputs and engine identities are identical. This corrects the
otherwise impossible requirement to make the current full report/manifest byte-identical even
though `CompletedUtc` and `EngineMilliseconds` vary.

The initial historical target should remain narrow: one graph, one portable instrument, QuoteL1,
one verified Parquet source, explicit UTC range, current EMA/decision/quantity/market-order target,
and explicit capital/fill/slippage/fee/risk/seed settings. Keep the existing synthetic action named
**Quick synthetic smoke**; add a different **Prepare historical backtest** action.

## Lane 4: CSP Events

Point72 CSP builds a typed graph first and runs it later. Graph construction, dependency collection,
and pruning are separate from engine execution
([CSP Graph](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/docs/wiki/concepts/CSP-Graph.md#L9-L103)). Nodes have typed edges, state, start/stop hooks, history, and alarms
([CSP Node](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/docs/wiki/concepts/CSP-Node.md#L10-L104)). The same graph model supports simulation and realtime modes with explicit push behavior
([Execution Modes](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/docs/wiki/concepts/Execution-Modes.md#L1-L57)). Its runtime normalizes time, builds before executing, and creates a fresh engine for a run
([runtime.py](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/csp/impl/wiring/runtime.py#L19-L113)); threaded runtime exposes stop, join, results, and propagated errors
([threaded_runtime.py](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/csp/impl/wiring/threaded_runtime.py#L49-L103)). CSP also provides graph visualization and runtime profiling
([Graph Utilities](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/docs/wiki/api-references/Graph-Utilities-API.md#L10-L96),
[Profiling](https://github.com/Point72/csp/blob/3ab39b299e8605419486c53f10f33a87c6c41363/docs/wiki/how-tos/Profile-CSP-Code.md#L12-L76)).

CSP is a general event-graph engine, not a trading backtest/report system. The example trading P&L
graph demonstrates calculations, not broker fills, portfolio accounting, costs, or a DaxAlgo result
contract. DaxAlgo should pin CSP in an isolated worker and own everything outside the graph:

1. generated source may declare `@csp.node` and `@csp.graph` but may not call `csp.run`;
2. the host binds approved adapters, time range, PushMode, and historical inputs;
3. the graph emits typed signals/intents and inspectable outputs only;
4. the host owns stop/timeout, risk, simulated order book, fills, costs, portfolio, and report;
5. UI shows the built/pruned graph, node states, timestamps, and profiling diagnostics;
6. the exact source, Python, CSP wheel, bridge, adapter, data, and run hashes are preserved.

A direct CSP-to-intent adapter creates a CSP-native historical simulation, not a canonical TradeIR
backtest. It must be labeled and stored under that proof boundary.

### Canonical Python/CSP comparison requires constrained profiles

Arbitrary Python is not deterministically lowerable in general. A canonical Python path must define
a stricter `VibePythonCanonicalSubsetV1`, separate from the free-form review/native-simulation
profile. It permits a closed expression/statement grammar over host events, typed parameters and
bounded state; a fixed allowlist of pure indicators and signal/target actions; and no dynamic import,
reflection, metaprogramming, I/O, concurrency, exceptions as control flow, or unknown calls. The
lowerer parses the AST, resolves types/units/time semantics, maps **every** material source span to a
TradeIR node/edge/parameter, and emits `PythonToTradeIrReceiptV1`. One unsupported or unmapped
material AST node returns a stable source-span issue and no graph. Running a Python bridge and
observing similar outputs is test evidence, not a conversion receipt.

Likewise, canonical CSP requires `CspCanonicalSubsetV1`: a graph composed only from pinned,
host-registered node templates and adapters with declared type, state, alarm, timestamp and PushMode
semantics. Arbitrary Python inside `@csp.node` is excluded. The host builds and canonicalizes the CSP
graph, resolves every edge and node template, and the lowerer emits one TradeIR graph plus a complete
node/edge-to-TradeIR `CspToTradeIrReceiptV1`; unknown nodes, custom bodies, feedback/state semantics,
or PushMode behavior without an exact TradeIR equivalent fail closed.

Both receipts bind source hash, `AuthoringFactsV1`, installed source-profile/catalog, lowerer
implementation and target-registry hashes, canonical module hash, and full coverage map. Same inputs
must produce identical module and receipt bytes. If product design keeps Python/CSP unrestricted,
then canonical comparison is intentionally scoped to Graph and the supported Rules subset; Python
and CSP remain in separately labeled native-result panels and never appear in a four-lane same-engine
leaderboard.

## Unified Vibe Quant lifecycle

The system needs three explicit state machines rather than one overloaded `DRAFT` label.

### A. Generation and interpretation

```text
Brief
  -> shared-facts preflight
  -> Needs facts | Facts confirmed
  -> four independent model requests
  -> parse lane-native response
  -> host constructs canonical candidate
  -> native contract validation
  -> Reviewable | Blocked
```

Each lane row should expose real stages only:

```text
Preparing -> Waiting for model -> Parsing artifact -> Validating contract -> Ready | Blocked
```

No percentage or ETA is shown for an opaque provider request. Completed lanes become inspectable
immediately, while selection/persistence waits for the complete batch if that remains a consistency
requirement. No lane request starts until the common facts used by all four artifacts are confirmed;
the UI must not show a four-lane generation board while the real state is still `Needs facts`.

### B. Resolution and executable conversion

```text
Reviewable
  -> verify immutable AuthoringFactsV1 equality
  -> resolve lane references and deterministic bridge support
  -> create ComparisonScenarioV1 + strategy-specific BacktestRunConfigV1
     and trusted historical-data binding
  -> Native preview available? ------------------------------+
  -> deterministic importer/lowerer (when installed)         |
  -> canonical TradeIR                                       |
  -> package + capability validation                         |
  -> Backtest ready | Blocked with exact next action         |
                                                               |
  +-----------------------------------------------------------+
```

Readiness is a matrix, not one badge:

| Gate | Meaning |
|---|---|
| Artifact | Exact source/JSON is present and inspectable |
| Format | Lane-native closed contract passed |
| Interpretation | User reviewed what the system understood |
| Shared authoring facts | Confirmed instrument/schema/interval/session/decision semantics match the artifact |
| Importer/lowerer | Installed deterministic bridge supports every requested semantic |
| Canonical | Exact TradeIR hash exists |
| Comparison scenario | Range, capital, execution/cost/risk assumptions and seed are immutable and strategy-neutral |
| Run config | Exact strategy/module/parameter lineage binds one comparison scenario |
| Data | Trusted manifest, immutable snapshot, schema and range are admitted |
| Target | Runtime/compiler/host capability profile admits the graph |
| Native evidence | Optional lane-native smoke or historical simulation is available and labeled |
| Historical | Exact canonical candidate is ready for DaxAlgo worker |

### C. Historical run

```text
Needs setup
  -> Data preflight
  -> Admitting
  -> Ready
  -> Queued
  -> Validating
  -> Loading data
  -> Warming up
  -> Simulating
  -> Aggregating
  -> Publishing
  -> Completed | Failed | Canceled
```

Progress uses actual phase, processed/total events, simulated timestamp, monotonic percentage when
the denominator is known, warning count, and Cancel. `100%` appears only after atomic result
publication.

## Recommended screen design

### 1. Understand

Show the cumulative brief, facts extracted from it, assumptions, and material unresolved questions.
The user answers the minimum execution-defining questions once for the whole batch. Every value is
marked Proposed, Confirmed, or Missing, and **Build 4 candidates** remains disabled until the
required common subset is confirmed. `AuthoringFactsV1` contains:

- instrument, venue, asset class, currency;
- data kind and timeframe;
- session calendar, timezone, prior-day boundary;
- signal and confirmation definition;
- decision/fill timing;
- strategy-level exit, sizing, and risk semantics.

Fees, spread/slippage model, capital, benchmark, historical range, warmup and seed are shown later
under **Configure this run** and become one strategy-neutral `ComparisonScenarioV1`, not silently
confirmed generation facts. Each exact canonical strategy then receives its own
`BacktestRunConfigV1`, which binds that strategy to the shared scenario and resolved runtime
lineage.

### 2. Compare four interpretations

Use four equal cards with the same anatomy:

```text
[Vibe Python]              READY FOR REVIEW
What it understood         5-minute close breakout + volume filter
Artifact                   strategy.py · sha256:…
Format                     valid
Native preview             not installed
Canonical backtest         blocked: Python importer missing
[Inspect code] [Review interpretation] [Configure run]
```

Clicking a card always shows its complete source/JSON, semantic summary, assumptions, unresolved
questions, parameter axes, proposed tests, diagnostics, and exact hash. Preview and active editor
states remain distinct. Blocked content stays inspectable; its classified issue determines whether
the next action is syntax repair, fact review, semantic refinement, capability installation, or
environment setup.

### 3. Build

Show an evidence pipeline for the selected candidate rather than a paragraph of orange errors:

```text
Artifact       PASS   exact Python source saved
Format         PASS   Vibe Python v1 shape
Shared facts   PASS   exact AuthoringFactsV1 hash matches
Importer       BLOCK  no trusted Python bridge installed
Canonical IR   —
Run config     NEEDS  choose range, capital and cost model
Data           NEEDS  select a trusted historical snapshot
Runtime        —
```

Every blocker has one adjacent action. Do not offer “Load smoke starter” as a substitute for the
user's momentum strategy; that tests a different artifact. A smoke example belongs in a separate
Examples action.

### 4. Backtest

Keep the Backtest tab visible for every candidate. Disabled state must say exactly why and what the
next supported action is. For an admitted Graph candidate, the tab becomes a setup sheet:

- exact strategy/canonical hashes;
- instrument, venue, currency, data snapshot, schema, range, timezone, session, warmup;
- initial capital, sizing constraints, fill price, spread, slippage, fees, partial-fill behavior;
- end-of-run open-position policy, benchmark, seed;
- compiler/runtime/host identities.

The primary action is **Run historical backtest**. **Quick synthetic smoke** remains a clearly
different developer/compatibility proof.

### 5. Results and iteration

Results use Summary, Equity/Drawdown, Trades/Orders, Rule/Node trace, Diagnostics, Assumptions, and
Provenance tabs. Show cost decomposition, data warnings, warmup, final open position, and exact run
hashes. Selecting a trade should trace to the source rule/node, input event, intent, risk decision,
simulated order, and fill.

`Fork and compare` creates a new immutable artifact and run. A comparison table must show strategy,
data, run assumptions, and engine identity before performance metrics, so different inputs are not
presented as an apples-to-apples result.

## Contract boundaries to implement

### Model-facing response

The compact response should carry only review metadata and native content:

```json
{
  "title": "5-minute momentum breakout",
  "interpretation": "...",
  "assumptions": [],
  "unresolvedQuestions": [],
  "parameters": [],
  "variationAxes": [],
  "explanation": "...",
  "proposedTests": [],
  "artifact": "<Python/CSP source, or native JSON object for Rules/Graph>"
}
```

The host derives all envelope and package values from the request and installed catalog. Legacy
full envelopes may be read during migration, but their host-owned fields have no authority.

The user message accompanying this compact response must also include one host-resolved
`AuthoringFactsV1` object and hash. Models may use those facts in their native artifact, but cannot change their authority. The
host revalidates every resulting instrument/schema/time reference against that object; a mismatch is
a lane-content error, not a new proposed fact.

### Executable bridge

All lane bridges should implement the same conceptual result:

```text
Resolve(source hash, host facts, installed capabilities)
  -> Resolved | Issues

Convert(resolved source, exact bridge identity)
  -> Canonical TradeIR + trace receipt | Unsupported issues
```

Identity conversion is valid for Graph. Rules uses a deterministic lowerer. Python and CSP may
first support native preview; any later conversion must be explicit and provenance-bound. AI
synthesis produces a **new proposal**, never a deterministic conversion receipt.

### Executable parameter binding

Candidate `parameters` and `variationAxes` are review metadata until an installed bridge proves how
each value changes executable semantics. A versioned `CanonicalParameterBindingV1` set must bind:

```text
source candidate/resolved-source hash
base canonical module hash + AuthoringFactsV1/catalog hashes
stable parameter ID, value kind, unit, domain/choices and canonical literal encoding
exact target: TradeIR node ID + parameter name (or a versioned runtime parameter port)
binder ID/version/implementation hash and target-registry hash
```

Targets are unique and typed; arbitrary JSON Pointer patches are not accepted. Structural choices
that add/remove operators require a new authored artifact and conversion receipt rather than being
misrepresented as scalar parameters. Applying a canonical `ParameterVectorV1` validates every
type/unit/domain constraint, orders values by stable ID, rewrites only declared targets, revalidates
the whole module, and returns a new child module hash plus
`ParameterApplicationReceiptV1(baseModuleHash, bindingSetHash, vectorHash, childModuleHash)`.
There is no model call.

Graph bindings are authored against its exact nodes. The Rules lowerer must emit parameter mappings
as part of its full trace receipt. Python/CSP native simulators can have separate native parameter
bindings, but their children are not canonical-comparable until their deterministic lowerer emits
the canonical binding set. An experiment scheduler may sweep only parameters with valid bindings;
an outer-envelope proposal with no target is display-only.

### Historical worker source

Add a versioned TradeIR source reference to the existing worker protocol rather than creating a
second engine. The result manifest should bind candidate, module, definition, admission, compiled
plan, compiler, runtime, host, `AuthoringFactsV1`, `ComparisonScenarioV1`, `BacktestRunConfigV1`,
`HistoricalDataBindingManifestV1`, runtime receipt, request, input, report, canonical parameter
application, seed and `EconomicResultDigestV1` hashes. Job/time/machine telemetry stays in the unique
run receipt and outside the repeatability digest.

## Delivery sequence and stop conditions

### Phase 0A — reliable authoring transport

- compact lane-native model response;
- host-owned identity and package binding;
- old-envelope migration reader;
- immediate raw/native artifact inspection;
- classified issue/repair routing with a closed one-repair allowlist;
- full `GenerationProvenanceV1` for every initial and repair request;
- adversarial tests proving a model cannot mutate trusted fields.

Stop condition: four valid compact fixtures survive parse, host wrapping, native validation, save/
restore, and hash verification; malformed native content remains a precise lane failure; missing
facts/capabilities/environment never invoke repair; syntax-only repair creates a second provenance
receipt linked to the first.

### Phase 0B — shared-facts preflight

- host-owned immutable `AuthoringFactsV1` contract and resolver;
- Proposed/Confirmed/Missing fact UI using brief and current-chart suggestions;
- exact instrument, schema/catalog, interval, timezone/session, and decision-time binding;
- all four prompts receive the same confirmed facts;
- lane artifacts are checked against those facts after parsing;
- separate immutable `ComparisonScenarioV1` and strategy-specific `BacktestRunConfigV1` contracts
  are created only during later run setup.

Stop condition: no four-lane artifact request starts with missing common facts, no suggested value is
silently confirmed, and every lane either uses the exact confirmed binding or fails at an actionable
native path.

### Phase 1 — Graph historical vertical slice

- worker protocol `TradeIr` strategy source;
- trusted `HistoricalDataBindingManifestV1` and immutable Parquet materialization;
- immutable strategy-neutral `ComparisonScenarioV1` with exact execution/cost/risk assumptions and
  a strategy-bound `BacktestRunConfigV1`;
- exact admission and worker replay;
- deterministic `EconomicResultDigestV1` plus unique run receipt;
- Backtest setup/progress/results handoff inside Vibe Quant;
- one QuoteL1 EMA graph profile.

Stop condition: two jobs with the same admitted Graph/data/run/engine inputs produce the same
`EconomicResultDigestV1` while retaining distinct job/telemetry receipt hashes; instrument identity
is admitted only through the trusted manifest; every tamper, unsupported capability, cancel, crash,
or stale editor hash fails closed.

### Phase 2 — Rules position-lifecycle subset

- add `strategyKind: positionLifecycle`;
- split model-owned `RulesDraftV1` from host-owned `RulesResolvedV1`;
- exact `AuthoringFactsV1`/catalog equality and `RulesResolutionReceiptV1`;
- reference/type/unit/causality/lifecycle validation;
- deterministic supported-subset lowerer and full trace receipt;
- semantic rule cards and per-trade rule trace.

Stop condition: 100% source semantic coverage in the receipt; unsupported constructs produce no
partial graph and no AI call.

### Phase 3 — Vibe Python native preview and simulation

- pinned isolated runtime and bridge;
- closed imports/resources and completed-event ABI;
- point-in-time/prefix tests;
- DaxAlgo-owned execution accounting and report.

Stop condition: source/runtime/data/run hashes are complete, future leakage is rejected, and worker
failure cannot contaminate the host or sibling runs.

### Phase 4 — CSP native preview and simulation

- pinned CSP wheel and isolated worker;
- host-owned adapters and `csp.run` prohibition in generated code;
- graph visualization, pruning, node timing, stop/join/errors;
- DaxAlgo intent/risk/book/portfolio adapter.

Stop condition: deterministic graph build/replay with exact PushMode/timestamp semantics and no raw
CSP result mislabeled as a trading backtest.

### Phase 5 — canonical parameter binding and scoped experiments

- executable `CanonicalParameterBindingV1` and application receipts;
- Graph and supported-Rules canonical parameter axes only;
- LEAN-style parent experiment and immutable child runs;
- concurrency, cancel, failure isolation, objective/constraints;
- train/out-of-sample split and compare UI.

Stop condition: every child module is derived by a typed declared target and links to its exact
binding/vector/application receipts and run; native-only Python/CSP results remain visibly outside
the canonical leaderboard; the product never auto-selects the highest in-sample return as a
recommendation.

### Phase 6 — constrained Python/CSP canonical profiles

- versioned `VibePythonCanonicalSubsetV1` and `CspCanonicalSubsetV1` contracts;
- closed AST/node-template catalogs with exact time/state semantics;
- deterministic Python-to-TradeIR and CSP-to-TradeIR lowerers;
- complete source-span/node/edge coverage and canonical parameter mappings;
- fail-closed unsupported diagnostics, equivalence fixtures and prefix tests.

Stop condition: every material source construct maps exactly once and the same source/facts/catalog/
lowerer inputs produce byte-identical TradeIR and conversion receipt; otherwise no canonical graph
is retained. If this phase is rejected as a product constraint, canonical comparison remains
explicitly Graph+Rules only.

### Phase 7 — four-lane canonical comparison

- admit only identity/deterministically lowered TradeIR children;
- require equal facts, comparison-scenario, data-manifest and engine identities before comparison;
- retain lane-native results in separate evidence panels;
- show conversion and parameter lineage beside every metric.

Stop condition: a four-lane same-engine label appears only when all four entries carry valid
identity/lowering, parameter-application and run receipts under the same comparison contract; every
displayed metric links to an immutable child run and economic digest.

## Cross-lane acceptance gates

1. Model-supplied candidate ID, lane, request hash, filename, language, catalog, or package binding
   cannot alter the host-owned canonical candidate.
2. Same native artifact and trusted binding canonicalize to identical bytes and hash.
3. Saved candidates become stale, never silently rebound, after schema/catalog/bridge changes.
4. Each reference, type, unit, instrument axis, and time axis resolves before conversion; each
   executable parameter additionally has an exact canonical target before an experiment.
5. Current-event/future/revised data leakage is rejected by prefix-invariance and timing tests.
6. Unsupported semantics return stable codes and paths; no partial executable is retained and no
   repair model is called. Only closed-allowlist response syntax/native-shape issues receive one
   automatic repair.
7. A model/AI provider is never called during deterministic import/lowering/admission/backtest.
8. Editing after admission invalidates readiness and late output cannot attach to the new hash.
9. Data bytes, length, schema, instrument, interval, range and revision are verified against a
   trusted `HistoricalDataBindingManifestV1` before execution; a free-form provenance string is
   insufficient.
10. Compiler/runtime/host/bridge identity mismatches fail before execution.
11. Equal-timestamp ordering and chunk boundaries are deterministic.
12. Progress is monotonic and terminal; cancellation is idempotent; partial output is not completed.
13. Cash, equity, fees, slippage, orders, fills, positions, and round trips reconcile.
14. Result restore re-verifies manifest and artifact hashes before display.
15. Comparison refuses an apples-to-apples label when data or run assumptions differ.
16. Every native preview and canonical backtest is labeled with its proof boundary.
17. Forking preserves the parent artifact/run and creates new lineage and hashes.
18. No workflow added here introduces live order execution.
19. Missing common facts prevent all four artifact calls; confirming them starts exactly four initial
    calls with one identical host-owned facts hash.
20. A lane that contradicts the confirmed instrument/schema/time binding is blocked at its native
    path and cannot silently create a new fact.
21. Native Python/CSP historical results cannot receive the canonical-comparable badge or enter a
    same-engine leaderboard.
22. Every initial/repair provider call has complete prompt/facts/provider/model/options/raw-response/
    validator provenance; a repair receipt links to its failed parent attempt.
23. Model-owned Rules drafts cannot supply authoritative catalog/schema/instrument identity; only a
    hash-bound `RulesDraftV1` to `RulesResolvedV1` host receipt can enter lowering.
24. Every experiment vector applies through `CanonicalParameterBindingV1`, creates a newly validated
    child-module hash, and retains a deterministic application receipt; unbound proposals cannot run.
25. Repeating identical deterministic inputs with different job IDs yields an identical
    `EconomicResultDigestV1` and different telemetry-bearing run-receipt hashes.
26. Python/CSP enter canonical comparison only through their closed supported profiles and complete
    deterministic lowering receipts; unsupported or unmapped source constructs retain no graph.

## Evidence versus inference

Evidence from pinned source supports the described authoring contracts, data bindings, execution
loops, progress/result structures, and test patterns. Official product documentation supports the
described Composer and Capitalise interactions and stated assumptions.

The following are DaxAlgo design decisions, not claims that a reference product implements them:

- one canonical TradeIR comparison boundary for all lanes;
- exact content-addressed identity for every bridge and run artifact;
- `AuthoringFactsV1`, `ComparisonScenarioV1`, `BacktestRunConfigV1`,
  `HistoricalDataBindingManifestV1`,
  `CanonicalParameterBindingV1`, and `EconomicResultDigestV1` as separate contracts;
- the classified generation repair policy and full provenance receipt;
- the Rules Draft-to-Resolved transition and constrained Python/CSP lowering profiles;
- the proposed UI tabs and readiness matrix;
- the phased DaxAlgo protocol changes;
- deterministic equivalence receipts for supported conversions.

The proprietary parser/compiler/engine internals of Composer and Capitalise remain unknown. Native
Python or CSP compatibility, economic equivalence across lanes, historical profitability, and live
readiness remain unproven until their corresponding implementation and acceptance gates pass.
