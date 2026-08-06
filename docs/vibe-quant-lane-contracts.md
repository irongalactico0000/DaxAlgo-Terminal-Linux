# Vibe Quant lane contracts v1

This document is the normative format specification for the four artifacts produced by Vibe Quant's
parallel strategy-generation lanes. The key words **MUST**, **MUST NOT**, **SHOULD**, and **MAY** are
to be interpreted as described by RFC 2119 and RFC 8174 when, and only when, they appear in capitals.

These are authoring and review contracts. A valid artifact is not automatically importable,
backtestable, profitable, or safe to execute.

The current product implements the compact model response, host-owned candidate envelope,
lane-native structural validation, installed-package validation for Typed Graph, and one narrow
synthetic Graph smoke. `AuthoringFactsV1`, category-aware repair routing, Rules resolution/lowering,
Python/CSP native runtimes and lowerers, and historical generated-candidate execution are target
contracts below and are **not yet implemented**. An unavailable gate MUST stay unavailable rather
than being simulated by an AI rewrite or a different example strategy.

## Contract authority and roles

| Lane | Normative contract | Authority | Semantic role | Canonical target |
|---|---|---|---|---|
| Vibe Python | `vibe-quant/python-strategy/v1` | Vibe Quant | Editable source/review representation | DaxAlgo TradeIR v1, only through a future deterministic importer/lowerer |
| Declarative Rules | `vibe-quant/declarative-rules/v1` | Vibe Quant | Closed declarative source/review representation | DaxAlgo TradeIR v1, through a deterministic supported-subset lowerer |
| Typed Graph | DaxAlgo TradeIR v1 (`trade-ir/module/v1`) | DaxAlgo TradeIR package and its installed operator catalog | Canonical executable-IR candidate | Itself, after exact package and admission checks |
| CSP Events | `vibe-quant/csp-authoring-profile/v1` | Vibe Quant | Inert CSP-style source/review representation | DaxAlgo TradeIR v1, only through a future deterministic supported-subset lowerer |

The contract authority says who defines the artifact's meaning. It is separate from the code that
performs a structural check, the package that may validate it, and any importer or runtime that may
eventually execute it. A validator implementation hash therefore does not turn an authoring profile
into an executable runtime contract.

The formats are related by review lineage, not by shared runtime semantics. Vibe Python,
Declarative Rules, and CSP Events are Vibe Quant-owned ways to inspect an idea. Typed Graph is the
canonical DaxAlgo TradeIR representation. A source profile may eventually enter canonical execution
only through a deterministic, hash-bound importer/lowerer. Optional AI synthesis creates a new
proposal for review; it is not an equivalence-preserving compiler.

## Model response and host-owned candidate envelope

Every lane model returns one compact JSON object containing review metadata and one lane-native
`artifact`. Python/CSP artifacts are direct JSON strings; Rules/Graph artifacts are direct JSON
objects. A model response MUST NOT be responsible for candidate schema, candidate ID, lane, request
hash, package binding, artifact kind, filename, language, or content hash.

After parsing, the trusted host constructs `strategy-generation-candidate/v2` and binds those values
from the original request and installed catalog. For Rules it also binds `strategy.id`; for Graph it
binds `definition.strategyId` and the exact installed `definition.operatorCatalog` before validation
and hashing. During migration the host MAY read the previous full envelope or `source` / `document`
artifact wrappers, but MUST ignore and reconstruct every echoed host-owned field.

The envelope's comparable `parameters[].defaultValue` accepts only a JSON string, number, or boolean
scalar. The host normalizes that scalar to its invariant JSON spelling and serializes it as a string
before canonical hashing and persistence. This accommodates native scalar output from model
providers without making objects, arrays, or unknown properties valid. It does not alter parameter
typing inside the lane artifact: the Vibe, Declarative Rules, TradeIR, and CSP contracts below remain
authoritative for their own documents.

