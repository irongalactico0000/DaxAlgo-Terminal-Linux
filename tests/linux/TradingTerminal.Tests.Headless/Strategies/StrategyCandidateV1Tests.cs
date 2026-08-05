using System.Text.Json.Nodes;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class StrategyCandidateV1Tests
{
    [Fact]
    public void Triangle_idea_is_a_valid_editable_candidate_but_not_claimed_executable()
    {
        var candidate = TriangleCandidate();

        var assessment = StrategyCandidateValidatorV1.Assess(candidate);

        assessment.IsStructurallyValid.Should().BeTrue(Describe(assessment));
        assessment.CanConfirm.Should().BeFalse();
        assessment.CanLower.Should().BeFalse();
        assessment.Issues.Should().Contain(issue =>
            issue.Code == "CANDIDATE_QUESTION_OPEN" && issue.Message.Contains("instrument and timeframe"));
        assessment.Issues.Should().Contain(issue =>
            issue.Code == "CANDIDATE_BUILD_SUPPORT_INCOMPLETE" && issue.Message.Contains("Causal swing-pivot detector"));
        assessment.Issues.Should().Contain(issue =>
            issue.Code == "CANDIDATE_BUILD_SUPPORT_INCOMPLETE" && issue.Message.Contains("Converging-trendline triangle detector"));

        candidate.Interpretation.Summary.Should().Contain("symmetrical price triangle");
        candidate.Interpretation.Alternatives.Should().Contain(alternative =>
            alternative.Summary.Contains("triangular arbitrage"));
        candidate.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == "triangle-pattern").Statements
            .Should().Contain(statement => statement.Text.Contains("lower swing highs"));
    }

    [Fact]
    public void Confirmed_ema_candidate_with_supported_requirements_is_ready_for_lowering()
    {
        var candidate = EmaCandidate();

        var assessment = StrategyCandidateValidatorV1.Assess(candidate);

        assessment.IsStructurallyValid.Should().BeTrue(Describe(assessment));
        assessment.CanConfirm.Should().BeTrue(Describe(assessment));
        assessment.CanLower.Should().BeTrue(Describe(assessment));
    }

    [Fact]
    public void Understood_options_candidate_can_be_confirmed_without_claiming_missing_chain_data()
    {
        var candidate = EmaCandidate() with
        {
            CandidateId = "options-volatility",
            Title = "Short elevated implied volatility before expiry",
            RawIntent = "Sell expensive option volatility before expiry.",
            Interpretation = new StrategyCandidateInterpretationV1(
                "Sell a defined-risk option spread when implied volatility is elevated before expiry.",
                StrategyInterpretationConfidenceV1.High,
                []),
            BuildSupport =
            [
                Support("support-options-chain", "Point-in-time option chain and Greeks",
                    StrategyBuildSupportStatusV1.DataUnavailable, true,
                    "The current strategy-generation target has no historical point-in-time option-chain binding.",
                    "rule-market", "rule-entry", "rule-exit"),
            ],
        };

        var assessment = StrategyCandidateValidatorV1.Assess(candidate);

        assessment.IsStructurallyValid.Should().BeTrue(Describe(assessment));
        assessment.CanConfirm.Should().BeTrue(Describe(assessment));
        assessment.CanLower.Should().BeFalse();
        assessment.Issues.Should().ContainSingle(issue =>
            issue.Scope == StrategyCandidateIssueScopeV1.BuildSupport &&
            issue.Code == "CANDIDATE_BUILD_SUPPORT_INCOMPLETE");
    }

    [Fact]
    public void Omitting_build_support_does_not_make_a_confirmed_strategy_executable()
    {
        var candidate = EmaCandidate() with { BuildSupport = [] };

        var assessment = StrategyCandidateValidatorV1.Assess(candidate);

        assessment.IsStructurallyValid.Should().BeTrue(Describe(assessment));
        assessment.CanConfirm.Should().BeTrue(Describe(assessment));
        assessment.CanLower.Should().BeFalse();
        assessment.Issues.Should().Contain(issue => issue.Code == "CANDIDATE_BUILD_SUPPORT_EMPTY");
        assessment.Issues.Should().Contain(issue => issue.Code == "CANDIDATE_BUILD_SUPPORT_MISSING");
    }

    [Fact]
    public void Candidate_round_trips_and_hashes_independently_of_json_property_order()
    {
        var candidate = EmaCandidate();
        var json = StrategyCandidateCanonicalJsonV1.Serialize(candidate);
        var original = JsonNode.Parse(json)!.AsObject();
        var reorderedObject = new JsonObject
        {
            ["title"] = original["title"]!.DeepClone(),
        };
        foreach (var property in original.Where(static property => property.Key != "title"))
            reorderedObject[property.Key] = property.Value?.DeepClone();
        var reordered = reorderedObject.ToJsonString();

        StrategyCandidateCanonicalJsonV1.Deserialize(json).Should().BeEquivalentTo(candidate);
        StrategyCandidateCanonicalJsonV1.Canonicalize(reordered)
            .Should().Be(StrategyCandidateCanonicalJsonV1.Canonicalize(json));
        StrategyCandidateCanonicalJsonV1.Hash(candidate).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Duplicate_ids_and_a_confirmed_open_question_are_rejected_deterministically()
    {
        var candidate = TriangleCandidate() with { Status = StrategyCandidateStatusV1.Confirmed };
        candidate = candidate with
        {
            BuildSupport =
            [
                Support("signal", "Colliding id", StrategyBuildSupportStatusV1.Supported, false, "Collision test."),
            ],
        };

        var assessment = StrategyCandidateValidatorV1.Assess(candidate);

        assessment.IsStructurallyValid.Should().BeFalse();
        assessment.CanLower.Should().BeFalse();
        assessment.Issues.Should().Contain(issue => issue.Code == "CANDIDATE_ID_DUPLICATE");
        assessment.Issues.Should().Contain(issue => issue.Code == "CANDIDATE_CONFIRMED_WITH_OPEN_DECISIONS");
    }

    [Fact]
    public void Workspace_selects_an_exact_candidate_revision()
    {
        var first = EmaCandidate() with { Status = StrategyCandidateStatusV1.Superseded };
        var second = first with
        {
            Revision = 2,
            ParentContentHashSha256 = StrategyCandidateCanonicalJsonV1.Hash(first),
            Status = StrategyCandidateStatusV1.Confirmed,
        };
        var workspace = new StrategyGenerationWorkspaceV1(
            StrategyGenerationWorkspaceV1.CurrentSchemaVersion,
            "workspace/demo",
            "Strategy Builder Demo",
            [first, second],
            second.CandidateId,
            second.Revision);

        StrategyCandidateValidatorV1.ValidateWorkspace(workspace).Should().BeEmpty();
    }

    [Fact]
    public void Specialist_amendment_can_replace_only_its_assigned_nested_group()
    {
        var baseline = TriangleCandidate();
        var request = new StrategySpecialistRequestV1(
            "request-pattern",
            "technical.chart_pattern@1",
            "triangle-pattern",
            "Operationalize a causal symmetrical-triangle detector without choosing user parameters.",
            Required: true);
        var replacement = baseline.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == "triangle-pattern") with
        {
            Summary = "A chart-pattern specialist proposed causal pivots, convergence, and completed-bar breakout semantics.",
        };
        var amendment = new StrategyCandidateAmendmentV1(
            request.RequestId,
            request.SpecialistId,
            request.TargetGroupId,
            replacement,
            [
                Support("support-pattern-review", "Triangle semantic review",
                    StrategyBuildSupportStatusV1.Unknown, false,
                    "The semantic proposal still requires deterministic review; executable pattern operators remain separately unsupported.",
                    "rule-pivots", "rule-convergence", "rule-breakout"),
            ]);

        var result = StrategyCandidateComposerV1.Compose(
            new StrategyCandidateDraftV1(baseline, [request]),
            [amendment]);

        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        result.Candidate!.Revision.Should().Be(2);
        result.Candidate.ParentContentHashSha256.Should().Be(StrategyCandidateCanonicalJsonV1.Hash(baseline));
        result.Candidate.Status.Should().Be(StrategyCandidateStatusV1.AwaitingConfirmation);
        result.Candidate.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == "triangle-pattern").Summary.Should().Be(replacement.Summary);
        result.Candidate.BuildSupport.Should().Contain(item => item.SupportId == "support-pattern-review");
        result.Assessment!.CanLower.Should().BeFalse("specialists propose meaning but cannot confirm it for the user");
    }

    [Fact]
    public void Conflicting_specialist_ownership_is_rejected_instead_of_using_agent_order()
    {
        var baseline = TriangleCandidate();
        var original = baseline.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == "triangle-pattern");
        var first = new StrategySpecialistRequestV1(
            "request-pattern", "technical.chart_pattern@1", "triangle-pattern", "Define the pattern.", true);
        var second = new StrategySpecialistRequestV1(
            "request-quant", "research.quant_rules@1", "triangle-pattern", "Review the rule.", true);

        var result = StrategyCandidateComposerV1.Compose(
            new StrategyCandidateDraftV1(baseline, [first, second]),
            [
                new StrategyCandidateAmendmentV1(first.RequestId, first.SpecialistId, first.TargetGroupId, original, []),
                new StrategyCandidateAmendmentV1(second.RequestId, second.SpecialistId, second.TargetGroupId, original, []),
            ]);

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "COMPOSE_TARGET_CONFLICT");
    }

    [Fact]
    public void Missing_required_specialist_result_is_explicit()
    {
        var request = new StrategySpecialistRequestV1(
            "request-options", "domain.options@1", "signal", "Resolve expiry and volatility semantics.", true);

        var result = StrategyCandidateComposerV1.Compose(
            new StrategyCandidateDraftV1(TriangleCandidate(), [request]),
            []);

        result.Success.Should().BeFalse();
        result.Issues.Should().ContainSingle(issue => issue.Code == "COMPOSE_REQUIRED_SPECIALIST_MISSING");
    }

    [Fact]
    public void Specialist_cannot_mark_its_own_build_support_as_supported()
    {
        var baseline = TriangleCandidate();
        var request = new StrategySpecialistRequestV1(
            "request-pattern", "technical.chart_pattern@1", "triangle-pattern", "Define the pattern.", true);
        var target = baseline.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == request.TargetGroupId);
        var amendment = new StrategyCandidateAmendmentV1(
            request.RequestId,
            request.SpecialistId,
            request.TargetGroupId,
            target,
            [Support("specialist-self-approval", "Pattern operator", StrategyBuildSupportStatusV1.Supported,
                true, "The specialist claims its own work is installed.", "rule-pivots")]);

        var result = StrategyCandidateComposerV1.Compose(
            new StrategyCandidateDraftV1(baseline, [request]),
            [amendment]);

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "COMPOSE_SUPPORT_AUTHORITY_VIOLATION");
    }

    [Fact]
    public void Specialist_cannot_change_support_for_a_statement_outside_its_assigned_group()
    {
        var baseline = TriangleCandidate();
        var request = new StrategySpecialistRequestV1(
            "request-pattern", "technical.chart_pattern@1", "triangle-pattern", "Define the pattern.", true);
        var target = baseline.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == request.TargetGroupId);
        var amendment = new StrategyCandidateAmendmentV1(
            request.RequestId,
            request.SpecialistId,
            request.TargetGroupId,
            target,
            [Support("specialist-cross-group", "Unrelated market decision", StrategyBuildSupportStatusV1.Unknown,
                true, "The pattern specialist attempted to assess another group's statement.", "question-market")]);

        var result = StrategyCandidateComposerV1.Compose(
            new StrategyCandidateDraftV1(baseline, [request]),
            [amendment]);

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "COMPOSE_SUPPORT_SCOPE_VIOLATION");
    }

    [Fact]
    public void Specialist_cannot_claim_deterministic_system_provenance_for_its_statements()
    {
        var baseline = TriangleCandidate();
        var request = new StrategySpecialistRequestV1(
            "request-pattern", "technical.chart_pattern@1", "triangle-pattern", "Define the pattern.", true);
        var target = baseline.Groups.Single(group => group.GroupId == "signal").Children
            .Single(group => group.GroupId == request.TargetGroupId);
        var replacement = target with
        {
            Statements = target.Statements.Select(statement => statement with
            {
                Source = StrategyCandidateStatementSourceV1.DeterministicSystem,
            }).ToArray(),
        };

        var result = StrategyCandidateComposerV1.Compose(
            new StrategyCandidateDraftV1(baseline, [request]),
            [new StrategyCandidateAmendmentV1(
                request.RequestId, request.SpecialistId, request.TargetGroupId, replacement, [])]);

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "COMPOSE_STATEMENT_AUTHORITY_VIOLATION");
    }

    [Fact]
    public void Confirmation_is_bound_to_the_reviewed_hash_and_promotes_proposals_only()
    {
        var baseline = EmaCandidate() with
        {
            Status = StrategyCandidateStatusV1.AwaitingConfirmation,
            Groups = EmaCandidate().Groups
                .Select(group => group with
                {
                    Statements = group.Statements.Select(statement => statement with
                    {
                        Source = StrategyCandidateStatementSourceV1.Agent,
                        State = StrategyCandidateStatementStateV1.Proposed,
                    }).ToArray(),
                })
                .ToArray(),
        };
        var reviewedHash = StrategyCandidateCanonicalJsonV1.Hash(baseline);

        var result = StrategyCandidateConfirmationV1.Confirm(baseline, reviewedHash);

        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        result.Candidate!.Revision.Should().Be(2);
        result.Candidate.ParentContentHashSha256.Should().Be(reviewedHash);
        result.Candidate.Status.Should().Be(StrategyCandidateStatusV1.Confirmed);
        result.Candidate.Groups.SelectMany(group => group.Statements)
            .Should().OnlyContain(statement => statement.State == StrategyCandidateStatementStateV1.Confirmed);
        result.Assessment!.CanLower.Should().BeTrue();
    }

    [Fact]
    public void Confirmation_rejects_a_stale_hash_and_never_answers_triangle_questions()
    {
        var candidate = TriangleCandidate();

        var stale = StrategyCandidateConfirmationV1.Confirm(candidate, new string('0', 64));
        var exact = StrategyCandidateConfirmationV1.Confirm(
            candidate, StrategyCandidateCanonicalJsonV1.Hash(candidate));

        stale.Success.Should().BeFalse();
        stale.Issues.Should().Contain(issue => issue.Code == "CONFIRM_HASH_MISMATCH");
        exact.Success.Should().BeFalse();
        exact.Issues.Should().Contain(issue =>
            issue.Code == "CONFIRM_DECISION_REQUIRED" && issue.Message.Contains("instrument and timeframe"));
        candidate.Groups.SelectMany(Flatten).SelectMany(group => group.Statements)
            .Should().Contain(statement => statement.Kind == StrategyCandidateStatementKindV1.Question &&
                                             statement.State == StrategyCandidateStatementStateV1.Open);
    }

    [Fact]
    public void Lowering_boundary_exports_only_the_exact_confirmed_supported_candidate()
    {
        var ema = EmaCandidate();

        var ready = StrategyCandidateLoweringBoundaryV1.Create(ema);
        var triangle = StrategyCandidateLoweringBoundaryV1.Create(TriangleCandidate());

        ready.Success.Should().BeTrue();
        ready.Request!.CandidateId.Should().Be(ema.CandidateId);
        ready.Request.CandidateRevision.Should().Be(ema.Revision);
        ready.Request.CandidateContentHashSha256.Should().Be(StrategyCandidateCanonicalJsonV1.Hash(ema));
        StrategyCandidateCanonicalJsonV1.Deserialize(ready.Request.CanonicalCandidateJson)
            .Should().BeEquivalentTo(ema);
        triangle.Success.Should().BeFalse();
        triangle.Issues.Should().Contain(issue =>
            issue.Scope == StrategyCandidateIssueScopeV1.Confirmation ||
            issue.Scope == StrategyCandidateIssueScopeV1.BuildSupport);
    }

    private static StrategyCandidateV1 TriangleCandidate() => new(
        StrategyCandidateV1.CurrentSchemaVersion,
        "triangle-breakout",
        1,
        null,
        "Build a strategy when candles form a triangle.",
        "Triangle breakout",
        StrategyCandidateStatusV1.AwaitingConfirmation,
        new StrategyCandidateInterpretationV1(
            "Trade a confirmed breakout from a symmetrical price triangle formed by converging swing highs and lows.",
            StrategyInterpretationConfidenceV1.Medium,
            [
                new StrategyInterpretationAlternativeV1("ascending-triangle", "Trade an ascending or descending chart triangle."),
                new StrategyInterpretationAlternativeV1("triangular-arbitrage", "Trade a three-leg currency or venue triangular arbitrage."),
            ]),
        [
            Group(
                "market",
                StrategyCandidateGroupKindV1.MarketAndUniverse,
                "Market and timeframe",
                "The instrument and bar interval are not yet known.",
                [Question("question-market", "Which instrument and timeframe should this strategy use?")]),
            Group(
                "signal",
                StrategyCandidateGroupKindV1.SignalAndAlpha,
                "Signal and alpha",
                "Detect a causal triangle and enter only after a completed-bar breakout.",
                [],
                [
                    Group(
                        "triangle-pattern",
                        StrategyCandidateGroupKindV1.SignalAndAlpha,
                        "Triangle pattern",
                        "Operational definition proposed by the chart-pattern specialist.",
                        [
                            ProposedRule("rule-pivots", "Require at least two confirmed lower swing highs and two confirmed higher swing lows."),
                            ProposedRule("rule-convergence", "Fit upper and lower trendlines that converge toward a future apex."),
                            ProposedRule("rule-breakout", "Enter only after a completed candle closes outside the relevant triangle boundary."),
                        ]),
                ]),
            Group(
                "risk",
                StrategyCandidateGroupKindV1.RiskAndExits,
                "Risk and exits",
                "Protective behavior remains a material proposal.",
                [ProposedRule("rule-stop", "Place the protective stop beyond the opposite triangle boundary.")]),
        ],
        [
            Support("support-bars", "Completed OHLCV bars", StrategyBuildSupportStatusV1.Supported, true,
                "Canonical completed bars are available.", "rule-breakout"),
            Support("support-pivots", "Causal swing-pivot detector", StrategyBuildSupportStatusV1.NeedsImplementation, true,
                "The current operator catalog has no pivot detector with an explicit right-bar confirmation delay.", "rule-pivots"),
            Support("support-triangle", "Converging-trendline triangle detector", StrategyBuildSupportStatusV1.NeedsImplementation, true,
                "The current operator catalog has no trendline convergence or triangle-pattern operator.", "rule-convergence"),
            Support("support-market-choice", "Instrument and timeframe selection", StrategyBuildSupportStatusV1.NeedsUserChoice, true,
                "The user has not selected the market or bar interval.", "question-market"),
        ]);

    private static StrategyCandidateV1 EmaCandidate() => new(
        StrategyCandidateV1.CurrentSchemaVersion,
        "ema-cross",
        1,
        null,
        "Trade EMA 4/12 with a trailing stop.",
        "EMA 4/12 trend strategy",
        StrategyCandidateStatusV1.Confirmed,
        new StrategyCandidateInterpretationV1(
            "Hold a long target when EMA(4) is above EMA(12), otherwise hold a short target, with a trailing exit.",
            StrategyInterpretationConfidenceV1.High,
            []),
        [
            Group(
                "market",
                StrategyCandidateGroupKindV1.MarketAndUniverse,
                "Market",
                "AAPL on completed five-minute bars.",
                [
                    ConfirmedRule("rule-market", "Evaluate AAPL on each completed five-minute bar.",
                        new StrategyCandidateValueV1("core.timeframe@1", "PT5M")),
                ]),
            Group(
                "signal",
                StrategyCandidateGroupKindV1.SignalAndAlpha,
                "EMA signal",
                "Compare causal fast and slow exponential moving averages.",
                [
                    ConfirmedRule("rule-entry", "Target long when EMA(4) is greater than EMA(12); otherwise target short."),
                ]),
            Group(
                "risk",
                StrategyCandidateGroupKindV1.RiskAndExits,
                "Risk and exits",
                "Use a confirmed trailing exit.",
                [
                    ConfirmedRule("rule-exit", "Apply a 2.5% trailing stop.",
                        new StrategyCandidateValueV1("core.percentage@1", "0.025", "fraction")),
                ]),
        ],
        [
            Support("support-bars", "Completed OHLCV bars", StrategyBuildSupportStatusV1.Supported, true,
                "Completed bars are supported.", "rule-market"),
            Support("support-ema", "EMA feature", StrategyBuildSupportStatusV1.Supported, true,
                "feature.ema@1 is installed.", "rule-entry"),
            Support("support-comparison", "Greater-than comparison", StrategyBuildSupportStatusV1.Supported, true,
                "logic.greater_than@1 is installed.", "rule-entry"),
            Support("support-exit", "Trailing fractional exit", StrategyBuildSupportStatusV1.Supported, true,
                "risk.trailing_fraction@1 is installed.", "rule-exit"),
        ]);

    private static StrategyCandidateGroupV1 Group(
        string id,
        StrategyCandidateGroupKindV1 kind,
        string title,
        string summary,
        IReadOnlyList<StrategyCandidateStatementV1> statements,
        IReadOnlyList<StrategyCandidateGroupV1>? children = null) =>
        new(id, kind, title, summary, statements, children ?? []);

    private static StrategyCandidateStatementV1 ProposedRule(string id, string text) =>
        new(id, StrategyCandidateStatementKindV1.Rule, text, StrategyCandidateStatementSourceV1.Agent,
            StrategyCandidateStatementStateV1.Proposed, true);

    private static StrategyCandidateStatementV1 ConfirmedRule(
        string id,
        string text,
        StrategyCandidateValueV1? value = null) =>
        new(id, StrategyCandidateStatementKindV1.Rule, text, StrategyCandidateStatementSourceV1.User,
            StrategyCandidateStatementStateV1.Confirmed, true, value);

    private static StrategyCandidateStatementV1 Question(string id, string text) =>
        new(id, StrategyCandidateStatementKindV1.Question, text, StrategyCandidateStatementSourceV1.Agent,
            StrategyCandidateStatementStateV1.Open, true);

    private static StrategyBuildSupportItemV1 Support(
        string id,
        string description,
        StrategyBuildSupportStatusV1 status,
        bool required,
        string detail,
        params string[] statementIds) =>
        new(id, description, status, required, detail, statementIds);

    private static string Describe(StrategyCandidateAssessmentV1 assessment) =>
        string.Join(Environment.NewLine, assessment.Issues.Select(issue => $"{issue.Scope}: {issue.Path}: {issue.Message}"));

    private static IEnumerable<StrategyCandidateGroupV1> Flatten(StrategyCandidateGroupV1 group)
    {
        yield return group;
        foreach (var child in group.Children.SelectMany(Flatten)) yield return child;
    }
}
