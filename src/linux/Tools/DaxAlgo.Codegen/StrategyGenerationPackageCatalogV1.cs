using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Host-owned generation contracts for the four authoring artifacts. Every lane has a deterministic
/// shape validator; only Typed Graph has an installed package validator. An authoring binding is not
/// evidence of an importer, runtime dependency, executable package, backtest, or successful test.
/// </summary>
internal static class StrategyGenerationPackageCatalogV1
{
    private const string AuthoringPackageId = "DaxAlgo.Codegen.GenerationAuthoring";
    private const string AuthoringPackageVersion = "1";

    private static readonly StrategyOperatorRegistryV1 TradeIrRegistry =
        StrategyOperatorRegistryV1.CreateDefault();

    private static readonly Type AuthoringValidatorType = typeof(StrategyGenerationCandidateValidatorV1);
    private static readonly Type TradeIrArtifactType = typeof(OperatorGraphModuleV1);
    private static readonly Type TradeIrValidatorType = typeof(TradeIrModuleValidatorV1);

    private const string CanonicalTradeIrContract = "trade-ir/module/v1";

    private static readonly StrategyGenerationContractAuthorityV1 VibePythonAuthority = new(
        AuthorityId: "DaxAlgo.VibeQuant",
        SpecificationReference: "docs/vibe-quant-lane-contracts.md#vibe-python-v1",
        SemanticRole: StrategyGenerationSemanticRoleV1.SourceReview,
        CanonicalTargetContract: CanonicalTradeIrContract,
        LoweringMode: StrategyGenerationLoweringModeV1.ReviewedAiSynthesis,
        ExternalReference: null,
        ExternalCompatibility: StrategyGenerationExternalCompatibilityV1.NotApplicable);

    private static readonly StrategyGenerationContractAuthorityV1 DeclarativeRulesAuthority = new(
        AuthorityId: "DaxAlgo.VibeQuant",
        SpecificationReference: "docs/schemas/vibe-quant-declarative-rules-v1.schema.json",
        SemanticRole: StrategyGenerationSemanticRoleV1.SourceReview,
        CanonicalTargetContract: CanonicalTradeIrContract,
        LoweringMode: StrategyGenerationLoweringModeV1.ReviewedAiSynthesis,
        ExternalReference: null,
        ExternalCompatibility: StrategyGenerationExternalCompatibilityV1.NotApplicable);

    private static readonly StrategyGenerationContractAuthorityV1 TradeIrAuthority = new(
        AuthorityId: "DaxAlgo.TradeIR",
        SpecificationReference: "docs/vibe-quant-lane-contracts.md#typed-graph-canonical-daxalgo-tradeir-v1",
        SemanticRole: StrategyGenerationSemanticRoleV1.CanonicalExecutableIr,
        CanonicalTargetContract: CanonicalTradeIrContract,
        LoweringMode: StrategyGenerationLoweringModeV1.Identity,
        ExternalReference: null,
        ExternalCompatibility: StrategyGenerationExternalCompatibilityV1.NotApplicable);

    private static readonly StrategyGenerationContractAuthorityV1 CspAuthoringAuthority = new(
        AuthorityId: "DaxAlgo.VibeQuant",
        SpecificationReference: "docs/vibe-quant-lane-contracts.md#csp-events-authoring-profile-v1",
        SemanticRole: StrategyGenerationSemanticRoleV1.SourceReview,
        CanonicalTargetContract: CanonicalTradeIrContract,
        LoweringMode: StrategyGenerationLoweringModeV1.ReviewedAiSynthesis,
        ExternalReference: "https://github.com/Point72/csp",
        ExternalCompatibility: StrategyGenerationExternalCompatibilityV1.Unverified);

    private static readonly StrategyGenerationPackageBindingV1 VibePythonBinding = CreateAuthoringBinding(
        StrategyGenerationLaneV1.VibePython,
        "DaxAlgo.VibeQuant.VibePython.Authoring",
        "vibe-quant/python-strategy/v1",
        VibePythonAuthority);

