# Vibe Quant lane contracts v1

This document is the normative format specification for the four artifacts produced by Vibe Quant's
parallel strategy-generation lanes. The key words **MUST**, **MUST NOT**, **SHOULD**, and **MAY** are
to be interpreted as described by RFC 2119 and RFC 8174 when, and only when, they appear in capitals.

These are authoring and review contracts. A valid artifact is not automatically importable,
backtestable, profitable, or safe to execute.

## Contract authority and roles

| Lane | Normative contract | Authority | Semantic role | Canonical target |
|---|---|---|---|---|
| Vibe Python | `vibe-quant/python-strategy/v1` | Vibe Quant | Editable source/review representation | DaxAlgo TradeIR v1, through reviewed AI synthesis |
| Declarative Rules | `vibe-quant/declarative-rules/v1` | Vibe Quant | Closed declarative source/review representation | DaxAlgo TradeIR v1, through reviewed AI synthesis |
| Typed Graph | DaxAlgo TradeIR v1 (`trade-ir/module/v1`) | DaxAlgo TradeIR package and its installed operator catalog | Canonical executable-IR candidate | Itself, after exact package and admission checks |
| CSP Events | `vibe-quant/csp-authoring-profile/v1` | Vibe Quant | Inert CSP-style source/review representation | DaxAlgo TradeIR v1, through reviewed AI synthesis |

The contract authority says who defines the artifact's meaning. It is separate from the code that
performs a structural check, the package that may validate it, and any importer or runtime that may
eventually execute it. A validator implementation hash therefore does not turn an authoring profile
into an executable runtime contract.

The formats are related by review lineage, not by shared runtime semantics. Vibe Python,
Declarative Rules, and CSP Events are Vibe Quant-owned ways to inspect an idea. Typed Graph is the
canonical DaxAlgo TradeIR representation. A source profile can influence a new Typed Graph artifact
only through the hash-bound synthesis boundary defined below; it is not directly executable merely
because it is structurally valid.

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
- `operatorCatalog`: the exact catalog identity, version, and SHA-256 expected by synthesis;
- `parameters`: typed defaults and explicit bounds or choices;
- `dataRequirements`: instrument selection, event schema, point-in-time semantics, normalization,
  missing-data, and revision policies;
- `indicators`: operator references with named, typed expression inputs;
- `entryRules` and `exitRules`: conditions and order-intent templates;
- `risk`: position/order limits, exposure, protective exits, and end-of-session behavior; and
- `outputs`: explicit references to the indicators or rules exposed by the strategy.

JSON Schema validation is necessary but not sufficient. A conforming semantic validator MUST also
reject duplicate identifiers, missing references, expression type mismatches, dependency cycles,
non-causal data access, invalid parameter bounds, and operator ids or ports absent from the bound
catalog. It MUST fail closed when those facts cannot be established.

Declarative Rules v1 is not TradeIR and has no implied importer. Any conversion into TradeIR is a
new synthesis operation with a new hash and review boundary.

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

## Combining reviewed candidates

The four lane results are independent source/review artifacts. Even a package-valid Typed Graph lane
remains a candidate until the user reviews its interpretation, assumptions, unresolved questions,
parameters, and exact content hash.

Combining candidates MUST create a new synthesis request containing, at minimum:

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
the author's economic intent. That equivalence remains a human-review and test obligation.

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
source hashes + reviewed resolutions
  -> synthesis receipt + new synthesized TradeIR hash
  -> TradeIR package and operator-catalog validation
  -> authoritative point-in-time data binding
  -> target/operator capability admission
  -> installed runtime/importer admission
  -> backtest admission receipt
  -> backtest request bound to that same hash and receipt
```

Failure or absence at any gate MUST reject that execution mode and identify the missing gate.
Opening Backtest Studio, selecting a candidate, or validating an authoring profile MUST NOT bypass
the historical chain. Live execution requires additional risk, identity, venue, and release
controls and is outside these v1 lane contracts.

The synthesis receipt and future backtest admission receipt are different proofs. The synthesis
receipt says which reviewed source bytes produced which TradeIR bytes. The backtest admission
receipt must additionally say which exact data binding, target capabilities, importer/runtime, and
backtest request were admitted for those TradeIR bytes. Possessing the first MUST NOT be treated as
possessing the second.

## User-visible lifecycle

The intended fail-closed product lifecycle is:

1. Generate the four lane artifacts and show each contract authority and validation state.
2. Review interpretations, assumptions, unresolved questions, parameters, and exact source hashes.
3. Make one additional AI synthesis request over the explicitly included source hashes.
4. Validate the new TradeIR artifact and show its synthesis receipt without altering the sources.
5. Permit **Run synthetic smoke test** only for an unchanged, package-valid TradeIR artifact. After
   the click, the runner MUST bind and admit its synthetic data, target, runtime, and execution
   evidence against the exact hashes before reporting success.
6. Keep historical Backtest Studio locked while historical data, worker, or admission-receipt
   evidence is absent.

No UI action may silently replace a hash-bound artifact between these stages.
