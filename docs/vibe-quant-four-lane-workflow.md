# Vibe Quant four-lane workflow

Vibe Quant can ask four independent generation agents to express one strategy brief in four
different authoring formats. This is an authoring and comparison workflow. It does not silently
compile, import, backtest, register, or run any generated artifact.

The exact format rules and their owners are defined in the
[Vibe Quant lane contracts v1](vibe-quant-lane-contracts.md). The machine-readable Declarative
Rules contract is [JSON Schema Draft 2020-12](schemas/vibe-quant-declarative-rules-v1.schema.json).

## Current implementation versus intended product

The current implementation starts four real model requests, reconstructs the trusted candidate
envelope in the host, keeps invalid native output inspectable, validates Typed Graph against the
installed TradeIR package, and can run one narrow exact-hash synthetic Graph smoke. It does **not**
yet implement the shared-facts preflight, Rules resolution/lowering, Python or CSP runtimes,
historical TradeIR worker source, historical data admission, parameter sweeps, or historical result
view described later in this document.

Those future stages are normative design, not hidden functionality. Until each gate exists, the UI
must say **not available** with the missing gate; restarting the app, choosing Expert C#, loading a
different smoke example, or asking an AI to rewrite the artifact does not make the selected
candidate historically backtestable.

## Quick start

1. Open **Strategy Studio → Vibe Code → Vibe Quant** and choose **New strategy**.
2. Search or filter the starter gallery, then choose a starter, or type a strategy brief from
   scratch. The 23 curated starters span overlapping family, horizon, topology, market, data, risk,
   and execution axes; they are prompts, not runnable templates and not an exhaustive taxonomy.
3. For the known-supported synthetic test path, search for `smoke` and choose
   **QuoteL1 EMA crossover · smoke compatible**. Other starters may produce package-valid graphs,
   but the current closed smoke target can still reject their data or operators.
4. Keep **Four AI lanes** selected, choose a provider/model, and press **Check & generate**.
5. Open **Candidate**. The live board reports each real request separately; it does not invent a
   model-completion percentage. As soon as one lane reaches **ready** or **blocked**, select that row
   to inspect its exact source/JSON or raw invalid response while the other lanes keep running.
6. When all four lanes finish, select any candidate card. Its exact read-only artifact appears
   immediately below the four cards; then review its assumptions, unresolved questions, proposed
   parameters, tests, and hash before choosing **Use selected in editor**.
7. The Candidate action panels deliberately separate **Generate / Regenerate** from
   **Test · synthetic only**. Testing requires **Graph · Typed → Use selected in editor → Run
   exact-hash synthetic smoke**; it never reruns the generation agents.

One submission starts four concurrent initial provider calls, one for each lane:

| Agent | Contract authority | Output | Role and current success meaning |
|---|---|---|---|
| VibeAgent | Vibe Quant: `vibe-quant/python-strategy/v1` | `strategy.py` | Editable source/review Python that passed its deterministic shape check |
| SpecAgent | Vibe Quant: `vibe-quant/declarative-rules/v1` | `strategy.spec.json` | Closed declarative source/review JSON that passed its deterministic structural check |
| GraphAgent | DaxAlgo TradeIR package: `trade-ir/module/v1` | `strategy.tradeir.json` | Canonical typed-IR candidate accepted by the installed package/catalog validator |
| CspAgent | Vibe Quant: `vibe-quant/csp-authoring-profile/v1` | `strategy.csp.py` | Inert CSP-style source/review Python that passed its deterministic shape check |

Results are always presented in Vibe, Spec, Graph, CSP order even if the provider calls finish in a
different order. A failure in one lane does not erase usable results from the other lanes.

Each model returns only review metadata and its lane-native source or JSON. The trusted host creates
the candidate ID, lane, request hash, canonical filename/language, and exact package binding before
validation and hashing. A model does not have to reproduce DaxAlgo's internal candidate record and
cannot acquire authority by echoing or changing one of those host fields.