    private static readonly StrategyGenerationPackageBindingV1 DeclarativeSpecBinding = CreateAuthoringBinding(
        StrategyGenerationLaneV1.DeclarativeSpec,
        "DaxAlgo.VibeQuant.DeclarativeRules",
        "vibe-quant/declarative-rules/v1",
        DeclarativeRulesAuthority);

    private static readonly StrategyGenerationPackageBindingV1 TradeIrBinding = new(
        PackageId: TradeIrArtifactType.Assembly.GetName().Name!,
        PackageVersion: TradeIrArtifactType.Assembly.GetName().Version?.ToString() ?? "unversioned",
        PackageImplementationHashSha256: PackageImplementationHash(),
        ArtifactContract: TradeIrArtifactType.FullName!,
        ArtifactContractVersion: TradeIrModuleV1.CurrentSchemaVersion,
        ValidatorId: TradeIrValidatorType.FullName!,
        ImporterId: null,
        OperatorCatalog: TradeIrRegistry.Catalog,
        Authority: TradeIrAuthority);

    private static readonly StrategyGenerationPackageBindingV1 CspPythonBinding = CreateAuthoringBinding(
        StrategyGenerationLaneV1.CspPython,
        "DaxAlgo.VibeQuant.CspAuthoringProfile",
        "vibe-quant/csp-authoring-profile/v1",
        CspAuthoringAuthority);

    public static bool IsSupported(StrategyGenerationLaneV1 lane) =>
        lane is StrategyGenerationLaneV1.VibePython or
            StrategyGenerationLaneV1.DeclarativeSpec or
            StrategyGenerationLaneV1.TypedGraph or
            StrategyGenerationLaneV1.CspPython;

    public static bool PackageValidationAvailable(StrategyGenerationLaneV1 lane) =>
        lane == StrategyGenerationLaneV1.TypedGraph;

    public static StrategyGenerationPackageBindingV1 RequireBinding(StrategyGenerationLaneV1 lane) => lane switch
    {
        StrategyGenerationLaneV1.VibePython => VibePythonBinding,
        StrategyGenerationLaneV1.DeclarativeSpec => DeclarativeSpecBinding,
        StrategyGenerationLaneV1.TypedGraph => TradeIrBinding,
        StrategyGenerationLaneV1.CspPython => CspPythonBinding,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown strategy generation lane."),
    };

    public static string ArtifactFileName(StrategyGenerationLaneV1 lane) => lane switch
    {
        StrategyGenerationLaneV1.VibePython => "strategy.py",
        StrategyGenerationLaneV1.DeclarativeSpec => "strategy.spec.json",
        StrategyGenerationLaneV1.TypedGraph => "strategy.tradeir.json",
        StrategyGenerationLaneV1.CspPython => "strategy.csp.py",
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown strategy generation lane."),
    };

    public static string ArtifactLanguage(StrategyGenerationLaneV1 lane) => lane switch
    {
        StrategyGenerationLaneV1.VibePython or StrategyGenerationLaneV1.CspPython => "python",
        StrategyGenerationLaneV1.DeclarativeSpec or StrategyGenerationLaneV1.TypedGraph => "json",
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown strategy generation lane."),
    };

    /// <summary>Compatibility text for consumers that explain the absent runtime boundary.</summary>
    public static string UnsupportedReason(StrategyGenerationLaneV1 lane) => lane switch
    {
        StrategyGenerationLaneV1.VibePython =>
            "The Vibe Python source/review artifact has no deterministic TradeIR lowerer, registered importer, runtime package, or package validator.",
        StrategyGenerationLaneV1.DeclarativeSpec =>
            "The declarative authoring artifact has no registered lowerer, importer, runtime package, or package validator.",
        StrategyGenerationLaneV1.CspPython =>
            "The Vibe Quant CSP profile has unverified Point72 CSP compatibility and no registered CSP dependency, importer, runtime host, or package validator.",
        StrategyGenerationLaneV1.TypedGraph =>
            throw new InvalidOperationException("The canonical TradeIR graph package is registered."),
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown strategy generation lane."),
    };

