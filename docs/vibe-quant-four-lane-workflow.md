# Vibe Quant four-lane workflow

Vibe Quant can ask four independent generation agents to express one strategy brief in four
different authoring formats. This is an authoring and comparison workflow. It does not silently
compile, import, backtest, register, or run any generated artifact.

## Quick start

1. Open **Strategy Studio → Vibe Code → Vibe Quant** and choose **New strategy**.
2. Search or filter the starter gallery, then choose a starter, or type a strategy brief from
   scratch. The 22 curated starters span overlapping family, horizon, topology, market, data, risk,
   and execution axes; they are prompts, not runnable templates and not an exhaustive taxonomy.
3. Keep **Four AI lanes** selected, choose a provider/model, and press **Check & generate**.
4. Open **Candidate**. The live board reports each real request separately; it does not invent a
   model-completion percentage.
5. When generation finishes, select a candidate card to preview it. Review its assumptions,
   unresolved questions, proposed parameters, tests, and exact artifact before choosing
   **Use selected in editor**.

One submission starts four concurrent provider calls:

| Agent | Output | Current success meaning |
|---|---|---|
| VibeAgent | `strategy.py` | Structurally valid editable ordinary Python |
| SpecAgent | `strategy.spec.json` | Structurally valid declarative strategy JSON |
| GraphAgent | `strategy.tradeir.json` | Typed TradeIR JSON accepted by the installed package validator |
| CspAgent | `strategy.csp.py` | Structurally valid editable CSP-style Python |

Results are always presented in Vibe, Spec, Graph, CSP order even if the provider calls finish in a
different order. A failure in one lane does not erase usable results from the other lanes.

## Reading the states

During generation, each lane moves through **waiting**, **generating**, and a terminal state such as
**finished**, **needs attention**, or **canceled**. The `n/4 lanes finished` counter counts terminal
lane events; it is not an estimate of model progress.

After generation:

| State | Meaning | Can be chosen? |
|---|---|---:|
| `GENERATED · NOT PACKAGE-VALIDATED` | The artifact passed its deterministic authoring-shape checks, but this lane has no installed package validator | Yes |
| `PACKAGE VALID · NOT TESTED` | The TradeIR package validator accepted the canonical candidate content/hash | Yes |
| `GENERATED · INVALID` | A response exists, but its structure or closed contract is invalid | No |
| Provider failure | That provider request failed or returned no usable result | No |
| Canceled | The user stopped the turn | No |

The violet **PREVIEW** card is the result currently being inspected. **ACTIVE IN EDITOR** identifies
the exact candidate hash loaded into the editor. Those are separate states: previewing another card
does not silently replace the active artifact.

Choosing or locally revalidating an artifact does not call the model, compile it, import it, or run
it. A local edit receives a new SHA-256 content hash after deterministic revalidation.

## Does it understand the strategy?

The four outputs are evidence of how four agents represented the brief, not proof that any agent
captured the intended trading semantics. Treat **INTERPRETATION**, assumptions, and
**UNRESOLVED QUESTIONS** as the human-review checklist.

An unresolved question means the brief did not establish a material fact, such as:

- instrument, venue, currency, or universe;
- bar interval, session calendar, timezone, and prior-day boundary;
- the exact definition of a sweep, absorption, confirmation, entry, or exit;
- permitted data and point-in-time timing semantics;
- position sizing, risk limits, costs, slippage, and test period.

Answer those questions in a refinement and generate again. A useful brief makes the ambiguity
explicit:

```text
Instrument and venue:
Event/bar input and interval:
Session calendar and timezone:
Signal definition and confirmation:
Entry timing:
Exit and invalidation:
Sizing and risk limits:
Fees/slippage assumptions:
Facts that must remain unresolved rather than guessed:
```

The system deliberately permits unresolved facts instead of encouraging an agent to fabricate a
schema, snapshot, instrument identity, or timing rule.

## Why a Graph candidate can be invalid

