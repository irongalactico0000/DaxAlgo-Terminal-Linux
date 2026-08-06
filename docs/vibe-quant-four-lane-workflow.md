# Vibe Quant four-lane workflow

Vibe Quant can ask four independent generation agents to express one strategy brief in four
different authoring formats. This is an authoring and comparison workflow. It does not silently
compile, import, backtest, register, or run any generated artifact.

The exact format rules and their owners are defined in the
[Vibe Quant lane contracts v1](vibe-quant-lane-contracts.md). The machine-readable Declarative
Rules contract is [JSON Schema Draft 2020-12](schemas/vibe-quant-declarative-rules-v1.schema.json).

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

If an initial response is not one valid candidate envelope, that lane may make at most one separate
repair request. The repair prompt contains the original host envelope plus the exact deterministic
issue codes, paths, and messages. Therefore a generation turn makes four initial requests and zero
to four bounded repair requests; a first-pass-valid turn still makes exactly four. Repair never
silently changes a valid sibling lane and never bypasses deterministic validation.

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

The shared candidate envelope also normalizes model-authored scalar parameter defaults. A provider
may return `defaultValue` as a JSON string, number, or boolean; the host stores its invariant scalar
spelling as a string for comparison and canonical hashing. Objects and arrays remain invalid. This
does not coerce the lane artifact itself: each artifact contract still owns its native parameter
types. Every prompt now embeds the exact host-owned `packageBinding` object, so a model cannot satisfy
the contract with a `copy` placeholder.

## Response recovery boundary

The provider is still required to return one root JSON object. Claude CLI requests add its
root-object structured-output flag; other adapters retain the same host-side parser and validator.
For resilience, the host first tries strict JSON parsing, then accepts exactly one unambiguous JSON
object embedded in incidental prose or a Markdown fence. It does not guess when two objects are
present.

If that recovered object still fails the shared envelope or lane contract, the host may make one
validation-aware repair request for that lane. Cancellation propagates through the repair call,
provider failures are not retried, and an invalid repaired response ends as
`LANE_JSON_INVALID_AFTER_REPAIR`. Usage from both calls is reported together. This is bounded output
recovery, not an execution retry and not a semantic-correctness proof.

## From four drafts to one canonical artifact

The four cards are alternatives, not four executable pieces that can be concatenated. Combining
reviewed candidates is a separate AI synthesis operation:

```text
reviewed selectable source candidates
  + exact source contract ids and SHA-256 hashes
  + original brief hash and target TradeIR binding
  -> one new AI synthesis request
  -> one new strategy.tradeir.json with a new SHA-256
  -> installed TradeIR package/catalog validation
  -> immutable synthesis receipt
```

The synthesis receipt binds the ordered source lane ids, candidate ids, contract ids and versions,
source hashes, batch-prompt hash, synthesis-request hash, target package/catalog binding, synthesized
candidate hash, and provider/model identity. Editing a source, changing the target binding, or
changing the synthesized bytes makes that receipt stale.

This operation is reviewed **AI synthesis**, not a deterministic compiler. In particular, the
terminal does not claim that ordinary Python or CSP Python can be mechanically lowered to an
economically equivalent graph. The combined TradeIR artifact is a fifth candidate with its own
review boundary. It never overwrites or borrows the hash of a source candidate.

The intended Candidate-tab flow is:

1. Review the selectable Vibe, Rules, Graph, and CSP results and resolve material questions.
2. Choose **Synthesize valid drafts → TradeIR**. This makes one additional provider request.
3. Inspect the included source hashes, new target hash, package-validation result, and synthesis
   receipt hash.
4. Choose **Use combined TradeIR in editor** only after that review.

After loading that canonical artifact, the same narrow synthetic smoke test described below is
available. Synthesis itself still runs no test.

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

- four distinct initial provider requests were started for one brief, plus no more than one repair
  request for each invalid lane;
- each preserved response belongs to its expected lane and request;
- deterministic envelope and lane-specific shape checks passed or failed;
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
reviewed source hashes
  -> synthesis receipt + package-valid synthesized TradeIR hash
  -> authoritative point-in-time data binding
  -> target/operator capability admission
  -> installed worker-isolated TradeIR runtime admission
  -> historical backtest admission receipt bound to all of the above
  -> Backtest Studio request bound to the same synthesized hash and receipt
```

The future historical user path is therefore:

1. Generate and review the four source candidates.
2. Synthesize them into a new TradeIR candidate and review its receipt.
3. Bind the required instruments, schemas, snapshots, calendars, and event-time rules.
4. Let the terminal verify target/operator capabilities and the installed importer/runtime.
5. Review the resulting backtest admission receipt.
6. Click **Open historical backtest**; Backtest Studio must receive that same target hash and
   receipt without regenerating or rewriting the artifact.

Binding every stage prevents the terminal from validating one artifact and backtesting different
bytes. Until the historical data, worker protocol, and admission-receipt handoff exist, Vibe Quant
labels its available action as a synthetic smoke test and does not call it a historical backtest.