The response MUST contain one root JSON object. The host MAY recover exactly one unambiguous object
from incidental prose or a Markdown fence. It MAY make one repair request only for malformed
transport (`TransportSyntax`) or a mechanically repairable native shape (`DraftShape`) that requires
no semantic choice. That request MUST contain the compact lane contract and exact issue codes,
paths, and messages. `NeedsFacts`, `SemanticContradiction`, `UnsupportedSemantic`,
`FactsMismatch`, `CapabilityBlocked`, `DataOrEnvironment`, `IntegrityOrProvenance`,
`ProviderFailure`, and `Canceled` MUST NOT enter an AI
repair loop. Multiple embedded objects, a second invalid response, cancellation, and provider
failure remain terminal for that attempt. Recovery and repair do not weaken any lane contract or
prove semantic fidelity.

Each issue MUST preserve its category, stable code, native path, message, request/candidate hash,
validator identity, and user/host next action. A repair MUST NOT invent a fact, substitute an
unsupported operator or order type, remove a risk rule, or choose between contradictory semantics.
The current implementation has a conservative partial classifier for known missing TradeIR data
facts, unknown installed-catalog operators, and the currently enumerated Declarative Rules
clock/data-fact paths. Complete category-aware routing for every lane and every category above is
still required before it can claim conformance with this paragraph.

## Authoring facts and run configuration are separate contracts

Before any four-lane generation request, the host MUST construct `AuthoringFactsV1`. It binds the
shared strategy meaning needed by all four artifacts:

- canonical instrument or universe identity, venue, asset class, and currency;
- data kind, interval, canonical event-schema identity, and point-in-time policy;
- session calendar, timezone, prior-period boundary, and decision timing; and
- confirmed signal, entry, exit, sizing, and risk semantics shared by the lanes.

Each value MUST be `Proposed`, `Confirmed`, or `Missing` and MUST record provenance: user/starter/
current-chart/catalog source, canonical resolver and catalog versions, confirmation identity, and
the canonical `AuthoringFactsV1` hash. Suggestions MUST NOT become authoritative without explicit
confirmation. No lane request may start until the required common values are confirmed. All four
requests receive the same facts hash, and each artifact reference is revalidated against it.

`ComparisonScenarioV1` is created during run setup without a source/module hash. It binds the
historical data manifest/range, initial capital, sizing constraints, fill/spread/slippage/fee
assumptions, risk overrides, warmup and end-of-run policy, benchmark, seed, and required target/
engine profile. Any economic input change creates a new scenario hash.

`BacktestRunConfigV1` is created only after an exact canonical TradeIR hash exists. It binds that
module, conversion/parameter lineage, one comparison-scenario hash, and resolved compiler/runtime/
host identities. It MUST NOT modify strategy meaning or the shared scenario. Different strategies
have different run-config hashes; canonical comparison requires equal scenario and admitted data/
target/engine identities, not equal run-config hashes.

Historical dates, a particular materialized dataset, capital, costs, fills, benchmark, seed, and
target choice belong to `ComparisonScenarioV1`, not `AuthoringFactsV1`. Conversely, a run config MUST
NOT be used to supply an instrument, interval, session, or signal fact that was missing during
generation. Lane-specific facts discovered during review may be resolved later, but they cannot
contradict the confirmed common facts or mutate their hash.

## Vibe Python v1

A Vibe Python artifact is one UTF-8 Python module. It MUST declare all of the following top-level
members:

```python
VIBE_QUANT_CONTRACT = "vibe-quant/python-strategy/v1"

PARAMETERS = [
    {
        "id": "lookback",
        "type": "integer",
        "default": 20,
        "description": "Completed intervals used by the signal.",
    }
]

DATA_REQUIREMENTS = [
    {
        "id": "bars",
        "dataKind": "bar",
        "instrument": "review-required",
        "interval": "PT5M",
        "eventTimeBasis": "intervalCloseUtc",
    }
]

def initialize_state():
    return {}

def on_event(event, state, parameters):
    return []
```