The current transport may make at most one separate repair request after an invalid initial
response. It already stops without retry for known missing TradeIR data facts, operators absent
from the installed TradeIR catalog, and the currently enumerated Declarative Rules clock/data-fact
paths. Its conservative classifier is not yet the complete cross-lane taxonomy: the intended
product permits the extra call only for malformed transport or mechanically repairable draft shape,
using exact deterministic issue codes, paths, and messages. Missing facts,
unsupported semantics, capability/data/environment blockers, contradictions, provider failures,
and cancellation require their own user or host action and MUST NOT enter an AI repair loop. A
first-pass-valid turn still makes exactly four model calls.

"Contract authority" identifies who defines the format. It does not identify an installed runtime.
Vibe Quant owns the Vibe, Rules, and inert CSP authoring profiles. DaxAlgo's installed TradeIR
package owns Typed Graph. The CSP profile is informed by Point72 CSP, but compatibility with a
specific Point72 release is unverified and must not be inferred from the name.

## Reading the states

During generation, the Candidate tab is a four-row job board. Each lane moves through the exact
host-observable boundaries **preparing request**, **waiting for model**, **parsing response**, and
**validating artifact**. A rejected first response may then show **repairing response** before the
same parse-and-validation gates run once more. The terminal states are **ready**, **blocked**, or
**canceled**. Completed rows update independently while slower requests continue. The `n/4 lanes
finished` counter and elapsed clock are facts; Vibe Quant does not show a percentage or ETA because
the one-shot provider call exposes neither.

Live lane results are deliberately read-only staging data. Seeing one lane early never constructs a
partial candidate batch and never enables selection, persistence, synthesis, testing, or execution.
Only the complete ordered four-lane batch can cross those gates. An invalid response remains
inspectable during the live turn: parsed artifacts show their lane-native source/JSON, while an
unparseable envelope shows the exact raw model response and its blocking diagnostic. Raw invalid
responses are not written into the durable session snapshot.

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
does not silently replace the active artifact. Each committed card is explicitly clickable, and the
selected artifact preview sits directly beneath the two-by-two grid instead of being hidden below
the synthesis and testing sections.

If the composer says **Expert C#**, candidate comparison is temporarily hidden rather than deleted.
Use the prominent **Return to candidates** action; it switches directly to the Candidate tab without
changing the editor artifact or candidate hashes. **Compile & Register** is shown only when every
editor file is C#. A selected Python/JSON source-review artifact instead shows that no importer or
runtime is installed, so it cannot be mistaken for compilable C#.

Choosing or locally revalidating an artifact does not call the model, compile it, import it, or run
it. A local edit receives a new SHA-256 content hash after deterministic revalidation.

## Follow-up turns preserve the strategy

The first four-lane submission becomes the session's durable strategy brief. Later strategy
refinements are appended in order; a later clause supersedes only a directly conflicting earlier
clause. For example, `change the ATR period to 20` retains the original entry, filter, sizing, and
timing requirements while replacing the earlier ATR period. The cumulative brief is saved with the
authoring session and restored after restarting the app.

A short navigation request such as `go to backtest` or the previously observed typo
`gow to backtest` is not strategy logic. Vibe Quant keeps the current batch and hashes, opens the
Candidate test guidance, and makes no provider request. A mixed request containing new strategy
facts, such as `backtest with a 20-period ATR and 1 bp fees`, remains a refinement so those facts are
not silently discarded. Generation and testing remain separate explicit actions.

The host candidate builder also normalizes model-authored scalar parameter defaults. A provider
may return `defaultValue` as a JSON string, number, or boolean; the host stores its invariant scalar
spelling as a string for comparison and canonical hashing. Objects and arrays remain invalid. This
does not coerce the lane artifact itself: each artifact contract still owns its native parameter
types. The prompt describes the expected native format; the host obtains the real `packageBinding`
from its installed catalog rather than asking the model to copy or invent it.

## Response recovery boundary