GraphAgent targets a closed, typed TradeIR contract. TradeIR rejects unknown properties, wrong JSON
types, unsupported operator ports, and values outside its exact enums rather than coercing them.

For example, the error path
`$.definition.dataRequirements[0].instrumentSelector.references[0].assetClass` means that the value
at that exact field could not be converted to the closed broker-neutral asset-class enum. Canonical
v1 output uses the lowercase values `equity`, `future`, `forex`, `crypto`, `option`, and `index`.
A value such as `futures` or a non-string JSON value is invalid. Casing deviations are noncanonical
and must not be emitted, although the current .NET enum reader may accept them case-insensitively.

This rejection is a guardrail, not evidence that all four requests failed. Continue with a valid
sibling candidate, or refine and regenerate with the exact known market facts. Do not invent an
asset class or schema hash merely to make validation green.

Even a `PACKAGE VALID · NOT TESTED` Graph result proves only that the exact JSON matches the
installed TradeIR authoring/package contract. It does not prove that market data can be bound, that a
runtime target admits it, or that the strategy can be backtested.

## What is and is not proved

The workflow can prove:

- four distinct provider requests were started for one brief;
- each preserved response belongs to its expected lane and request;
- deterministic envelope and lane-specific shape checks passed or failed;
- the exact candidate/edit content hash;
- for Graph only, whether the installed TradeIR package validator accepted that hash.

It does not prove:

- semantic fidelity to the trader's intent or economic validity;
- Python syntax, dependency safety, or a Python/CSP runtime;
- a declarative lowerer or importer;
- data availability, point-in-time correctness, or target admission;
- compilation, package tests, backtest results, profitability, or live-execution safety.

## Stop behavior

Pressing **Stop** marks queued or running lanes **Canceled** and advances the UI generation epoch.
Late provider callbacks and results are ignored, so they cannot repopulate the candidate list after
the stop.

The current one-shot CLI adapter does not prove process-tree termination. A child provider process
may finish after the UI has stopped listening; its output remains ignored. Start a new turn to retry.

## Restoring an older session

The chat and editor files are restored independently from candidate proof. If a saved batch was
created under an older generation or validation contract, Vibe Quant keeps the chat and code but does
not silently rebind its old hashes. The Candidate tab shows **Saved candidates need fresh
generation** and reloads the batch's original brief into the composer when it can recover it. Review
or refine that brief and press **Check & generate** to create a new batch under the current contract.

## Where backtesting works today

The outcome panel at the top of **Candidate** always states whether backtesting is available. After
choosing a result, its detail view also shows four readiness gates:

1. Generated artifact
2. Package validation
3. Importer + runtime
4. Backtest target

For all four generated lanes, **Backtest not ready** is intentionally disabled. Vibe Python,
Declarative Spec, and CSP have no registered importer or runtime. Graph has a package validator, but
its binding also has no importer and still needs data binding and target admission. Opening Backtest
Studio separately does not convert or bind the selected generated artifact.

The only authored route connected to the current runtime and strategy registry is **Expert C#**:

1. Choose **Use Expert code**.
2. Ask the agent to implement the strategy as the terminal's C# `IBacktestStrategy` contract.
3. Review the Code and Diagnostics tabs, then press **Compile & Register**.
4. Review the exact diff and press **Register strategy**.
5. Return to the main strategy catalog and use the clock button for **Quick backtest**, or open
   **Tools → Backtest Studio…**.

This is a separate C# implementation path. Registration does not prove it is semantically identical
to any Python, Spec, Graph, or CSP candidate.

## Planned exact-hash backtest handoff

The generated-candidate button can become active only when the terminal can complete this chain:

```text
selected or revalidated candidate hash
  → registered lane importer
  → runnable strategy handle
  → exact data binding
  → target admission
  → Backtest Studio request bound to the same hash
```

Binding every stage to the selected SHA-256 prevents the terminal from validating one artifact and
backtesting different bytes. Until that importer/runtime handoff exists, the disabled button is the
honest product state.