The exact contract marker value is required. `PARAMETERS` MUST be a finite sequence of parameter
descriptors, and `DATA_REQUIREMENTS` MUST be a finite sequence that makes the expected market facts
and event-time basis reviewable. Parameter and data-requirement identifiers MUST be unique within
their respective sequences.

`initialize_state()` MUST accept no arguments and return the initial strategy-owned state.
`on_event(event, state, parameters)` MUST accept exactly those three logical inputs and return a
finite sequence of declarative intents or diagnostics. The module MUST NOT start a backtest, broker,
network client, process, thread, timer, or event loop at import time.

Passing the Vibe Python authoring check proves only that the required source profile is present. It
does not prove Python syntax, dependency safety, data binding, intent semantics, or runtime support.
Vibe Python v1 has no deterministic TradeIR lowerer.

## Declarative Rules v1

A Declarative Rules artifact is one UTF-8 JSON document whose `schemaVersion` is exactly
`vibe-quant/declarative-rules/v1`. The machine-readable normative schema is
[vibe-quant-declarative-rules-v1.schema.json](schemas/vibe-quant-declarative-rules-v1.schema.json).

The document is closed: unknown properties are invalid at every object boundary. It MUST contain:

- `strategy`: stable identity, version, display name, and summary;
- `clock`: event-time basis, session calendar, timezone, decision timing, and optional interval;
- `operatorCatalog`: draft semantic-catalog identity, version, and SHA-256; executable resolution
  MUST replace/verify it against a host-installed catalog rather than trust model-authored values;
- `parameters`: typed defaults and explicit bounds or choices;
- `dataRequirements`: instrument selection, event schema, point-in-time semantics, normalization,
  missing-data, and revision policies;
- `indicators`: operator references with named, typed expression inputs;
- `entryRules` and `exitRules`: conditions and order-intent templates;
- `risk`: position/order limits, exposure, protective exits, and end-of-session behavior; and
- `outputs`: explicit references to the indicators or rules exposed by the strategy.

Rules v1 has two lifecycle states; neither state is inferred from an AI-authored label:

1. `RulesDraftV1` is the exact model-authored closed semantic AST retained for review. Its
   instrument, event-schema, and operator-catalog values are requested hints only and have no
   execution authority.
2. `RulesResolvedV1` is a new host materialization. The host resolves or replaces those references
   from the exact confirmed `AuthoringFactsV1`, installed schema registry, and installed semantic
   catalog; reruns all structural and semantic checks; canonicalizes the result; and assigns a new
   SHA-256.

The Draft-to-Resolved receipt MUST bind the draft and resolved hashes, AuthoringFacts hash, every
replaced/resolved source path and canonical target identity, schema/catalog/resolver versions and
hashes, validator implementation hash, and stable ordered issues. Missing or ambiguous resolution
returns `NeedsFacts` or `UnsupportedSemantic` with no `RulesResolvedV1`. A model-supplied catalog or
schema hash MUST NOT be accepted merely because it has the right shape.

JSON Schema validation is necessary but not sufficient. A conforming semantic validator MUST also
reject duplicate identifiers, missing references, expression type mismatches, dependency cycles,
non-causal data access, invalid parameter bounds, and operator ids or ports absent from the bound
catalog. It MUST fail closed when those facts cannot be established.

Declarative Rules v1 is not TradeIR and has no implied importer. Executable conversion MUST consume
only `RulesResolvedV1` and requires a deterministic supported-subset Rules-to-TradeIR lowerer with a
complete source-path-to-node/port receipt. It returns one fully validated graph or stable ordered
unsupported issues and no graph; it MUST NOT emit a partial executable or call a model. Optional AI
synthesis creates a new proposal with a new hash and review boundary; it is not that lowerer and
does not prove equivalence. The current implementation performs only a draft structural check and
does not create `RulesResolvedV1`.