The provider is still required to return one compact root JSON object containing review metadata and
one lane-native `artifact`. Claude CLI requests add its root-object structured-output flag; other
adapters retain the same host-side parser and validator. For resilience, the host first tries strict
JSON parsing, then accepts exactly one unambiguous JSON object embedded in incidental prose or a
Markdown fence. It does not guess when two objects are present. During migration, the parser may
read the older full candidate wrapper, but it discards and reconstructs every host-owned identity and
binding field.

If that recovered object is malformed JSON or has a mechanically repairable compact-response/native
shape error, the target issue router may make one validation-aware repair request for that lane.
Cancellation propagates through the repair call, provider failures are not retried, and an invalid
repaired response ends as `LANE_JSON_INVALID_AFTER_REPAIR`. Usage from both calls is reported
together. This is bounded output recovery, not an execution retry and not a semantic-correctness
proof. The current conservative router recognizes the two TradeIR cases above, but the remaining
cross-lane categories stay a visible gap until the complete issue router below is implemented.

## Issue routing and the next honest action

Every blocking diagnostic needs a category because “invalid” does not imply “ask the model again.”
The intended routing is:

| Category | Example | Automatic AI repair? | Next action |
|---|---|---:|---|
| `TransportSyntax` | Truncated JSON, fence/prose ambiguity | Once | Reformat the same response, then parse again |
| `DraftShape` | Required native property omitted or wrong primitive type | Once, only when no semantic choice is needed | Repair the exact path, then rerun the same validator |
| `NeedsFacts` | Instrument, interval, timezone, or session is missing | No | Return to shared facts and confirm it |
| `FactsMismatch` | Artifact contradicts a confirmed instrument/schema/time fact | No | Review facts or regenerate from the confirmed hash |
| `SemanticContradiction` | Entry, exit, sizing, or risk clauses conflict | No | User reviews/refines the brief |
| `UnsupportedSemantic` | Requested ATR trail has no supported operator/lowering rule | No | Show the unsupported construct and supported alternatives |
| `CapabilityBlocked` | Runtime, lowerer, importer, or target profile is absent | No | Install/implement the capability or choose a supported lane |
| `DataOrEnvironment` | Dataset, schema, credentials, worker, or package is unavailable | No | Resolve the named data/environment dependency |
| `IntegrityOrProvenance` | Hash, signature, request lineage, or stale-editor mismatch | No | Fail closed and restore/re-admit the exact artifact |
| `ProviderFailure` | Provider timeout/authentication/process failure | No schema repair | User explicitly retries the generation request |
| `Canceled` | User pressed Stop | No | Keep the last committed batch and await a new action |

The issue category, stable code, native JSON/source path, message, candidate/request hash, validator
identity, and suggested next action are preserved together. Only `TransportSyntax` and the
non-semantic subset of `DraftShape` are repairable by the bounded model call. No repair may invent a
shared fact, swap an unsupported operator, loosen a risk rule, or reinterpret a contradiction.

## Four alternatives and the canonical execution boundary

The four cards are alternative interpretations, not executable pieces that can be concatenated.
Typed Graph is already expressed in canonical TradeIR. A package-valid Graph can therefore proceed
directly to data and target admission without being rewritten through Expert C# or combined with
the other three lanes.

Rules has two required states. `RulesDraftV1` is the model-authored semantic AST for review; any
instrument, event-schema, or operator-catalog identities it contains are non-authoritative.
`RulesResolvedV1` is a new host materialization that replaces/resolves those references from the
confirmed `AuthoringFactsV1` and installed catalogs, receives a new hash, and carries a
Draft-to-Resolved receipt. The current implementation stops at a structural draft check and does
not create `RulesResolvedV1`.

A deterministic, fail-closed Rules-to-TradeIR lowerer must consume only `RulesResolvedV1` and emit
either one canonical graph plus a complete source-path-to-node receipt, or stable unsupported issues
and no graph. Vibe Python and CSP first require isolated native preview runtimes; any later canonical
conversion must have an explicit, provenance-bound deterministic importer/lowerer. Only Graph
identity or a deterministic conversion receipt can support an equivalence claim.