    public static string PromptContract(StrategyGenerationLaneV1 lane)
    {
        var binding = ExecutableStrategyDefinitionCanonicalJson.Serialize(RequireBinding(lane));
        if (lane == StrategyGenerationLaneV1.VibePython)
        {
            return $$"""
                You are VibeAgent. Produce one editable ordinary-Python strategy authoring module.

                Host-owned generation-authoring binding (copy it exactly into packageBinding):
                {{binding}}

                This binding validates only the candidate envelope and deterministic source shape. It
                does not identify a Python runtime package, an importer, an execution ABI, or test
                evidence. Never describe the source as runnable, imported, package-valid, or tested.

                Set lane to vibePython. Set artifact.kind to vibePythonSource, fileName to strategy.py,
                language to python, document to null, and source to the exact plain Python module text.
                The source must contain no markdown fence and must declare this editable, ordinary
                Python authoring contract: the exact top-level marker
                `VIBE_QUANT_CONTRACT = "vibe-quant/python-strategy/v1"`, a top-level `PARAMETERS`
                sequence, a top-level `DATA_REQUIREMENTS` sequence, an `initialize_state`
                function with exact signature `initialize_state()`, and an
                `on_event(event, state, parameters)` function. Pass observations and
                parameters explicitly, keep all outputs inert (for example signal or target
                proposals), and do not fetch data, call a broker, start a runtime, or include
                terminal/package scaffolding. Use comments and unresolvedQuestions to preserve facts
                that the prompt does not establish.
                """;
        }

        if (lane == StrategyGenerationLaneV1.DeclarativeSpec)
        {
            return $$"""
                You are SpecAgent. Produce one editable declarative strategy authoring document.

                Host-owned generation-authoring binding (copy it exactly into packageBinding):
                {{binding}}

                This binding validates only the candidate envelope and the document's deterministic
                sections. No lowerer, importer, executable DSL package, runtime, or package validator
                is claimed. Never describe the document as runnable, lowered, package-valid, or tested.

                Set lane to declarativeSpec. Set artifact.kind to declarativeStrategyJson, fileName to
                strategy.spec.json, language to json, source to null, and document to one JSON object
                with every required top-level section below:
                {
                  "schemaVersion": "vibe-quant/declarative-rules/v1",
                  "strategy": {
                    "id": "<exact strategy id supplied by host>",
                    "version": "1.0.0",
                    "displayName": "...",
                    "summary": "..."
                  },
                  "clock": {
                    "basis": "eventTime", "timezone": "...", "sessionCalendar": "...",
                    "decisionTiming": "onEvent|intervalClose", "interval": null
                  },
                  "operatorCatalog": {
                    "catalogId": "...", "catalogVersion": "...",
                    "catalogHashSha256": "<64 lowercase hex>"
                  },
                  "parameters": [],
                  "dataRequirements": [],
                  "indicators": [],
                  "entryRules": [],
                  "exitRules": [],
                  "risk": {},
                  "outputs": []
                }

                The normative closed schema is docs/schemas/vibe-quant-declarative-rules-v1.schema.json;
                do not add unknown properties. Keep rules declarative and causal. Represent references by stable ids, keep adjustable
                literals in parameters, and put missing instruments, schemas, timing facts, or risk
                choices in unresolvedQuestions instead of inventing them. Do not add code, package
                glue, execution commands, or claims that this authoring document was validated by a
                runtime package.
                """;
        }

        if (lane == StrategyGenerationLaneV1.CspPython)
        {
            return $$"""
                You are CspAgent. Produce one editable CSP-style Python authoring module.

                Host-owned generation-authoring binding (copy it exactly into packageBinding):
                {{binding}}

                This is the Vibe Quant inert CSP authoring profile. Its Point72 CSP compatibility is
                unverified: no upstream revision or package is pinned. The binding performs
                syntax-marker and envelope validation only and does not prove that a CSP dependency,
                importer, CSP host, or runtime package is installed. Never describe the source as
                Point72-compatible, runnable, imported, package-valid, or tested.

                Set lane to cspPython. Set artifact.kind to cspPythonSource, fileName to strategy.csp.py,
                language to python, document to null, and source to the exact plain Python module text.
                The source must contain the exact top-level marker
                `VIBE_QUANT_CSP_CONTRACT = "vibe-quant/csp-authoring-profile/v1"` and all of these structural elements:
                - an exact `import csp` statement;
                - at least one `@csp.node` decorated function;
                - at least one `@csp.graph` decorated function;
                - one or more `ts[...]` time-series annotations, written either as `csp.ts[...]` or
                  imported with `from csp import ts` and used as `ts[...]`.

                The source must not contain `csp.run`; generation stops at an inert graph definition.
                Do not create adapters, feed subscriptions, engine startup, broker calls, execution
                glue, or package scaffolding. Preserve unknown event schemas and timing assumptions in
                unresolvedQuestions rather than fabricating them.
                """;
        }

        var manifest = ExecutableStrategyDefinitionCanonicalJson.Serialize(BuildOperatorManifest());
        var quoteSchemaHash = TradeIrSimulatedBacktestContractV1.SchemaHashSha256;
        return $$"""
            You are GraphAgent. Build the installed canonical DaxAlgo TradeIR operator-graph module.

            Host-owned package binding (copy it exactly into packageBinding):
            {{binding}}

            Host-owned operator authoring manifest (use only these operator ids, versions, ports, and
            parameter constraints; never invent an operator, port, parameter, or type):
            {{manifest}}

            Host-owned canonical QuoteL1 schema available to this package and its synthetic smoke target:
            {
              "schemaId": "canonical.quote-l1", "schemaVersion": 1,
              "schemaHashSha256": "{{quoteSchemaHash}}",
              "payloadFields": ["ask", "ask_size", "bid", "bid_size"]
            }
            For a QuoteL1 requirement with the event-time semantics below, copy that exact schema
            object. Never alter or invent its digest. A requiredSnapshotHashSha256 of null means the
            execution target must bind and hash its own materialized point-in-time snapshot.

            Strict nested JSON shapes (all shown properties are required unless explicitly nullable):
            - dataRequirements[]:
              {
                "requirementId": "quotes",
                "dataKind": "quoteL1",
                "instrumentSelector": { "references": [{
                  "instrumentKey": "equity/xnas/AAPL", "assetClass": "equity",
                  "symbol": "AAPL", "venue": "XNAS", "currency": "USD"
                }] },
                "eventSchema": {
                  "schemaId": "canonical.quote-l1", "schemaVersion": 1,
                  "schemaHashSha256": "{{quoteSchemaHash}}",
                  "payloadFields": ["ask", "ask_size", "bid", "bid_size"]
                },
                "temporalSemantics": {
                  "eventTimeBasis": "occurredAtUtc", "timestampPrecision": "microseconds",
                  "ordering": "eventTimeThenSourceSequence", "interval": null,
                  "requireAuthoritativeEventTime": true,
                  "requirePointInTimeAvailability": true
                },
                "normalizationPolicy": "rawUnadjusted", "missingDataPolicy": "reject",
                "revisionPolicy": "latestAvailableAtDecisionTime",
                "requiredSnapshotHashSha256": null
              }

            Emit closed wire vocabularies in their canonical camel-case form exactly as shown.
            In particular, `assetClass` must be one of `equity`, `future`, `forex`, `crypto`,
            `option`, or `index`. Use singular `future`, never `futures`, and never substitute a product
            description. If the prompt does not establish the instrument or asset class, preserve that
            gap in unresolvedQuestions instead of inventing a reference.
            - nodes[]:
              {
                "nodeId": "fast", "operatorId": "feature.ema", "operatorVersion": 1,
                "inputBindings": { "value": "price" },
                "parameters": { "period": {
                  "kind": "integer", "booleanValue": null, "integerValue": 10,
                  "numberValue": null, "textValue": null
                } }
              }
            - outputs[]:
              [
                { "outputId": "order-intent", "kind": "orderIntent", "nodeId": "execution" },
                { "outputId": "target", "kind": "target", "nodeId": "target" }
              ]

            When the brief explicitly asks for the installed QuoteL1 synthetic smoke path, emit
            exactly one `orderIntent` output whose node is `execution.market@1`, plus the portfolio
            `target` output consumed by that order path. Its order path must
            use only `market.quote.mid@1`, `feature.ema@1`, `logic.greater_than@1`,
            `portfolio.fixed_quantity@1`, optional `risk.trailing_fraction@1`, and
            `execution.market@1`; every emitted node must contribute to that order-intent path.
            Bind `market.quote.mid.requirement_id` to the QuoteL1 requirement id, set
            `execution.market.time_in_force` to the text literal `day`, use the exact fixed target
            quantities from the brief, and set `flattenOnEnd` to true. This recipe makes the graph
            eligible for admission; it does not claim admission or execution succeeded.

            The example instrument illustrates shape only; the canonical QuoteL1 schema identity is
            host-owned fact, not an example. Never copy, guess, or fabricate instrument, venue,
            currency, another schema, snapshot, or temporal facts. Declare the exact data contract
            required by the strategy and preserve missing material facts in unresolvedQuestions.
            Package validation does not mean data binding or target admission passed.

            Set lane to typedGraph. Set artifact.kind to tradeIrModuleJson, fileName to
            strategy.tradeir.json, language to json, source to null, and document to one exact
            OperatorGraphModuleV1 object:
            {
              "moduleKind": "operatorGraph",
              "schemaVersion": "trade-ir/module/v1",
              "moduleId": "a-lowercase-stable-id",
              "definition": {
                "schemaVersion": 1,
                "strategyId": "<exact strategy id supplied by host>",
                "strategyVersion": "1.0.0",
                "operatorCatalog": <copy the exact operatorCatalog object from packageBinding>,
                "clock": "eventTime",
                "dataRequirements": [],
                "nodes": [],
                "outputs": [],
                "flattenOnEnd": true
              }
            }

            Nodes contain only nodeId, operatorId, operatorVersion, inputBindings, and closed typed
            literal parameters. Do not author typedInputs, typedOutputs, port types, edges, groups,
            layout, state declarations, or requiredOperators: the trusted registry derives ports,
            types, state, capabilities, placement, and causal validity. Keep nodes, data requirements,
            and outputs in ordinal id order. Output only inert signal, target, quoteIntent, or
            orderIntent bindings. If the installed catalog cannot express the requested idea, preserve
            the gap in unresolvedQuestions instead of inventing catalog features.
            """;
    }