## Typed Graph: canonical DaxAlgo TradeIR v1

Typed Graph emits the DaxAlgo `trade-ir/module/v1` module envelope containing the canonical typed
strategy graph. Its operator ids, versions, ports, literals, clock, data requirements, and outputs
MUST validate against the exact installed TradeIR package and operator-catalog binding recorded with
the candidate. The installed `OperatorGraphModuleV1` type, canonical serializer, operator registry,
and `TradeIrModuleValidatorV1` are authoritative for this current package binding.

Typed Graph is the only v1 lane whose artifact is already in the canonical executable-IR form. Even
then, package validity proves only schema and catalog conformance for the exact artifact bytes. It
does not prove that required data exists, that a target supports every operator, that a runtime is
installed, or that a backtest has run.

## CSP Events authoring profile v1

A CSP Events artifact is one UTF-8 Python module with the exact marker:

```python
VIBE_QUANT_CSP_CONTRACT = "vibe-quant/csp-authoring-profile/v1"
```

It MUST import `csp`, declare at least one `@csp.node`, declare at least one `@csp.graph`, and use
typed `ts[...]` event streams. It MAY use CSP-style alarms, baskets, and graph composition when those
features remain explicit and reviewable. It MUST NOT call `csp.run`, start an engine, connect to a
data source, or perform broker or network I/O. The generated file is intentionally inert.