The existing optional **Synthesize valid drafts → TradeIR** operation is a separate AI proposal:

```text
reviewed source candidates and hashes
  -> one additional model request
  -> one new proposed TradeIR artifact and hash
  -> installed TradeIR validation
  -> synthesis provenance receipt
```

It never overwrites its sources and does not prove that it preserves their trading semantics. Its
result must be reviewed as a fifth candidate. It is not a prerequisite for running an already valid
Graph candidate and must not be presented as the deterministic bridge for Python, Rules, or CSP.

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

### Three immutable contracts, at different times

The target workflow separates authoring meaning from run economics:

| Contract | When it is created | What it controls | What it must not contain or imply |
|---|---|---|---|
| `AuthoringFactsV1` | Before any of the four artifact requests | Canonical instrument/universe identity, venue, asset class, currency, data kind and interval, event schema, session calendar, timezone, prior-period boundary, decision timing, and the confirmed semantic clauses shared by all lanes | No historical date range, data-file choice, starting capital, fill model, fee/slippage model, benchmark, or claim that a backtest is admitted |
| `ComparisonScenarioV1` | During run setup, independent of any one strategy | Historical data manifest/range, initial capital, sizing constraints, fill/spread/slippage/fee assumptions, risk overrides, warmup/end-of-run policy, benchmark, seed, and required target/engine profile | No source/module hash and no change to strategy meaning |
| `BacktestRunConfigV1` | After one exact canonical TradeIR hash exists | That exact module, conversion/parameter lineage, one comparison-scenario hash, and resolved compiler/runtime/host identities | No silent rewrite of either the strategy or shared scenario |

Every `AuthoringFactsV1` value is visibly `Proposed`, `Confirmed`, or `Missing`. Its provenance binds
the source (`user`, `starter`, `current-chart suggestion`, or installed catalog), canonical resolver
and catalog versions, who/what confirmed it, and a canonical facts hash. A chart or starter may
propose a value, but only explicit confirmation makes it authoritative. **Build 4 candidates** stays
disabled until the required common values are confirmed; then all four prompts receive the same
facts hash and all four outputs are checked against it.

The current release has no `AuthoringFactsV1` gate and instead surfaces unresolved questions after
generation. That is why an underspecified Graph can currently fail with an empty data requirement.
The target behavior is to classify that state as `NeedsFacts` before the four calls, not to ask an AI
to guess or repair it. Facts that become necessary only for a particular lane or for execution are
resolved later without rewriting the already-confirmed common facts.

Accordingly, a post-generation Build panel must show **Shared authoring facts: PASS** separately
from **Lane/run facts: needs setup**. It must not show the same instrument/interval/session fact as
both confirmed before generation and missing afterward. Later blockers should name genuinely
run-specific values such as the historical snapshot, date range, commission model, or benchmark.

Changing a date, fee, seed, fill assumption, or other economic input creates a new
`ComparisonScenarioV1` hash. Each strategy gets a distinct `BacktestRunConfigV1` that binds its exact
module/parameter lineage to that scenario. Comparison requires equal scenario and admitted data/
target/engine identities, not equal strategy-bound config hashes.

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

## Four proof levels

Vibe Quant must keep these evidence levels separate in cards, buttons, stored results, and
comparisons:

| Proof level | What passed | May display P&L? | Canonically comparable across lanes? |
|---|---|---:|---:|
| Format validation | Exact native bytes passed the lane's closed shape/structural validator | No | No |
| Synthetic compatibility smoke | A pinned narrow input passed one exact bridge/runtime/target | Not as historical evidence | No |
| Lane-native historical simulation | A native Python/CSP/Rules evaluator used an immutable historical snapshot and explicit assumptions | Yes, labeled **native simulation** | No; evaluator semantics may differ |
| Canonical historical backtest | Graph identity or a deterministic conversion receipt produced exact TradeIR, then one admitted DaxAlgo data/execution contract ran it | Yes | Yes, only when comparison scenario, data, target, and engine identities match |