    public static IReadOnlyList<StrategyCandidateGenerationIssueV1> ValidatePackage(
        StrategyGenerationCandidateV1 candidate,
        string expectedStrategyId)
    {
        var issues = new List<StrategyCandidateGenerationIssueV1>();
        if (!IsSupported(candidate.Lane))
        {
            issues.Add(Error(
                "LANE_AUTHORING_BINDING_UNKNOWN",
                "packageBinding",
                $"Unknown strategy generation lane value '{candidate.Lane}'."));
            return issues;
        }

        var expectedBinding = RequireBinding(candidate.Lane);
        if (candidate.PackageBinding != expectedBinding)
            issues.Add(Error(
                candidate.Lane == StrategyGenerationLaneV1.TypedGraph
                    ? "LANE_PACKAGE_BINDING_CHANGED"
                    : "LANE_AUTHORING_BINDING_CHANGED",
                "packageBinding",
                candidate.Lane == StrategyGenerationLaneV1.TypedGraph
                    ? "The model changed the host-owned TradeIR package, validator, or operator-catalog binding."
                    : "The model changed the host-owned generation-authoring contract or structural-validator binding."));

        if (candidate.Lane != StrategyGenerationLaneV1.TypedGraph)
            return issues;

        if (candidate.Artifact is null)
            return issues;

        if (candidate.Artifact.Document is not { ValueKind: JsonValueKind.Object } document)
            return issues;

        OperatorGraphModuleDocumentV1 moduleDocument;
        try
        {
            moduleDocument = ExecutableStrategyDefinitionCanonicalJson.Deserialize<OperatorGraphModuleDocumentV1>(
                document.GetRawText());
        }
        catch (JsonException exception)
        {
            issues.Add(Error(
                "LANE_TRADEIR_JSON_INVALID",
                string.IsNullOrWhiteSpace(exception.Path) ? "artifact.document" : exception.Path,
                ConciseJsonError(exception)));
            return issues;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            issues.Add(Error("LANE_TRADEIR_JSON_INVALID", "artifact.document", exception.Message));
            return issues;
        }

        if (!string.Equals(moduleDocument.ModuleKind, "operatorGraph", StringComparison.Ordinal))
        {
            issues.Add(Error(
                "LANE_TRADEIR_MODULE_KIND_INVALID",
                "artifact.document.moduleKind",
                "GraphAgent must emit an OperatorGraphModuleV1."));
            return issues;
        }

        var graph = new OperatorGraphModuleV1(
            moduleDocument.SchemaVersion,
            moduleDocument.ModuleId,
            moduleDocument.Definition);

        if (!string.Equals(graph.Definition?.StrategyId, expectedStrategyId, StringComparison.Ordinal))
            issues.Add(Error(
                "LANE_TRADEIR_STRATEGY_ID_CHANGED",
                "artifact.document.definition.strategyId",
                "The TradeIR definition must preserve the exact host-owned strategy id."));

        foreach (var issue in TradeIrModuleValidatorV1.Validate(graph, TradeIrRegistry).Issues)
            issues.Add(Error(
                "LANE_TRADEIR_PACKAGE_INVALID",
                $"artifact.document.{issue.Path}",
                $"{issue.Code}: {issue.Message}"));

        return issues;
    }

