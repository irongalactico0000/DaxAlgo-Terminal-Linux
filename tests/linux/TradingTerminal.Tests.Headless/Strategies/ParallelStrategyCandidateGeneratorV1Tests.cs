using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class ParallelStrategyCandidateGeneratorV1Tests
{
    private static readonly ParallelStrategyGenerationRequestV1 Request =
        new("ema-cross", "Trade a causal fast/slow EMA cross with fixed sizing.");

    [Fact]
    public async Task Starts_all_four_lanes_before_any_lane_completes()
    {
        var agents = StrategyGenerationLaneCatalogV1.Ordered.Select(lane => new GatedLaneAgent(lane)).ToArray();
        var sut = new ParallelStrategyCandidateGeneratorV1(agents);
        var progress = new SynchronousProgressRecorder();

        var generation = sut.GenerateAsync(new UnusedProvider(), Request, progress: progress);
        await Task.WhenAll(agents.Select(agent => agent.Started)).WaitAsync(TimeSpan.FromSeconds(5));

        generation.IsCompleted.Should().BeFalse("all four generation calls should be in flight together");
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
        {
            progress.StatesFor(lane).Should().Equal(
                StrategyGenerationLaneProgressStateV1.Queued,
                StrategyGenerationLaneProgressStateV1.Running);
        }
        foreach (var agent in agents) agent.Release();
        var result = await generation;

        result.Lanes.Should().HaveCount(4);
        result.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph)
            .Readiness.Should().Be(StrategyGenerationReadinessV1.PackageValid);
        result.Lanes.Where(lane => lane.Lane != StrategyGenerationLaneV1.TypedGraph)
            .Should().OnlyContain(lane => lane.Readiness == StrategyGenerationReadinessV1.Generated);
        result.Lanes.Should().OnlyContain(lane => lane.Selectable);
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
        {
            progress.StatesFor(lane).Should().Equal(
                StrategyGenerationLaneProgressStateV1.Queued,
                StrategyGenerationLaneProgressStateV1.Running,
                StrategyGenerationLaneProgressStateV1.Completed);
        }
    }

    [Fact]
    public async Task Returns_Vibe_Spec_Graph_Csp_order_regardless_of_completion_order()
    {
        var agents = StrategyGenerationLaneCatalogV1.Ordered.Select(lane => new GatedLaneAgent(lane)).ToArray();
        var sut = new ParallelStrategyCandidateGeneratorV1(agents.Reverse());
        var generation = sut.GenerateAsync(new UnusedProvider(), Request);
        await Task.WhenAll(agents.Select(agent => agent.Started)).WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var agent in agents.Reverse())
        {
            agent.Release();
            await agent.Completed.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var result = await generation;
        result.Lanes.Select(lane => lane.Lane).Should().Equal(StrategyGenerationLaneCatalogV1.Ordered);
        StrategyGenerationBatchValidationV1.Validate(result).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_reports_canceled_for_every_running_lane_before_rethrowing()
    {
        var agents = StrategyGenerationLaneCatalogV1.Ordered.Select(lane => new GatedLaneAgent(lane)).ToArray();
        var sut = new ParallelStrategyCandidateGeneratorV1(agents);
        var progress = new SynchronousProgressRecorder();
        using var cts = new CancellationTokenSource();

        var generation = sut.GenerateAsync(new UnusedProvider(), Request, cts.Token, progress);
        await Task.WhenAll(agents.Select(agent => agent.Started)).WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var act = async () => await generation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
        {
            progress.StatesFor(lane).Should().Equal(
                StrategyGenerationLaneProgressStateV1.Queued,
                StrategyGenerationLaneProgressStateV1.Running,
                StrategyGenerationLaneProgressStateV1.Canceled);
        }
    }

    [Fact]
    public async Task Pre_canceled_generation_makes_no_lane_or_provider_calls_and_reports_no_progress()
    {
        var provider = new FourLaneArtifactProvider();
        var progress = new SynchronousProgressRecorder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await ProductionGenerator().GenerateAsync(provider, Request, cts.Token, progress);

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.CallCount.Should().Be(0);
        provider.Requests.Should().BeEmpty();
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
            progress.StatesFor(lane).Should().BeEmpty();
    }

    [Fact]
    public async Task Non_cooperative_lanes_cannot_turn_a_canceled_generation_into_success()
    {
        var agents = StrategyGenerationLaneCatalogV1.Ordered
            .Select(lane => new GatedLaneAgent(lane, honorCancellation: false))
            .ToArray();
        var sut = new ParallelStrategyCandidateGeneratorV1(agents);
        var progress = new SynchronousProgressRecorder();
        using var cts = new CancellationTokenSource();

        var generation = sut.GenerateAsync(new UnusedProvider(), Request, cts.Token, progress);
        await Task.WhenAll(agents.Select(agent => agent.Started)).WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        generation.IsCompleted.Should().BeFalse("the deliberately non-cooperative lanes are still gated");
        foreach (var agent in agents) agent.Release();

        var act = async () => await generation;
        await act.Should().ThrowAsync<OperationCanceledException>();
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
        {
            progress.StatesFor(lane).Should().Equal(
                StrategyGenerationLaneProgressStateV1.Queued,
                StrategyGenerationLaneProgressStateV1.Running,
                StrategyGenerationLaneProgressStateV1.Canceled);
        }
    }

    [Fact]
    public async Task All_four_lanes_call_the_model_once_and_prompts_are_format_specific()
    {
        var provider = new FourLaneArtifactProvider();
        var sut = ProductionGenerator();
        var request = Request with { StrategyId = $"  {Request.StrategyId}  " };

        var result = await sut.GenerateAsync(provider, request);

        result.StrategyId.Should().Be(Request.StrategyId);
        result.PromptHashSha256.Should().Be(
            StrategyGenerationCandidateCanonicalJsonV1.PromptHash(Request.StrategyId, Request.UserPrompt));
        provider.CallCount.Should().Be(4);
        provider.Requests.Should().HaveCount(4);
        provider.Requests.Should().OnlyContain(modelRequest =>
            modelRequest.OutputContract == StrategyCodegenOutputContract.RawJsonObject);

        var vibePrompt = PromptFor(provider, StrategyGenerationLaneV1.VibePython);
        vibePrompt.Should().Contain("VibeAgent");
        vibePrompt.Should().Contain("editable ordinary-Python");
        vibePrompt.Should().Contain("top-level `PARAMETERS` mapping");
        vibePrompt.Should().Contain("`initialize_state`");
        vibePrompt.Should().Contain("`on_event(event, state, parameters)`");
        vibePrompt.Should().Contain("outputs inert");
        vibePrompt.Should().Contain("does not identify a Python runtime package");

        var specPrompt = PromptFor(provider, StrategyGenerationLaneV1.DeclarativeSpec);
        specPrompt.Should().Contain("SpecAgent");
        specPrompt.Should().Contain("declarative-strategy/v1");
        specPrompt.Should().Contain("dataRequirements");
        specPrompt.Should().Contain("entryRules");
        specPrompt.Should().Contain("exitRules");
        specPrompt.Should().Contain("risk");
        specPrompt.Should().Contain("No lowerer, importer, executable DSL package");

        var graphPrompt = PromptFor(provider, StrategyGenerationLaneV1.TypedGraph);
        graphPrompt.Should().Contain("GraphAgent");
        graphPrompt.Should().Contain("OperatorGraphModuleV1");
        graphPrompt.Should().Contain("daxalgo.strategy-operators");
        graphPrompt.Should().Contain("TradeIrModuleValidatorV1");
        graphPrompt.Should().Contain("packageImplementationHashSha256");
        graphPrompt.Should().Contain("requiredSnapshotHashSha256");
        graphPrompt.Should().Contain("integerValue");
        graphPrompt.Should().Contain("`assetClass` must be one of `equity`, `future`, `forex`, `crypto`");
        graphPrompt.Should().Contain("`option`, or `index`");
        graphPrompt.Should().Contain("Use singular `future`, never `futures`");
        graphPrompt.Should().Contain("Do not author typedInputs, typedOutputs");

        var cspPrompt = PromptFor(provider, StrategyGenerationLaneV1.CspPython);
        cspPrompt.Should().Contain("CspAgent");
        cspPrompt.Should().Contain("@csp.node");
        cspPrompt.Should().Contain("@csp.graph");
        cspPrompt.Should().Contain("ts[");
        cspPrompt.Should().Contain("csp.run");

        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered)
        {
            var prompt = PromptFor(provider, lane);
            prompt.Should().Contain("requestHashSha256");
            prompt.Should().Contain($"{Request.StrategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}");
            prompt.Should().Contain("never claim a backtest, metric, or package check passed");
        }

        result.Lanes.Should().ContainSingle(lane => lane.PackageValid && lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        result.Lanes.Where(lane => lane.Lane != StrategyGenerationLaneV1.TypedGraph).Should().OnlyContain(lane =>
            lane.Readiness == StrategyGenerationReadinessV1.Generated &&
            lane.Selectable &&
            !lane.PackageValidationAvailable);
        result.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph)
            .PackageValidationAvailable.Should().BeTrue();
        StrategyGenerationBatchValidationV1.Validate(result).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_candidate_is_bound_to_its_exact_request_artifact_and_host_contract()
    {
        var provider = new FourLaneArtifactProvider();
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);

        foreach (var result in batch.Lanes)
        {
            var candidate = result.Candidate!;
            candidate.CandidateId.Should().Be(
                $"{Request.StrategyId}/{StrategyGenerationLaneCatalogV1.WireName(result.Lane)}");
            candidate.RequestHashSha256.Should().Be(
                StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                    Request.StrategyId,
                    Request.UserPrompt,
                    result.Lane));
            candidate.PackageBinding.Should().Be(
                StrategyGenerationPackageCatalogV1.RequireBinding(result.Lane));
            candidate.PackageBinding.PackageImplementationHashSha256.Should().MatchRegex("^[0-9a-f]{64}$");
            candidate.Artifact.Kind.Should().Be(StrategyGenerationLaneCatalogV1.ArtifactKind(result.Lane));
            result.CandidateHashSha256.Should().Be(
                StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate));
            result.AgentRun.AgentId.Should().Be(ParallelStrategyGenerationPromptV1.AgentId(result.Lane));
            result.Selectable.Should().BeTrue(Describe(result.Issues));
        }

        batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.VibePython)
            .Candidate!.Artifact.FileName.Should().Be("strategy.py");
        batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.DeclarativeSpec)
            .Candidate!.Artifact.FileName.Should().Be("strategy.spec.json");
        batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph)
            .Candidate!.Artifact.FileName.Should().Be("strategy.tradeir.json");
        batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.CspPython)
            .Candidate!.Artifact.FileName.Should().Be("strategy.csp.py");
    }

    [Fact]
    public async Task One_provider_failure_is_isolated_while_the_other_three_candidates_remain_selectable()
    {
        var provider = new FourLaneArtifactProvider(failedLane: StrategyGenerationLaneV1.DeclarativeSpec);
        var progress = new SynchronousProgressRecorder();
        var result = await ProductionGenerator().GenerateAsync(provider, Request, progress: progress);

        result.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.DeclarativeSpec)
            .Readiness.Should().Be(StrategyGenerationReadinessV1.Failed);
        result.Lanes.Where(lane => lane.Lane != StrategyGenerationLaneV1.DeclarativeSpec)
            .Should().OnlyContain(lane => lane.Selectable);
        provider.CallCount.Should().Be(4);
        result.HasPackageValidCandidate.Should().BeTrue();
        StrategyGenerationBatchValidationV1.Validate(result).Should().BeEmpty();
        progress.StatesFor(StrategyGenerationLaneV1.DeclarativeSpec).Should().Equal(
            StrategyGenerationLaneProgressStateV1.Queued,
            StrategyGenerationLaneProgressStateV1.Running,
            StrategyGenerationLaneProgressStateV1.Failed);
        foreach (var lane in StrategyGenerationLaneCatalogV1.Ordered.Where(lane =>
                     lane != StrategyGenerationLaneV1.DeclarativeSpec))
        {
            progress.StatesFor(lane).Should().Equal(
                StrategyGenerationLaneProgressStateV1.Queued,
                StrategyGenerationLaneProgressStateV1.Running,
                StrategyGenerationLaneProgressStateV1.Completed);
        }
    }

    [Fact]
    public async Task Canonical_graph_module_passes_the_real_package_validator()
    {
        var provider = new FourLaneArtifactProvider();
        var result = await ProductionGenerator().GenerateAsync(provider, Request);
        var graph = result.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);

        graph.Readiness.Should().Be(StrategyGenerationReadinessV1.PackageValid, Describe(graph.Issues));
        graph.PackageValid.Should().BeTrue();
        graph.Candidate!.Artifact.Kind.Should().Be(StrategyGenerationArtifactKindV1.TradeIrModuleJson);
        graph.Candidate.Artifact.FileName.Should().Be("strategy.tradeir.json");
        graph.Candidate.PackageBinding.Should().Be(StrategyGenerationPackageCatalogV1.RequireBinding(
            StrategyGenerationLaneV1.TypedGraph));
        graph.Candidate.PackageBinding.PackageId.Should().Be("TradingTerminal.Core");
        graph.Candidate.PackageBinding.ArtifactContractVersion.Should().Be(TradeIrModuleV1.CurrentSchemaVersion);
        graph.Candidate.PackageBinding.PackageImplementationHashSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        graph.Candidate.RequestHashSha256.Should().Be(
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                Request.StrategyId,
                Request.UserPrompt,
                StrategyGenerationLaneV1.TypedGraph));
        graph.Candidate.Artifact.Document!.Value.GetRawText().Should().NotContain("typedInputs");
        graph.Candidate.Artifact.Document!.Value.GetRawText().Should().NotContain("typedOutputs");
    }

    [Fact]
    public async Task Invalid_graph_asset_class_reports_the_exact_TradeIR_JSON_path()
    {
        var provider = new FourLaneArtifactProvider((lane, candidate) =>
        {
            if (lane != StrategyGenerationLaneV1.TypedGraph)
                return candidate;

            var graphJson = candidate.Artifact.Document!.Value.GetRawText();
            graphJson.Should().Contain("\"assetClass\":\"equity\"");
            return candidate with
            {
                Artifact = candidate.Artifact with
                {
                    Document = Json(graphJson.Replace(
                        "\"assetClass\":\"equity\"",
                        "\"assetClass\":\"futures\"",
                        StringComparison.Ordinal)),
                },
            };
        });

        var result = await ProductionGenerator().GenerateAsync(provider, Request);
        var graph = result.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);

        graph.Readiness.Should().Be(StrategyGenerationReadinessV1.Invalid);
        graph.Selectable.Should().BeFalse();
        graph.Issues.Should().ContainSingle(issue =>
            issue.Code == "LANE_TRADEIR_JSON_INVALID" &&
            issue.Path == "$.definition.dataRequirements[0].instrumentSelector.references[0].assetClass" &&
            !issue.Message.Contains(" Path: ", StringComparison.Ordinal) &&
            !issue.Message.Contains("LineNumber", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StrategyGenerationLaneV1.VibePython)]
    [InlineData(StrategyGenerationLaneV1.DeclarativeSpec)]
    [InlineData(StrategyGenerationLaneV1.TypedGraph)]
    [InlineData(StrategyGenerationLaneV1.CspPython)]
    public async Task Structurally_or_package_invalid_artifacts_are_preserved_as_invalid_and_not_selectable(
        StrategyGenerationLaneV1 lane)
    {
        var provider = new FourLaneArtifactProvider((candidateLane, candidate) =>
            candidateLane == lane ? Malform(candidate) : candidate);
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);
        var laneResult = batch.Lanes.Single(result => result.Lane == lane);

        laneResult.Readiness.Should().Be(StrategyGenerationReadinessV1.Invalid);
        laneResult.Generated.Should().BeTrue();
        laneResult.Selectable.Should().BeFalse();
        laneResult.PackageValid.Should().BeFalse();
        laneResult.CandidateHashSha256.Should().NotBeNull();
        laneResult.Issues.Should().Contain(issue =>
            issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error);
        StrategyGenerationBatchValidationV1.Validate(batch).Should().BeEmpty();

        var selected = StrategyGenerationBatchValidationV1.Select(batch, laneResult.CandidateHashSha256);
        selected.Success.Should().BeFalse();
        selected.Issues.Should().Contain(issue => issue.Code == "BATCH_SELECTION_NOT_FOUND");
    }

    [Fact]
    public void Legacy_AI_authored_ports_and_edges_are_rejected_by_strict_TradeIR_deserialization()
    {
        var candidateId = $"{Request.StrategyId}/typed-graph";
        var candidate = GraphCandidate(candidateId);
        var legacy = candidate with
        {
            Artifact = candidate.Artifact with
            {
                Document = Json("""
                    {
                      "moduleKind": "operatorGraph",
                      "schemaVersion": "trade-ir/module/v1",
                      "moduleId": "legacy-graph",
                      "definition": {
                        "operatorCatalogRef": "installed",
                        "nodes": [{"id":"price","typedInputs":[],"typedOutputs":[]}],
                        "edges": []
                      }
                    }
                    """),
            },
        };

        var issues = StrategyGenerationCandidateValidatorV1.Validate(
            legacy,
            StrategyGenerationLaneV1.TypedGraph,
            candidateId,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                Request.StrategyId,
                Request.UserPrompt,
                StrategyGenerationLaneV1.TypedGraph));

        issues.Should().Contain(issue => issue.Code == "LANE_TRADEIR_JSON_INVALID");
    }

    [Fact]
    public void Host_owned_package_binding_cannot_be_changed_by_the_model()
    {
        var candidateId = $"{Request.StrategyId}/typed-graph";
        var candidate = GraphCandidate(candidateId);
        var tampered = candidate with
        {
            PackageBinding = candidate.PackageBinding with
            {
                PackageImplementationHashSha256 = new string('0', 64),
            },
        };

        var issues = StrategyGenerationCandidateValidatorV1.Validate(
            tampered,
            StrategyGenerationLaneV1.TypedGraph,
            candidateId,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                Request.StrategyId,
                Request.UserPrompt,
                StrategyGenerationLaneV1.TypedGraph));

        issues.Should().Contain(issue => issue.Code == "LANE_PACKAGE_BINDING_CHANGED");
    }

    [Fact]
    public async Task Malformed_nullable_batch_state_returns_issues_instead_of_throwing()
    {
        var original = await ProductionGenerator().GenerateAsync(new FourLaneArtifactProvider(), Request);
        var lanes = original.Lanes.ToArray();
        var index = Array.FindIndex(lanes, lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        var malformedCandidate = lanes[index].Candidate! with { Artifact = null! };
        lanes[index] = lanes[index] with
        {
            Candidate = malformedCandidate,
            CandidateHashSha256 = StrategyGenerationCandidateCanonicalJsonV1.Hash(malformedCandidate),
        };
        var malformed = original with { StrategyId = null!, Lanes = lanes };

        var action = () => StrategyGenerationBatchValidationV1.Validate(malformed);

        action.Should().NotThrow();
        action().Should().Contain(issue => issue.Code == "BATCH_IDENTITY_INVALID");
        action().Should().Contain(issue => issue.Code == "BATCH_LANE_VALIDATION_ISSUES_STALE");
    }

    [Fact]
    public async Task Null_lane_issue_returns_a_batch_error_instead_of_throwing()
    {
        var original = await ProductionGenerator().GenerateAsync(new FourLaneArtifactProvider(), Request);
        var lanes = original.Lanes.ToArray();
        var index = Array.FindIndex(lanes, lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        lanes[index] = lanes[index] with
        {
            Issues = new StrategyCandidateGenerationIssueV1[] { null! },
        };
        var malformed = original with { Lanes = lanes };

        var action = () => StrategyGenerationBatchValidationV1.Validate(malformed);

        action.Should().NotThrow();
        action().Should().Contain(issue => issue.Code == "BATCH_LANE_ISSUE_NULL");
    }

    [Fact]
    public async Task Selecting_any_of_the_four_candidates_uses_the_exact_hash_without_regeneration()
    {
        var provider = new FourLaneArtifactProvider();
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);
        var callsBeforeSelection = provider.CallCount;

        foreach (var lane in batch.Lanes)
        {
            var selected = StrategyGenerationBatchValidationV1.Select(batch, lane.CandidateHashSha256);
            selected.Success.Should().BeTrue(Describe(selected.Issues));
            selected.Candidate.Should().BeSameAs(lane.Candidate);
            selected.CandidateHashSha256.Should().Be(lane.CandidateHashSha256);
        }
        provider.CallCount.Should().Be(callsBeforeSelection);
    }

    [Fact]
    public async Task Locally_edited_graph_can_be_revalidated_without_a_model_call()
    {
        var provider = new FourLaneArtifactProvider();
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);
        var prior = batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        var callsBefore = provider.CallCount;
        var graph = ReadGraph(prior.Candidate!);
        var nodes = graph.Definition.Nodes.Select(node => node.NodeId == "fast"
            ? node with { Parameters = Parameters(("period", StrategyLiteralV1.FromInteger(5))) }
            : node).ToArray();
        var changed = ReplaceGraph(prior.Candidate!, graph with
        {
            Definition = graph.Definition with { Nodes = nodes },
        });

        var result = StrategyGenerationBatchValidationV1.RevalidateArtifact(
            batch,
            prior.CandidateHashSha256,
            changed.Artifact);

        result.Applied.Should().BeTrue(Describe(result.Issues));
        result.LaneResult!.Readiness.Should().Be(StrategyGenerationReadinessV1.PackageValid);
        result.LaneResult.CandidateHashSha256.Should().NotBe(prior.CandidateHashSha256);
        result.LaneResult.AgentRun.AgentId.Should().Be("strategy.local_revalidation@1");
        provider.CallCount.Should().Be(callsBefore);
        StrategyGenerationBatchValidationV1.Select(
            result.Batch,
            result.LaneResult.CandidateHashSha256).Success.Should().BeTrue();
    }

    [Theory]
    [InlineData(StrategyGenerationLaneV1.VibePython)]
    [InlineData(StrategyGenerationLaneV1.DeclarativeSpec)]
    [InlineData(StrategyGenerationLaneV1.CspPython)]
    public async Task Locally_edited_generation_only_artifact_remains_generated_and_selectable_without_a_model_call(
        StrategyGenerationLaneV1 lane)
    {
        var provider = new FourLaneArtifactProvider();
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);
        var prior = batch.Lanes.Single(result => result.Lane == lane);
        var callsBefore = provider.CallCount;

        var result = StrategyGenerationBatchValidationV1.RevalidateArtifact(
            batch,
            prior.CandidateHashSha256,
            ValidLocalEdit(prior.Candidate!));

        result.Applied.Should().BeTrue(Describe(result.Issues));
        result.LaneResult!.Readiness.Should().Be(StrategyGenerationReadinessV1.Generated);
        result.LaneResult.Selectable.Should().BeTrue(Describe(result.LaneResult.Issues));
        result.LaneResult.PackageValidationAvailable.Should().BeFalse();
        result.LaneResult.CandidateHashSha256.Should().NotBe(prior.CandidateHashSha256);
        result.LaneResult.AgentRun.AgentId.Should().Be("strategy.local_revalidation@1");
        provider.CallCount.Should().Be(callsBefore);
        StrategyGenerationBatchValidationV1.Select(
            result.Batch,
            result.LaneResult.CandidateHashSha256).Success.Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_local_edit_is_rehashed_and_preserved_but_not_selectable()
    {
        var provider = new FourLaneArtifactProvider();
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);
        var prior = batch.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        var callsBefore = provider.CallCount;
        var graph = ReadGraph(prior.Candidate!);
        var nodes = graph.Definition.Nodes.Select(node => node.NodeId == "fast"
            ? node with { Parameters = Parameters(("period", StrategyLiteralV1.FromInteger(1))) }
            : node).ToArray();
        var changed = ReplaceGraph(prior.Candidate!, graph with
        {
            Definition = graph.Definition with { Nodes = nodes },
        });

        var result = StrategyGenerationBatchValidationV1.RevalidateArtifact(
            batch,
            prior.CandidateHashSha256,
            changed.Artifact);

        result.Applied.Should().BeTrue(Describe(result.Issues));
        result.LaneResult!.Readiness.Should().Be(StrategyGenerationReadinessV1.Invalid);
        result.LaneResult.Selectable.Should().BeFalse();
        result.LaneResult.PackageValid.Should().BeFalse();
        result.LaneResult.CandidateHashSha256.Should().NotBe(prior.CandidateHashSha256);
        result.LaneResult.Issues.Should().Contain(issue =>
            issue.Code == "LANE_TRADEIR_PACKAGE_INVALID");
        provider.CallCount.Should().Be(callsBefore);
        StrategyGenerationBatchValidationV1.Validate(result.Batch).Should().BeEmpty();
        StrategyGenerationBatchValidationV1.Select(
            result.Batch,
            result.LaneResult.CandidateHashSha256).Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(StrategyGenerationLaneV1.VibePython)]
    [InlineData(StrategyGenerationLaneV1.DeclarativeSpec)]
    [InlineData(StrategyGenerationLaneV1.CspPython)]
    public async Task Invalid_generation_only_local_edit_uses_the_same_invalid_and_unselectable_state(
        StrategyGenerationLaneV1 lane)
    {
        var provider = new FourLaneArtifactProvider();
        var batch = await ProductionGenerator().GenerateAsync(provider, Request);
        var prior = batch.Lanes.Single(result => result.Lane == lane);
        var callsBefore = provider.CallCount;
        var malformed = Malform(prior.Candidate!);

        var result = StrategyGenerationBatchValidationV1.RevalidateArtifact(
            batch,
            prior.CandidateHashSha256,
            malformed.Artifact);

        result.Applied.Should().BeTrue(Describe(result.Issues));
        result.LaneResult!.Readiness.Should().Be(StrategyGenerationReadinessV1.Invalid);
        result.LaneResult.Generated.Should().BeTrue();
        result.LaneResult.Selectable.Should().BeFalse();
        result.LaneResult.CandidateHashSha256.Should().NotBe(prior.CandidateHashSha256);
        result.LaneResult.Issues.Should().Contain(issue =>
            issue.Severity == StrategyCandidateGenerationIssueSeverityV1.Error);
        provider.CallCount.Should().Be(callsBefore);
        StrategyGenerationBatchValidationV1.Validate(result.Batch).Should().BeEmpty();
        StrategyGenerationBatchValidationV1.Select(
            result.Batch,
            result.LaneResult.CandidateHashSha256).Success.Should().BeFalse();
    }

    [Fact]
    public async Task Batch_round_trip_preserves_availability_readiness_hash_and_selection()
    {
        var original = await ProductionGenerator().GenerateAsync(new FourLaneArtifactProvider(), Request);
        var chosenHash = original.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.VibePython)
            .CandidateHashSha256;

        var json = StrategyGenerationCandidateCanonicalJsonV1.SerializeBatch(original);
        var restored = StrategyGenerationCandidateCanonicalJsonV1.DeserializeBatch(json);
        var selected = StrategyGenerationBatchValidationV1.Select(restored, chosenHash);

        StrategyGenerationBatchValidationV1.Validate(restored).Should().BeEmpty();
        restored.Lanes.Select(lane => lane.Lane).Should().Equal(StrategyGenerationLaneCatalogV1.Ordered);
        restored.Lanes.Select(lane => lane.Readiness).Should().Equal(original.Lanes.Select(lane => lane.Readiness));
        restored.Lanes.Select(lane => lane.PackageValidationAvailable)
            .Should().Equal(original.Lanes.Select(lane => lane.PackageValidationAvailable));
        selected.Success.Should().BeTrue(Describe(selected.Issues));
        selected.Candidate!.Lane.Should().Be(StrategyGenerationLaneV1.VibePython);
    }

    [Fact]
    public async Task Stale_persisted_graph_hash_is_rejected()
    {
        var original = await ProductionGenerator().GenerateAsync(new FourLaneArtifactProvider(), Request);
        var lanes = original.Lanes.ToArray();
        var index = Array.FindIndex(lanes, lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        lanes[index] = lanes[index] with { CandidateHashSha256 = new string('0', 64) };

        var issues = StrategyGenerationBatchValidationV1.Validate(original with { Lanes = lanes });

        issues.Should().Contain(issue => issue.Code == "BATCH_CANDIDATE_HASH_INVALID");
    }

    [Fact]
    public async Task Candidate_from_another_prompt_cannot_be_spliced_into_the_batch()
    {
        var original = await ProductionGenerator().GenerateAsync(new FourLaneArtifactProvider(), Request);
        var changedPrompt = Request with { UserPrompt = "Use a causal rolling breakout instead." };
        var changed = await ProductionGenerator().GenerateAsync(
            new FourLaneArtifactProvider(changedPrompt),
            changedPrompt);
        var lanes = changed.Lanes.ToArray();
        var index = Array.FindIndex(lanes, lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        lanes[index] = original.Lanes.Single(lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        var spliced = changed with { Lanes = lanes };

        var issues = StrategyGenerationBatchValidationV1.Validate(spliced);

        issues.Should().Contain(issue => issue.Code == "BATCH_LANE_VALIDATION_ISSUES_STALE" &&
            issue.Message.Contains("LANE_REQUEST_HASH_CHANGED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generation_batch_cannot_claim_package_tests_passed_without_test_evidence()
    {
        var original = await ProductionGenerator().GenerateAsync(new FourLaneArtifactProvider(), Request);
        var lanes = original.Lanes.ToArray();
        var index = Array.FindIndex(lanes, lane => lane.Lane == StrategyGenerationLaneV1.TypedGraph);
        lanes[index] = lanes[index] with { Readiness = StrategyGenerationReadinessV1.TestPassed };
        var relabeled = original with { Lanes = lanes };

        var issues = StrategyGenerationBatchValidationV1.Validate(relabeled);
        var selection = StrategyGenerationBatchValidationV1.Select(relabeled, lanes[index].CandidateHashSha256);

        issues.Should().Contain(issue => issue.Code == "BATCH_READINESS_STALE" &&
            issue.Message.Contains("no package-test evidence", StringComparison.Ordinal));
        selection.Success.Should().BeFalse();
    }

    [Fact]
    public void Cli_prompt_keeps_the_existing_CSharp_suffix_for_legacy_two_argument_requests()
    {
        var request = new StrategyCodegenRequest(
            "Existing expert-code context.",
            [new CodegenMessage(CodegenRole.User, "Build a strategy plugin.")]);

        var flattened = AgentCliCodegenClient.FlattenPrompt(request);

        request.OutputContract.Should().Be(StrategyCodegenOutputContract.CSharpPluginFiles);
        flattened.Should().Contain("one ```csharp fenced block");
        flattened.Should().Contain("COMPLETE plugin");
    }

    private static ParallelStrategyCandidateGeneratorV1 ProductionGenerator() => new(
        StrategyGenerationLaneCatalogV1.Ordered.Select(lane =>
            (IStrategyGenerationLaneAgentV1)new StrategyGenerationLaneAgentV1(lane)));

    private static StrategyGenerationLaneResultV1 CoherentResult(
        StrategyGenerationLaneV1 lane,
        string candidateId)
    {
        var candidate = CandidateForLane(lane, candidateId, Request);
        var issues = StrategyGenerationCandidateValidatorV1.Validate(
            candidate,
            lane,
            candidateId,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                Request.StrategyId,
                Request.UserPrompt,
                lane));
        return new StrategyGenerationLaneResultV1(
            lane,
            lane == StrategyGenerationLaneV1.TypedGraph
                ? StrategyGenerationReadinessV1.PackageValid
                : StrategyGenerationReadinessV1.Generated,
            candidate,
            StrategyGenerationCandidateCanonicalJsonV1.Hash(candidate),
            issues,
            Run(lane, success: true));
    }

    private static StrategyGenerationAgentRunV1 Run(
        StrategyGenerationLaneV1 lane,
        bool success,
        string? error = null) =>
        new(
            $"test.{StrategyGenerationLaneCatalogV1.WireName(lane)}@1",
            "test",
            null,
            success,
            error,
            null,
            new CodegenUsage(10, 5));

    private static StrategyGenerationCandidateV1 CandidateForLane(
        StrategyGenerationLaneV1 lane,
        string candidateId,
        ParallelStrategyGenerationRequestV1 request) =>
        lane == StrategyGenerationLaneV1.TypedGraph
            ? GraphCandidate(candidateId, request)
            : new StrategyGenerationCandidateV1(
                StrategyGenerationCandidateV1.CurrentSchemaVersion,
                candidateId,
                lane,
                StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                    request.StrategyId,
                    request.UserPrompt,
                    lane),
                StrategyGenerationPackageCatalogV1.RequireBinding(lane),
                $"{StrategyGenerationLaneCatalogV1.DisplayName(lane)} EMA cross",
                "Compare causal fast and slow EMAs and emit an inert signal and target.",
                [],
                ["The terminal will bind the declared instrument and event source after selection."],
                [
                    new StrategyGenerationParameterV1("fast_period", "integer", "4", "events", "Fast EMA period."),
                    new StrategyGenerationParameterV1("slow_period", "integer", "12", "events", "Slow EMA period."),
                    new StrategyGenerationParameterV1("quantity", "number", "5", "shares", "Absolute target size."),
                ],
                [new StrategyVariationAxisV1(
                    "ema-periods",
                    StrategyVariationAxisKindV1.Parameter,
                    "Compare causal fast/slow period pairs.",
                    ["4/12", "8/24"])],
                ArtifactForLane(lane, request.StrategyId.Trim()),
                "Edit the parameters or lane-native logic, then rerun deterministic validation.",
                ["Check causality and expected EMA-cross signal transitions."]);

    private static StrategyGenerationArtifactV1 ArtifactForLane(
        StrategyGenerationLaneV1 lane,
        string strategyId) => lane switch
        {
            StrategyGenerationLaneV1.VibePython => new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.VibePythonSource,
                "strategy.py",
                "python",
                """
                PARAMETERS = {"fast_period": 4, "slow_period": 12, "quantity": 5.0}

                def initialize_state():
                    return {"fast": None, "slow": None}

                def update_ema(previous, value, period):
                    alpha = 2.0 / (period + 1.0)
                    return value if previous is None else alpha * value + (1.0 - alpha) * previous

                def on_event(event, state, parameters):
                    price = float(event["mid"])
                    state["fast"] = update_ema(state["fast"], price, parameters["fast_period"])
                    state["slow"] = update_ema(state["slow"], price, parameters["slow_period"])
                    signal = 1 if state["fast"] > state["slow"] else -1
                    return {"signal": signal, "target": signal * parameters["quantity"]}
                """,
                null),
            StrategyGenerationLaneV1.DeclarativeSpec => new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.DeclarativeStrategyJson,
                "strategy.spec.json",
                "json",
                null,
                Json($$"""
                    {
                      "schemaVersion": "declarative-strategy/v1",
                      "strategy": {
                        "id": {{JsonSerializer.Serialize(strategyId)}},
                        "summary": "Causal EMA cross",
                        "universe": ["equity/xnas/ALPHA"],
                        "clock": "quote"
                      },
                      "parameters": [
                        {"name":"fast_period","type":"integer","default":4},
                        {"name":"slow_period","type":"integer","default":12},
                        {"name":"quantity","type":"number","default":5}
                      ],
                      "dataRequirements": [],
                      "indicators": [
                        {"id":"fast","kind":"ema","input":"mid","period":"fast_period"},
                        {"id":"slow","kind":"ema","input":"mid","period":"slow_period"}
                      ],
                      "entryRules": [{"when":"fast > slow","target":"quantity"}],
                      "exitRules": [{"when":"fast <= slow","target":0}],
                      "risk": {"sizingRule":"fixed_quantity","maximumAbsoluteTarget":"quantity"}
                    }
                    """)),
            StrategyGenerationLaneV1.CspPython => new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.CspPythonSource,
                "strategy.csp.py",
                "python",
                """
                import csp
                @csp.node
                def ema_cross_signal(fast: csp.ts[float], slow: csp.ts[float]) -> csp.ts[int]:
                    if csp.ticked(fast, slow):
                        return 1 if fast > slow else -1

                @csp.graph
                def strategy(fast: csp.ts[float], slow: csp.ts[float]) -> csp.ts[int]:
                    return ema_cross_signal(fast, slow)
                """,
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, "Expected a source/spec lane."),
        };

    private static StrategyGenerationCandidateV1 GraphCandidate(
        string candidateId,
        ParallelStrategyGenerationRequestV1? request = null)
    {
        request ??= Request;
        var registry = StrategyOperatorRegistryV1.CreateDefault();
        var definition = new StrategyIntermediateRepresentationV1(
            StrategyIntermediateRepresentationV1.CurrentSchemaVersion,
            request.StrategyId.Trim(),
            "1.0.0",
            registry.Catalog,
            StrategyClockKindV1.EventTime,
            [QuoteRequirement()],
            [
                Node("decision", "logic.greater_than", Bindings(("left", "fast"), ("right", "slow"))),
                Node("execution", "execution.market", Bindings(("target", "target")),
                    Parameters(("time_in_force", StrategyLiteralV1.FromText("day")))),
                Node("fast", "feature.ema", Bindings(("value", "price")),
                    Parameters(("period", StrategyLiteralV1.FromInteger(4)))),
                Node("price", "market.quote.mid", Bindings(),
                    Parameters(("requirement_id", StrategyLiteralV1.FromText("quotes")))),
                Node("slow", "feature.ema", Bindings(("value", "price")),
                    Parameters(("period", StrategyLiteralV1.FromInteger(12)))),
                Node("target", "portfolio.fixed_quantity", Bindings(("decision", "decision")),
                    Parameters(
                        ("when_false", StrategyLiteralV1.FromNumber(-5)),
                        ("when_true", StrategyLiteralV1.FromNumber(5)))),
            ],
            [
                new StrategyIrOutputBindingV1("order-intent", StrategyIrOutputKindV1.OrderIntent, "execution"),
                new StrategyIrOutputBindingV1("signal", StrategyIrOutputKindV1.Signal, "decision"),
                new StrategyIrOutputBindingV1("target", StrategyIrOutputKindV1.Target, "target"),
            ],
            FlattenOnEnd: true);
        var module = new OperatorGraphModuleV1(
            TradeIrModuleV1.CurrentSchemaVersion,
            "ema-cross",
            definition);
        var moduleJson = SerializeGraph(module);

        return new StrategyGenerationCandidateV1(
            StrategyGenerationCandidateV1.CurrentSchemaVersion,
            candidateId,
            StrategyGenerationLaneV1.TypedGraph,
            StrategyGenerationCandidateCanonicalJsonV1.RequestHash(
                request.StrategyId,
                request.UserPrompt,
                StrategyGenerationLaneV1.TypedGraph),
            StrategyGenerationPackageCatalogV1.RequireBinding(StrategyGenerationLaneV1.TypedGraph),
            "Canonical EMA cross",
            "Compare causal fast and slow EMAs and export an inert market-order intent.",
            [],
            ["The declared quote schema will be bound by the package after selection."],
            [
                new StrategyGenerationParameterV1("fast_period", "integer", "4", "quotes", "Fast EMA period."),
                new StrategyGenerationParameterV1("slow_period", "integer", "12", "quotes", "Slow EMA period."),
            ],
            [new StrategyVariationAxisV1(
                "ema-periods",
                StrategyVariationAxisKindV1.Parameter,
                "Compare causal fast/slow period pairs.",
                ["4/12", "8/24"])],
            new StrategyGenerationArtifactV1(
                StrategyGenerationArtifactKindV1.TradeIrModuleJson,
                "strategy.tradeir.json",
                "json",
                null,
                Json(moduleJson)),
            "Edit only installed operator parameters and bindings, then revalidate the module.",
            ["Validate the module with TradeIrModuleValidatorV1."]);
    }

    private static DataRequirementV1 QuoteRequirement() => new(
        "quotes",
        TradeIrDataKindV1.QuoteL1,
        new SourceIndependentInstrumentSelectorV1(
        [
            new SourceIndependentInstrumentRef(
                "equity/xnas/ALPHA",
                AssetClass.Equity,
                "ALPHA",
                "XNAS",
                "USD"),
        ]),
        new CanonicalEventSchemaV1(
            "canonical.quote-l1",
            1,
            new string('a', 64),
            ["ask", "ask_size", "bid", "bid_size"]),
        new DataTemporalSemanticsV1(
            TradeIrEventTimeBasisV1.OccurredAtUtc,
            TradeIrTimestampPrecisionV1.Microseconds,
            TradeIrEventOrderingV1.EventTimeThenSourceSequence,
            null,
            RequireAuthoritativeEventTime: true,
            RequirePointInTimeAvailability: true),
        TradeIrNormalizationPolicyV1.RawUnadjusted,
        TradeIrMissingDataPolicyV1.Reject,
        TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime);

    private static StrategyIrNodeV1 Node(
        string id,
        string operatorId,
        IReadOnlyDictionary<string, string> bindings,
        IReadOnlyDictionary<string, StrategyLiteralV1>? parameters = null) =>
        new(id, operatorId, 1, bindings, parameters ?? Parameters());

    private static IReadOnlyDictionary<string, string> Bindings(params (string Port, string NodeId)[] values) =>
        values.ToDictionary(static value => value.Port, static value => value.NodeId, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, StrategyLiteralV1> Parameters(
        params (string Name, StrategyLiteralV1 Value)[] values) =>
        values.ToDictionary(static value => value.Name, static value => value.Value, StringComparer.Ordinal);

    private static OperatorGraphModuleV1 ReadGraph(StrategyGenerationCandidateV1 candidate)
    {
        var document = candidate.Artifact.Document!.Value;
        return new OperatorGraphModuleV1(
            document.GetProperty("schemaVersion").GetString()!,
            document.GetProperty("moduleId").GetString()!,
            StrategyIrCanonicalJsonV1.Deserialize(document.GetProperty("definition").GetRawText()));
    }

    private static StrategyGenerationCandidateV1 ReplaceGraph(
        StrategyGenerationCandidateV1 candidate,
        OperatorGraphModuleV1 graph) =>
        candidate with
        {
            Artifact = candidate.Artifact with
            {
                Document = Json(SerializeGraph(graph)),
            },
        };

    private static StrategyGenerationCandidateV1 Malform(StrategyGenerationCandidateV1 candidate) =>
        candidate.Lane switch
        {
            StrategyGenerationLaneV1.VibePython => candidate with
            {
                Artifact = candidate.Artifact with { Source = "# no strategy function" },
            },
            StrategyGenerationLaneV1.DeclarativeSpec => candidate with
            {
                Artifact = candidate.Artifact with
                {
                    Document = Json("""{"schemaVersion":"declarative-strategy/v1"}"""),
                },
            },
            StrategyGenerationLaneV1.TypedGraph => ReplaceGraph(
                candidate,
                ReadGraph(candidate) with
                {
                    Definition = ReadGraph(candidate).Definition with
                    {
                        OperatorCatalog = ReadGraph(candidate).Definition.OperatorCatalog with
                        {
                            CatalogHashSha256 = new string('0', 64),
                        },
                    },
                }),
            StrategyGenerationLaneV1.CspPython => candidate with
            {
                Artifact = candidate.Artifact with { Source = "def strategy():\n    return None\n" },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Lane, "Unknown lane."),
        };

    private static StrategyGenerationArtifactV1 ValidLocalEdit(StrategyGenerationCandidateV1 candidate) =>
        candidate.Lane switch
        {
            StrategyGenerationLaneV1.VibePython or StrategyGenerationLaneV1.CspPython =>
                candidate.Artifact with { Source = candidate.Artifact.Source + "\n# locally edited\n" },
            StrategyGenerationLaneV1.DeclarativeSpec => candidate.Artifact with
            {
                Document = AddJsonProperty(candidate.Artifact.Document!.Value, "editorNote", "locally edited"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Lane, "Expected a generation-only lane."),
        };

    private static JsonElement AddJsonProperty(JsonElement source, string name, string value)
    {
        var values = source.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
        values[name] = JsonSerializer.SerializeToElement(value);
        return JsonSerializer.SerializeToElement(values);
    }

    private static string PromptFor(
        FourLaneArtifactProvider provider,
        StrategyGenerationLaneV1 lane)
    {
        var candidateId = $"{Request.StrategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
        return provider.Requests
            .Select(AgentCliCodegenClient.FlattenPrompt)
            .Single(prompt => prompt.Contains(candidateId, StringComparison.Ordinal));
    }

    private static string SerializeGraph(OperatorGraphModuleV1 graph) =>
        ExecutableStrategyDefinitionCanonicalJson.Canonicalize(
            "{" +
            "\"moduleKind\":\"operatorGraph\"," +
            $"\"schemaVersion\":{JsonSerializer.Serialize(graph.SchemaVersion)}," +
            $"\"moduleId\":{JsonSerializer.Serialize(graph.ModuleId)}," +
            $"\"definition\":{StrategyIrCanonicalJsonV1.Serialize(graph.Definition)}" +
            "}");

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Describe(IEnumerable<StrategyCandidateGenerationIssueV1> issues) =>
        string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Code} {issue.Path}: {issue.Message}"));

    private sealed class SynchronousProgressRecorder : IProgress<StrategyGenerationLaneProgressV1>
    {
        private readonly ConcurrentQueue<StrategyGenerationLaneProgressV1> _events = new();

        public void Report(StrategyGenerationLaneProgressV1 value) => _events.Enqueue(value);

        public IReadOnlyList<StrategyGenerationLaneProgressStateV1> StatesFor(StrategyGenerationLaneV1 lane) =>
            _events.Where(item => item.Lane == lane).Select(item => item.State).ToArray();
    }

    private sealed class GatedLaneAgent(
        StrategyGenerationLaneV1 lane,
        bool honorCancellation = true) : IStrategyGenerationLaneAgentV1
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StrategyGenerationLaneV1 Lane { get; } = lane;
        public Task Started => _started.Task;
        public Task Completed => _completed.Task;

        public async Task<StrategyGenerationLaneResultV1> GenerateAsync(
            IStrategyCodegenClient provider,
            ParallelStrategyGenerationRequestV1 request,
            string expectedCandidateId,
            CancellationToken ct = default)
        {
            _started.TrySetResult();
            if (honorCancellation)
                await _release.Task.WaitAsync(ct);
            else
                await _release.Task;
            var result = CoherentResult(Lane, expectedCandidateId);
            _completed.TrySetResult();
            return result;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class UnusedProvider : IStrategyCodegenClient
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public string ProviderId => "unused";
        public string DisplayName => "Unused";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(StrategyCodegenResponse.Fail("This provider should not be called."));
        }
    }

    private sealed class FourLaneArtifactProvider : IStrategyCodegenClient
    {
        private readonly ParallelStrategyGenerationRequestV1 _generationRequest;
        private readonly Func<StrategyGenerationLaneV1, StrategyGenerationCandidateV1,
            StrategyGenerationCandidateV1>? _change;
        private readonly StrategyGenerationLaneV1? _failedLane;
        private int _callCount;

        public FourLaneArtifactProvider(
            Func<StrategyGenerationLaneV1, StrategyGenerationCandidateV1,
                StrategyGenerationCandidateV1>? change = null,
            StrategyGenerationLaneV1? failedLane = null)
            : this(Request, change, failedLane)
        {
        }

        public FourLaneArtifactProvider(
            ParallelStrategyGenerationRequestV1 generationRequest,
            Func<StrategyGenerationLaneV1, StrategyGenerationCandidateV1,
                StrategyGenerationCandidateV1>? change = null,
            StrategyGenerationLaneV1? failedLane = null)
        {
            _generationRequest = generationRequest;
            _change = change;
            _failedLane = failedLane;
        }

        public int CallCount => Volatile.Read(ref _callCount);
        public string ProviderId => "four-lane-artifact";
        public string DisplayName => "Four lane artifact";
        public bool IsAvailable => true;
        public ConcurrentQueue<StrategyCodegenRequest> Requests { get; } = new();

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            Requests.Enqueue(request);
            var prompt = AgentCliCodegenClient.FlattenPrompt(request);
            var strategyId = _generationRequest.StrategyId.Trim();
            var lane = StrategyGenerationLaneCatalogV1.Ordered.Single(candidateLane =>
                prompt.Contains(
                    $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(candidateLane)}",
                    StringComparison.Ordinal));
            if (_failedLane == lane)
                return Task.FromResult(StrategyCodegenResponse.Fail($"Simulated {lane} provider failure."));

            var candidateId = $"{strategyId}/{StrategyGenerationLaneCatalogV1.WireName(lane)}";
            var candidate = CandidateForLane(lane, candidateId, _generationRequest);
            candidate = _change?.Invoke(lane, candidate) ?? candidate;
            return Task.FromResult(StrategyCodegenResponse.Reply(
                StrategyGenerationCandidateCanonicalJsonV1.Serialize(candidate),
                new CodegenUsage(50, 25)));
        }
    }
}