A result never moves upward merely because it has a chart or P&L. In particular, future Python and
CSP native simulations remain useful lane evidence but cannot receive a canonical-comparable badge,
enter a same-engine leaderboard, or borrow the result of a Graph candidate until their supported
source is deterministically lowered to TradeIR with complete trace coverage.

## What is and is not proved

The workflow can prove:

- four distinct initial provider requests were started for one brief, plus no more than one repair
  request for each invalid lane;
- each host-wrapped response belongs to its expected lane and request;
- deterministic compact-response and lane-specific shape checks passed or failed;
- the exact candidate/edit content hash;
- for Graph only, whether the installed TradeIR package validator accepted that hash.

It does not prove:

- semantic fidelity to the trader's intent or economic validity;
- Python syntax, dependency safety, or a Python/CSP runtime;
- a declarative lowerer or importer;
- historical data availability, historical point-in-time correctness, or broad target support;
- profitability, robustness, isolated-worker execution, or live-execution safety.

A successful **synthetic smoke test** adds a smaller, explicit proof: the exact selected TradeIR
module passed the closed target and data-admission gates and completed a deterministic in-process
QuoteL1 replay through the evaluator, risk gateway, simulated order book, and portfolio. The result
is a normal `BacktestReport`, but it is not historical evidence.

## Stop behavior

Pressing **Stop** marks every queued or otherwise nonterminal lane **Canceled** and advances the UI
generation epoch.
Late provider callbacks and results are ignored, so they cannot repopulate the candidate list after
the stop.

When a completed batch already exists, starting a replacement generation keeps that last validated
batch until the replacement fully validates. Canceling, closing the app, or losing the provider does
not replace the committed batch with an empty one. A first-ever generation interrupted before any
response completes still has no candidate artifact to recover. In both cases, the submitted but
uncommitted refinement is saved separately and restored into the composer after Stop or restart.
When an older completed batch is retained, Candidate labels it **PENDING REQUEST NOT APPLIED** and
disables selection, revalidation, unchanged-brief regeneration, and synthetic testing until the
restored refinement is applied with **Check & generate**. If the request is no longer wanted, choose
**Discard pending request** to keep the prior completed batch and hashes without making any provider,
synthesis, or test call. This prevents an old candidate hash from being mistaken for the result of
the newer request.

The current one-shot CLI adapter does not prove process-tree termination. A child provider process
may finish after the UI has stopped listening; its output remains ignored. Start a new turn to retry.

## Restoring an older session

The chat and editor files are restored independently from candidate proof. If a saved batch was
created under an older generation or validation contract, Vibe Quant keeps the chat and code but does
not silently rebind its old hashes. The Candidate tab shows **Saved candidates need fresh
generation** and reloads the batch's original brief into the composer when it can recover it. Review
or refine that brief and choose **Regenerate 4 candidates** to create a new batch under the current
contract. Restore itself never sends an AI request.

When a saved batch still matches the current contract, it is shown as **RESTORED RESULT · NOT A NEW
AI RUN**. Its prior invalid diagnostics remain evidence; installing a newer parser or repair pass
does not retroactively make the old bytes valid. Choose **Generate fresh 4 candidates** to create a
new batch under the current implementation.

## Test a Graph candidate inside Vibe Quant

Vibe Quant now has one deliberately narrow generated-candidate execution path. It applies only to a
package-valid **Graph · Typed** artifact or a package-valid combined TradeIR artifact. To use it:

1. Choose **New strategy**, search for `smoke`, and select
   **QuoteL1 EMA crossover · smoke compatible**.
2. Keep **Four AI lanes** selected and choose **Check & generate**.
3. In **Candidate**, preview the package-valid **Graph · Typed** result. If Graph is invalid or
   blocked, refine and regenerate; a sibling Vibe, Rules, or CSP result cannot enter this runner.