    private static OperatorAuthoringManifestV1 BuildOperatorManifest()
    {
        var parameterRules = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["cross_section.rank"] = [],
            ["execution.market"] = ["time_in_force:text one of day|good_til_cancelled|immediate_or_cancel"],
            ["feature.ema"] = ["period:integer 2..1000000"],
            ["feature.rolling_max"] = ["window:integer 1..1000000"],
            ["logic.greater_than"] = [],
            ["market.bar.close"] = ["requirement_id:non-empty text referencing one Bar dataRequirement"],
            ["market.quote.mid"] = ["requirement_id:non-empty text referencing one QuoteL1 dataRequirement"],
            ["portfolio.fixed_quantity"] = ["when_false:number", "when_true:number"],
            ["portfolio.rank_long_short"] =
                ["gross_quantity:number > 0", "long_fraction:number in (0,0.5]", "short_fraction:number in (0,0.5]"],
            ["risk.trailing_fraction"] = ["fraction:number in (0,1)"],
            ["time.lag"] = ["periods:integer 1..1000000"],
        };

        var operators = TradeIrRegistry.Keys.Select(key =>
        {
            TradeIrRegistry.TryResolve(key.OperatorId, key.Version, out var descriptor);
            return new OperatorAuthoringEntryV1(
                key.OperatorId,
                key.Version,
                descriptor.RequiredInputPorts,
                descriptor.OptionalInputPorts,
                parameterRules[key.OperatorId]);
        }).ToArray();
        return new OperatorAuthoringManifestV1(TradeIrRegistry.Catalog, operators);
    }

    private static StrategyGenerationPackageBindingV1 CreateAuthoringBinding(
        StrategyGenerationLaneV1 lane,
        string artifactContract,
        string artifactContractVersion,
        StrategyGenerationContractAuthorityV1 authority)
    {
        var validatorId = $"{AuthoringValidatorType.FullName}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        return new StrategyGenerationPackageBindingV1(
            AuthoringPackageId,
            AuthoringPackageVersion,
            ExecutableStrategyDefinitionCanonicalJson.Hash(new AuthoringImplementationIdentityV1(
                AssemblyIdentity(AuthoringValidatorType.Assembly),
                lane,
                artifactContract,
                artifactContractVersion,
                ArtifactFileName(lane),
                ArtifactLanguage(lane),
                validatorId,
                authority)),
            artifactContract,
            artifactContractVersion,
            validatorId,
            ImporterId: null,
            OperatorCatalog: null,
            Authority: authority);
    }

    private static StrategyCandidateGenerationIssueV1 Error(string code, string path, string message) =>
        new(StrategyCandidateGenerationIssueSeverityV1.Error, code, path, message);

    private static string ConciseJsonError(JsonException exception)
    {
        var message = exception.Message;
        var locationStart = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (locationStart >= 0)
            message = message[..locationStart];

        return string.IsNullOrWhiteSpace(message)
            ? "TradeIR JSON does not match the installed schema."
            : message.Trim();
    }

    private static string PackageImplementationHash()
    {
        return ExecutableStrategyDefinitionCanonicalJson.Hash(new PackageImplementationIdentityV1(
            AssemblyIdentity(TradeIrArtifactType.Assembly),
            AssemblyIdentity(typeof(StrategyGenerationPackageCatalogV1).Assembly),
            TradeIrArtifactType.FullName!,
            TradeIrValidatorType.FullName!));
    }

    private static AssemblyImplementationIdentityV1 AssemblyIdentity(System.Reflection.Assembly assembly)
    {
        var location = assembly.Location;
        var contentHash = !string.IsNullOrWhiteSpace(location) && File.Exists(location)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(location)))
                .ToLowerInvariant()
            : ExecutableStrategyDefinitionCanonicalJson.Hash(new
            {
                Assembly = assembly.FullName ?? assembly.GetName().Name ?? string.Empty,
                ModuleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("D"),
            });
        return new AssemblyImplementationIdentityV1(
            assembly.FullName ?? assembly.GetName().Name ?? string.Empty,
            assembly.ManifestModule.ModuleVersionId.ToString("D"),
            contentHash);
    }

    private sealed record OperatorAuthoringManifestV1(
        StrategyOperatorCatalogReferenceV1 Catalog,
        IReadOnlyList<OperatorAuthoringEntryV1> Operators);

    private sealed record OperatorAuthoringEntryV1(
        string OperatorId,
        int OperatorVersion,
        IReadOnlyList<string> RequiredInputPorts,
        IReadOnlyList<string> OptionalInputPorts,
        IReadOnlyList<string> Parameters);

    private sealed record PackageImplementationIdentityV1(
        AssemblyImplementationIdentityV1 ArtifactAssembly,
        AssemblyImplementationIdentityV1 GenerationAssembly,
        string ArtifactContract,
        string ValidatorContract);

    private sealed record AuthoringImplementationIdentityV1(
        AssemblyImplementationIdentityV1 GenerationAssembly,
        StrategyGenerationLaneV1 Lane,
        string ArtifactContract,
        string ArtifactContractVersion,
        string FileName,
        string Language,
        string ValidatorId,
        StrategyGenerationContractAuthorityV1 Authority);

    private sealed record AssemblyImplementationIdentityV1(
        string AssemblyIdentity,
        string ModuleVersionId,
        string ContentHashSha256);

    private sealed record OperatorGraphModuleDocumentV1(
        string ModuleKind,
        string SchemaVersion,
        string ModuleId,
        StrategyIntermediateRepresentationV1 Definition);
}