This profile is owned by Vibe Quant. It is informed by the public
[Point72 CSP project](https://github.com/Point72/csp), but compatibility with Point72 CSP is
**unverified**. No upstream release, commit, Python package version, or conformance suite is pinned
by v1. The marker and a structural profile check MUST NOT be presented as proof that Point72 CSP can
import or run the artifact. CSP Events v1 has no deterministic TradeIR lowerer.

## Proof levels and comparison labels

Implementations and stored results MUST use these four distinct proof levels:

| Proof level | Required evidence | Historical P&L label | Cross-lane comparison |
|---|---|---|---|
| Format validation | Exact native bytes and lane validator identity/result | None | Forbidden |
| Synthetic compatibility smoke | Exact source, pinned bridge/runtime/target, and deterministic synthetic input | Synthetic only | Forbidden |
| Lane-native historical simulation | Exact native source/runtime/adapter, immutable historical data, and run assumptions | **Native simulation** | Not canonical; evaluator semantics may differ |
| Canonical historical backtest | Graph identity or deterministic conversion receipt, exact TradeIR, admitted DaxAlgo data/run/target/engine | **Canonical historical** | Allowed only when all compared admission inputs match |

Displaying P&L does not promote native evidence to canonical evidence. A direct Python/CSP intent
adapter remains a lane-native simulation even if it reuses DaxAlgo's risk, book, fills, costs, and
reporting. Vibe Python and CSP Events MUST NOT receive a canonical-comparable badge or enter a
same-engine leaderboard until a deterministic supported-subset lowerer covers every material source
construct and emits a complete conversion receipt. Unsupported coverage produces no TradeIR.

## Optional AI synthesis proposal

The four lane results are independent source/review artifacts. Even a package-valid Typed Graph lane
remains a candidate until the user reviews its interpretation, assumptions, unresolved questions,
parameters, and exact content hash.

If the user asks to reconcile candidates with AI, that operation MUST create a new synthesis request
containing, at minimum:

1. the exact strategy-brief hash;
2. the ordered lane id, contract id, and SHA-256 content hash of every included candidate;
3. the resolutions to material questions or an explicit record that they remain unresolved;
4. the exact target TradeIR package and operator-catalog binding; and
5. the selected AI provider/model run identity.

The synthesis response MUST be stored as a new TradeIR artifact with its own SHA-256, source-hash
lineage, and validation result. It MUST NOT overwrite a source candidate or reuse a source hash.
Changing any source bytes, question resolution, target binding, or synthesized bytes invalidates the
prior synthesis receipt.

The synthesis receipt MUST bind:

- its own schema and host-owned synthesis identity;
- the strategy id and original batch-prompt hash;
- the synthesis-request hash;
- the ordered source lane, candidate id, contract id/version, and candidate SHA-256 values;
- the exact target TradeIR package/operator-catalog binding;
- the new target-candidate SHA-256; and
- the synthesis agent, provider, and model identities.

The receipt proves lineage and byte identity. It does not prove semantic equivalence, causality,
data availability, runtime support, or profitability.

This is reviewed **AI synthesis**, not deterministic lowering. In particular, ordinary Python and
CSP code cannot be truthfully compiled into equivalent TradeIR by the current system. The model may
reconcile representations, but package validation cannot prove that the synthesized graph preserves
the author's economic intent. The synthesized graph is a fifth proposal and is never a required
bridge for an already canonical Graph candidate. It MUST NOT receive a deterministic-equivalence or
cross-lane-comparable label.

## Canonical parameter binding

Candidate `parameters` and `variationAxes` are review metadata until a deterministic binding proves
where each value enters canonical execution. A parameter may be swept or optimized only through
`CanonicalParameterBindingV1`, which MUST bind:

- source candidate hash and base canonical TradeIR module hash;
- parameter id, native type, unit, allowed domain, and constraints;
- exact target path (`definition.nodes[nodeId].parameters[name]`) or declared runtime parameter port;
- canonical value encoding and binder implementation hash;
- applied value or vector and parent/child lineage; and
- resulting child TradeIR hash and package-validation result.

Applying a parameter vector MUST create a new immutable child module and receipt; it MUST NOT mutate
an admitted base module in place. A proposed parameter with no exact target is not executable and
MUST be excluded from an experiment. A Rules lowering receipt MAY establish the mappings while it
creates canonical nodes. Python/CSP-native grids remain native evidence until a deterministic
lowerer establishes canonical mappings and a child module for every vector.

## Execution admission

A TradeIR artifact MUST remain non-runnable until its exact candidate and persisted module hashes
pass the gates for the selected execution mode. The current bounded mode is
`in_process_synthetic_quote_l1_smoke`:

```text
exact selected candidate hash + exact persisted module hash
  -> installed TradeIR package validation
  -> deterministic synthetic QuoteL1 snapshot hash
  -> closed target/operator and data-binding admission
  -> pinned compiler/runtime/execution-host artifact hashes
  -> in-process evaluator + risk + simulated order book + portfolio
  -> normal BacktestReport + runtime receipt hash
```

This mode MUST report `IsHistoricalData=false` and `IsWorkerIsolated=false`. Its result MUST NOT be
presented as historical performance, profitability, broad target compatibility, package test
evidence, or worker isolation. Vibe Python, Declarative Rules, and CSP MUST remain disabled until an
explicit lowerer/runtime produces its own exact-hash admission proof.

A future historical Backtest Studio run requires the larger chain:

```text
reviewed source hash + AuthoringFactsV1 hash
  -> Graph identity OR deterministic importer/lowerer receipt
  -> exact canonical TradeIR hash
  -> optional CanonicalParameterBindingV1 + exact child TradeIR hash
  -> TradeIR package and operator-catalog validation
  -> authoritative HistoricalDataBindingManifestV1
  -> immutable ComparisonScenarioV1
  -> strategy-specific BacktestRunConfigV1
  -> target/operator capability admission
  -> installed runtime/importer admission
  -> backtest admission receipt
  -> backtest request bound to those same hashes and receipt
  -> unique BacktestRunReceiptV1 + EconomicResultDigestV1
```

Failure or absence at any gate MUST reject that execution mode and identify the missing gate.
Opening Backtest Studio, selecting a candidate, or validating an authoring profile MUST NOT bypass
the historical chain. Live execution requires additional risk, identity, venue, and release
controls and is outside these v1 lane contracts.

An optional synthesis receipt, deterministic conversion receipt, and future backtest admission
receipt are different proofs. Synthesis records which reviewed bytes informed a new AI proposal. A
deterministic conversion receipt proves the exact supported mapping to TradeIR. Backtest admission
additionally binds data, target capabilities, runtime, and run request. Possessing any earlier proof
MUST NOT be treated as possessing a later one.

### Historical data and provenance

`HistoricalDataBindingManifestV1` MUST bind dataset id; canonical instrument, venue, and currency;
data kind and schema hash; interval; timezone/session; start/end range; revision policy;
materialized file hash and length; and source/ingestion artifact identity. The worker MUST verify the
manifest hash, file bytes, Parquet schema/metadata, and range before replay. A Parquet hash and OHLCV
column schema alone do not prove instrument identity; that meaning depends on the trusted ingestion
lineage bound by the manifest.

The admission/run provenance MUST bind, as applicable, candidate/source, canonical module and
definition, AuthoringFacts, Rules Draft-to-Resolved, conversion/identity, parameter-binding, data
manifest, ComparisonScenario, BacktestRunConfig, target profile/revision, compiler, runtime, execution host, worker/
bridge, request, input, report, and seed hashes. Every provenance object MUST name its schema
version and producing implementation identity. Restore MUST reverify these links before results are
shown, and any mismatch MUST fail closed.

### Unique run receipts and deterministic economic results

Each attempt MUST create a unique immutable `BacktestRunReceiptV1`. It binds its job id, lifecycle
and publication timestamps, machine/worker telemetry, complete admission inputs, and an
`EconomicResultDigestV1`. Receipt/report bytes are not expected to be identical across repeated
jobs because those operational fields legitimately differ.

`EconomicResultDigestV1` MUST canonicalize only deterministic economic outputs: ordered
simulated-time equity and cash samples; orders, fills, trades, positions, fees, slippage, and round
trips; stable metrics; and warnings/assumptions that affect economics. It MUST exclude job id,
wall-clock timestamps, engine milliseconds, progress messages, machine identity, and filesystem
paths. Repeatability is proved when identical admitted module, data, parameter, scenario, run-config, target,
and engine inputs produce the same economic digest. Each run receipt remains separately auditable.

## User-visible lifecycle

The intended fail-closed product lifecycle is:

1. Build `AuthoringFactsV1`; keep every suggestion Proposed/Missing until the user confirms the
   shared instrument/data/time/semantic facts required by all four native contracts.
2. Generate the four lane artifacts and show each contract authority and validation state.
3. Review interpretations, assumptions, unresolved questions, parameters, and exact source hashes.
4. Materialize Rules Draft to host-owned Rules Resolved when selected. For Typed Graph, retain
   identity to the exact package-valid TradeIR. For another lane, require its installed deterministic
   importer/lowerer before claiming canonical equivalence. AI synthesis is an optional fifth
   proposal, not this gate.
5. Permit **Run synthetic smoke test** only for an unchanged, package-valid TradeIR artifact. After
   the click, the runner MUST bind and admit its synthetic data, target, runtime, and execution
   evidence against the exact hashes before reporting success.
6. For historical execution, create any `CanonicalParameterBindingV1` child, then bind
   `HistoricalDataBindingManifestV1`, `ComparisonScenarioV1`, and `BacktestRunConfigV1`; admit the exact TradeIR/data/config/
   target/runtime hashes without altering strategy meaning.
7. Keep historical Backtest Studio locked while data, worker, target, or admission-receipt evidence
   is absent. A direct package-valid Graph is the first historical path and requires no synthesis or
   Expert C# rewrite.
8. Publish a unique run receipt and deterministic economic digest; compare lanes only at the
   canonical proof level with matching scenario, data, target, and engine identities.

No UI action may silently replace a hash-bound artifact between these stages.