4. Choose **Use selected in editor**. Preview alone is not enough; the editor must still match the
   exact candidate hash.
5. In **Test · synthetic only**, choose **Run exact-hash synthetic smoke**.
6. Read the normal report summary or the exact rejection code and JSON path.

The runner independently recomputes the persisted module hash, creates and hashes a deterministic
synthetic QuoteL1 snapshot, performs exact data and closed-target admission, pins the installed
compiler/runtime/execution-host artifacts, and runs the real TradeIR evaluator and simulated
portfolio path. A mismatched hash, unsupported operator, incompatible data requirement, missing
artifact, or runtime failure returns a fail-closed issue instead of a report.

The scope label is important: `in_process_synthetic_quote_l1_smoke`. It uses no historical dataset
and no isolated worker, and it is not a profitability or robustness claim. The current closed target
supports the installed QuoteL1/EMA/decision/quantity/market-order path. A package-valid graph can
still be rejected when it asks for bars, tape, an unsupported operator, or a material data fact the
runner cannot truthfully bind. The enabled button proves only that the unchanged package-valid hash
can be submitted; synthetic data, closed-target, runtime, and execution checks run after the click.

### Why the 5-minute momentum starter cannot run yet

The momentum brief requires prior-bar highs, a 1.5× rolling volume average, and a ratcheting
ATR(14) stop. The installed Graph catalog currently has bar close and rolling maximum operators, but
does not have the required bar-high, volume-average, true-range/ATR, or ATR-trailing-state operators.
The QuoteL1 smoke target also does not admit five-minute OHLCV data. A Graph result for that brief
must therefore remain blocked instead of substituting a different strategy.

The other three outputs do not bypass that boundary: Vibe Python and CSP are inert source-review
drafts with no registered runtime/importer, and Declarative Rules has no deterministic TradeIR
lowerer. Restarting the app does not change these capabilities. The Candidate tab reports the
selected source-review lane and the Graph lane's exact rejection together, and offers the separate
QuoteL1 EMA starter only for testing the currently installed smoke path.

Generation prompts now require explicit direction, threshold, lookback, filter, exit, sizing, and
timing clauses to remain mandatory in the default artifact. For example, a requested 1.5× volume
filter cannot default to disabled, and a requested ATR trail cannot become an opposite-channel exit.
This reduces model drift, but it is still a prompt/structural check rather than deterministic proof of
economic equivalence.

Vibe Python, Declarative Rules, and CSP remain non-runnable because no deterministic lowerer/runtime
is registered for those formats. They must not borrow the Graph test result. The existing Expert C#
route remains a separate manual reimplementation path:

1. Choose **Use Expert code**.
2. Ask the agent to implement the strategy as the terminal's C# `IBacktestStrategy` contract.
3. Review the Code and Diagnostics tabs, then press **Compile & Register**.
4. Review the exact diff and press **Register strategy**.
5. Return to the main strategy catalog and use the clock button for **Quick backtest**, or open
   **Tools → Backtest Studio…**.

Registration proves only that this separate C# implementation compiled and was registered. It does
not prove semantic identity with any generated candidate.

## Full historical Backtest Studio path is still future work

The in-screen synthetic smoke is intentionally not presented as the full Backtest Studio path. A
historical, worker-isolated generated-candidate run still needs this chain for the exact hash:

```text
reviewed package-valid TradeIR hash + AuthoringFactsV1 hash
  -> optional CanonicalParameterBindingV1 application + child TradeIR hash
  -> authoritative point-in-time HistoricalDataBindingManifestV1
  -> immutable strategy-neutral ComparisonScenarioV1
  -> strategy-specific BacktestRunConfigV1
  -> target/operator capability admission
  -> installed worker protocol and TradeIR runtime admission
  -> historical backtest admission receipt bound to all of the above
  -> Backtest Studio request bound to the same module/config/admission hashes
  -> unique run receipt + deterministic EconomicResultDigestV1
```

