# Goal

Remove the model-facing dependency on DaxAlgo's internal candidate envelope. Each generation lane
must return only its lane-native artifact plus review metadata; the trusted host must construct and
bind candidate identity, lane, request hash, filename, language, and package metadata.

# Plan

1. Define and prompt a minimal model response shared by all four lanes.
2. Parse lane-native source/JSON while accepting the previous envelope during migration.
3. Construct the canonical `StrategyGenerationCandidateV1` exclusively from trusted request and
   catalog values before deterministic lane validation.
4. Add adversarial tests proving model-supplied host metadata cannot mutate the canonical candidate.
5. Stop blind semantic repair: classify known missing facts and unsupported TradeIR catalog behavior
   as non-repairable, and make canonical lowering authority explicit.
6. Run focused generator tests, affected authoring tests, project build, and diff checks.

# Blast radius

- `src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyGenerationPromptV1.cs`
- `src/linux/Tools/DaxAlgo.Codegen/ParallelStrategyCandidateGeneratorV1.cs`
- Supporting generation contracts/catalog code only if the smallest implementation requires it.
- `src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs` for the truthful
  deterministic-lowerer boundary label.
- `tests/linux/TradingTerminal.Tests.Headless/Strategies/ParallelStrategyCandidateGeneratorV1Tests.cs`
- This task record.

No broker, live execution, market-data ingestion, backtest engine, or Windows repository behavior is
in scope.

# Build filter

`DaxAlgo.Codegen` has medium blast radius and is consumed by Infrastructure and StrategyTool. Start
with the focused headless generator tests, then run the codegen build and affected strategy tests.

# Tests

- New compact-response tests for Vibe Python, Declarative Rules, Typed Graph, and CSP.
- Legacy envelope compatibility tests.
- Host-owned identity/binding adversarial tests.
- Existing malformed-output, repair, isolation, selection, and hash tests.

# Findings

The current prompt asks each model to serialize `StrategyGenerationCandidateV1`, including
host-owned IDs, hashes, lane identity, filename, language, and package binding. Strict .NET
deserialization therefore rejects otherwise useful lane artifacts when a model omits or slightly
changes internal wrapper fields. The screenshot's four failures occur at this transport boundary,
before native artifact validation or code review.

# Diff summary

- The model-facing prompt now requests only review metadata plus a direct artifact payload.
- Vibe Python and CSP accept a direct JSON source string; Rules and TradeIR accept a direct JSON
  document object.
- The parser remains migration-compatible with full candidate envelopes and `source` / `document`
  artifact wrappers, but reads no echoed host-owned identity or artifact metadata.
- The host always constructs schema version, candidate id, lane, request hash, package binding,
  artifact kind, filename, language, and canonical candidate hash before validation.
- The host also replaces Graph strategy/catalog identity and Rules strategy identity before hashing;
  model-supplied values cannot override the installed Graph catalog or requested strategy id.
- A conservative automatic-repair disposition now stops after one provider call for known missing
  TradeIR data facts, operators absent from the installed catalog, and enumerated Declarative Rules
  clock/data-fact paths. Only proven response/shape failures receive the one bounded repair call.
- Vibe Python, Declarative Rules, and CSP now state that deterministic lowerers are required for
  canonical execution. Typed Graph remains identity conversion; optional AI synthesis is not a
  lowering proof. The authoring binding and four agent protocol ids were versioned accordingly.
- Focused tests now use slim responses for all four lanes and adversarially replace every host-owned
  field in legacy envelopes to prove the replacements cannot escape into the canonical candidate.

# Verification

- `dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj
  --no-restore --filter FullyQualifiedName~ParallelStrategyCandidateGeneratorV1Tests --nologo`:
  69 passed, 0 failed, 0 skipped.
- `dotnet build src/linux/Tools/DaxAlgo.Codegen/DaxAlgo.Codegen.csproj --no-restore --nologo`:
  succeeded with 0 warnings and 0 errors.
- Full headless suite: 787 passed, 0 failed, 6 platform-process tests skipped.
- Avalonia application tests: 94 passed, 0 failed, 0 skipped.
- Full `TradingTerminal.Mac.slnx` build: succeeded with 0 errors and 2 pre-existing nullable warnings
  in `DaxqIlLowerer.cs`.
- macOS source context regenerated: 56 projects, 1,099 files, 174,375 LOC.
- `git diff --check`: passed.

# Risks/deferred

- This change makes generation and inspection reliable; it does not create the still-missing native
  Python, Rules, CSP, or historical TradeIR runtime/importer paths.
- The no-repair unsupported-semantic classification is authoritative for the installed TradeIR
  catalog. Rules still needs the host-owned Draft-to-Resolved catalog/facts transition before it can
  classify every semantic gap authoritatively.
- Changing the generation prompt hash intentionally invalidates saved batches created under the old
  prompt contract and requires fresh generation.