The first historical user path should therefore be:

1. Generate and review the four source candidates.
2. Select a package-valid Graph candidate; no synthesis or Expert C# rewrite is required.
3. Verify the host-owned authoring facts, then bind the required immutable data snapshot and its
   ingestion provenance; a Parquet file hash or column schema alone does not prove instrument
   identity.
4. Choose explicit capital, fill, fee, slippage, risk, end-of-run, benchmark, seed, and target
   settings to create `ComparisonScenarioV1`; bind the selected module to it in a separate
   `BacktestRunConfigV1`.
5. Let the terminal verify target/operator capabilities and the installed worker/runtime.
6. Review the resulting backtest admission receipt.
7. Click **Run historical backtest**; the worker and result view must receive that same hash and
   receipt without regenerating or rewriting the artifact.

Binding every stage prevents the terminal from validating one artifact and backtesting different
bytes. Until the historical data, worker protocol, and admission-receipt handoff exist, Vibe Quant
labels its available action as a synthetic smoke test and does not call it a historical backtest.
Rules, Vibe Python, and CSP reach this path only after their own deterministic lowerer/importer gates
are implemented; an AI-generated substitute cannot establish identity.

### Parameters are executable only through a binding receipt

The candidate's proposed parameter list is comparison metadata, not proof that changing a value
changes the strategy. A parameter is sweepable only after `CanonicalParameterBindingV1` binds:

- source candidate and base TradeIR hashes;
- parameter id, native type, unit, allowed domain, and constraints;
- the exact canonical target, such as `definition.nodes[nodeId].parameters[name]` or a declared
  runtime parameter port;
- binder implementation hash and canonical value encoding; and
- the resulting child TradeIR hash plus validation result for each applied value/vector.

Applying a vector creates a child module and binding receipt; it never edits the admitted base bytes
in place. A parameter with no exact canonical target cannot enter optimization. For Rules, the
lowering receipt may establish this mapping. Python/CSP-native parameter experiments remain native
evidence until a deterministic lowerer establishes the same canonical mapping.

### Historical data and run provenance

`HistoricalDataBindingManifestV1` binds the dataset id, canonical instrument/venue/currency, data
kind and schema hash, interval, timezone/session, start/end bounds, revision policy, materialized
file hash and length, and source/ingestion artifact identity. The worker verifies the manifest hash,
file bytes, Parquet schema/metadata, and range before replay. Instrument meaning ultimately depends
on this trusted ingestion lineage; it must not be inferred from a filename or bare OHLCV columns.

The admission and run provenance also binds the source candidate, canonical module and definition,
AuthoringFacts, conversion/identity and parameter-binding receipts, data manifest,
`ComparisonScenarioV1`, `BacktestRunConfigV1`, target profile/revision, compiler, runtime, execution host, worker/bridge,
request/input, and seed hashes. Any mismatch fails before execution; restored results reverify these
links before display.

### Repeatability uses an economic digest, not one universal receipt hash

Every attempt has a unique immutable `BacktestRunReceiptV1` containing its job identity, lifecycle
timestamps, machine/worker telemetry, publication facts, and the `EconomicResultDigestV1`. Two
valid repeated runs therefore need not have identical receipt or report bytes.

`EconomicResultDigestV1` hashes only deterministic economic outputs: ordered simulated-time equity
and cash samples; orders, fills, trades, positions, fees, slippage, and round trips; stable metrics;
and warnings/assumptions that affect economics. It excludes job id, wall-clock creation/publication
timestamps, engine milliseconds, progress messages, machine identity, and filesystem paths.
Repeatability means identical admitted module/data/scenario/run inputs produce the same economic digest.
Different job receipts remain separately auditable even when that digest matches.

The primary-source design rationale and phased runtime plan are recorded in the
[Vibe Quant runtime and backtest benchmark](research/vibe-quant-four-lane-runtime-benchmark.md).
