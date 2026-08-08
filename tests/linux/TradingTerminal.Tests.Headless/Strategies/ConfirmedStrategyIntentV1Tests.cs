using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Generation;
using TradingTerminal.Core.Strategies.Specification;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class ConfirmedStrategyIntentV1Tests
{
    [Fact]
    public void Complete_complex_directional_answers_confirm_without_turning_prose_into_a_DSL()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());

        var result = Confirm(fixture);

        result.Success.Should().BeTrue(Describe(result));
        result.Intent.Should().NotBeNull();
        result.Intent!.ReviewedDraftContentHashSha256.Should()
            .Be(StrategyIntentCanonicalJsonV1.Hash(fixture.Draft));
        fixture.Draft.Requirements.Should().OnlyContain(requirement =>
            requirement.Value != null &&
            requirement.Value.TypeId == "core.semantic_clause@1" &&
            !string.IsNullOrWhiteSpace(requirement.Value.CanonicalValue));
        fixture.Draft.Requirements.Single(x => x.RequirementId == "decision.long_condition")
            .Value!.CanonicalValue.Should().ContainAll("long", "upward", "SPY", "QQQ");
        fixture.Draft.Requirements.Single(x => x.RequirementId == "decision.short_condition")
            .Value!.CanonicalValue.Should().ContainAll("short", "downward", "SPY", "QQQ");
        fixture.Draft.Requirements.Single(x => x.RequirementId == "decision.no_trade_condition")
            .Value!.CanonicalValue.Should().ContainAll("no trade", "stale", "conflict");
        fixture.Draft.Requirements.Single(x => x.RequirementId == "exposure.sizing_formula")
            .Value!.CanonicalValue.Should().ContainAll("0.50%", "stop distance", "shares");
        fixture.Draft.Requirements.Single(x => x.RequirementId == "exposure.maximum_position")
            .Value!.CanonicalValue.Should().ContainAll("10%", "equity");
        fixture.Draft.Requirements.Single(x => x.RequirementId == "exposure.leverage_limit")
            .Value!.CanonicalValue.Should().ContainAll("gross", "net");
        fixture.Draft.Requirements.Single(x => x.RequirementId == "execution.limit_policy")
            .Value!.CanonicalValue.Should().ContainAll("limit", "cancel", "replace");
    }

    [Fact]
    public void Every_required_directional_answer_blocks_when_missing_or_unresolved()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var importantIds = new[]
        {
            "data.primary_instrument",
            "data.timeframe",
            "observe.trigger",
            "evidence.primary_threshold",
            "evidence.point_in_time",
            "evidence.data_freshness",
            "evidence.comparison_inputs",
            "evidence.cross_instrument",
            "evidence.conflict_no_trade",
            "decision.long_condition",
            "decision.short_condition",
            "decision.no_trade_condition",
            "decision.reversal_condition",
            "decision.position_actions",
            "decision.validity_window",
            "exposure.position_target",
            "exposure.sizing_formula",
            "exposure.maximum_position",
            "exposure.leverage_limit",
            "exposure.risk_budget",
            "exposure.early_tranche",
            "exposure.later_tranches",
            "execution.order_type_selection",
            "execution.market_policy",
            "execution.limit_policy",
            "execution.time_in_force",
            "execution.partial_fill_policy",
            "execution.cancel_replace",
            "lifecycle.fill_driven_state",
            "lifecycle.scale_policy",
            "finish.stop",
            "finish.target",
            "finish.trailing_stop",
            "finish.time_exit",
            "finish.reversal",
            "finish.invalidation",
            "finish.session_boundary",
        };
        fixture.Draft.Requirements.Select(x => x.RequirementId).Should().Contain(importantIds);

        foreach (var requirement in fixture.Draft.Requirements)
        {
            var missingDraft = fixture.Draft with
            {
                Requirements = fixture.Draft.Requirements
                    .Where(candidate => candidate.RequirementId != requirement.RequirementId)
                    .ToArray(),
            };
            var missing = Confirm(fixture with { Draft = missingDraft });
            missing.Success.Should().BeFalse($"{requirement.RequirementId} is a required answer");
            missing.Questions.Should().Contain(question => question.RequirementId == requirement.RequirementId,
                $"a missing {requirement.RequirementId} answer must remain visible");

            var unresolvedDraft = fixture.Draft with
            {
                Requirements = fixture.Draft.Requirements.Select(candidate =>
                    candidate.RequirementId == requirement.RequirementId
                        ? candidate with
                        {
                            Disposition = StrategySemanticDispositionV1.Unresolved,
                            Value = null,
                            DispositionRationale = "The user has not answered this question yet.",
                        }
                        : candidate).ToArray(),
            };
            var unresolved = Confirm(fixture with { Draft = unresolvedDraft });
            unresolved.Success.Should().BeFalse($"{requirement.RequirementId} is unresolved");
            unresolved.Questions.Should().Contain(question => question.RequirementId == requirement.RequirementId,
                $"an unresolved {requirement.RequirementId} answer must remain visible");
        }
    }

    [Fact]
    public void Wrong_schema_stage_and_material_unsupported_requirement_fail_closed()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var target = fixture.Draft.Requirements.Single(requirement =>
            requirement.RequirementId == "observe.trigger");

        var wrongSchema = Confirm(fixture with
        {
            Draft = fixture.Draft with { SchemaVersion = "strategy-intent-draft/v999" },
        });
        var wrongStage = Confirm(fixture with
        {
            Draft = fixture.Draft with
            {
                Requirements = fixture.Draft.Requirements.Select(requirement =>
                    requirement.RequirementId == target.RequirementId
                        ? requirement with { Stage = StrategySemanticStageV1.FinishOrUnwind }
                        : requirement).ToArray(),
            },
        });
        var unsupported = Confirm(fixture with
        {
            Draft = fixture.Draft with
            {
                Requirements = fixture.Draft.Requirements.Select(requirement =>
                    requirement.RequirementId == target.RequirementId
                        ? requirement with
                        {
                            Disposition = StrategySemanticDispositionV1.Unsupported,
                            Value = null,
                            DispositionRationale = "The selected implementation capability cannot represent it.",
                        }
                        : requirement).ToArray(),
            },
        });

        wrongSchema.Success.Should().BeFalse();
        wrongSchema.Issues.Should().Contain(issue => issue.Code == "INTENT_DRAFT_SCHEMA_UNSUPPORTED");
        wrongStage.Success.Should().BeFalse();
        wrongStage.Questions.Should().Contain(question =>
            question.RequirementId == target.RequirementId &&
            question.Reason.Contains("wrong lifecycle stage", StringComparison.Ordinal));
        unsupported.Success.Should().BeFalse();
        unsupported.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_MATERIAL_REQUIREMENT_UNSUPPORTED");
    }

    [Fact]
    public void Pair_intent_requires_complete_coordinated_legs_targets_and_unwind()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.MultiLegTarget, PairSpec());

        Confirm(fixture).Success.Should().BeTrue(Describe(Confirm(fixture)));
        fixture.Draft.Requirements.Single(x => x.RequirementId == "decision.coordinated_legs")
            .Value!.CanonicalValue.Should().ContainAll("AAPL", "MSFT", "together");

        AssertRemovingBlocks(fixture,
            "decision.coordinated_legs",
            "exposure.leg_targets",
            "execution.leg_coordination",
            "finish.multi_leg_unwind");
    }

    [Fact]
    public void Quote_set_requires_inventory_cancel_replace_fill_response_and_cancel_all()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.QuoteSet, QuoteSpec());

        Confirm(fixture).Success.Should().BeTrue(Describe(Confirm(fixture)));
        fixture.Draft.Requirements.Single(x => x.RequirementId == "execution.cancel_replace")
            .Value!.CanonicalValue.Should().ContainAll("Cancel", "replace");

        AssertRemovingBlocks(fixture,
            "decision.two_sided_quotes",
            "exposure.inventory_limits",
            "execution.cancel_replace",
            "lifecycle.quote_inventory",
            "lifecycle.quote_refresh",
            "finish.cancel_all_quotes");
    }

    [Fact]
    public void Portfolio_target_requires_weights_turnover_rebalance_and_rebalance_state()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PortfolioTarget, PortfolioSpec());

        Confirm(fixture).Success.Should().BeTrue(Describe(Confirm(fixture)));
        fixture.Draft.Requirements.Single(x => x.RequirementId == "exposure.portfolio_constraints")
            .Value!.CanonicalValue.Should().Contain("turnover");

        AssertRemovingBlocks(fixture,
            "decision.portfolio_targets",
            "exposure.portfolio_constraints",
            "exposure.turnover_limit",
            "execution.rebalance_policy",
            "lifecycle.rebalance_state");
    }

    [Fact]
    public void Execution_schedule_requires_parent_slice_control_and_residual_policy()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.ExecutionSchedule, ExecutionSpec());

        Confirm(fixture).Success.Should().BeTrue(Describe(Confirm(fixture)));

        AssertRemovingBlocks(fixture,
            "decision.parent_order",
            "exposure.parent_quantity",
            "execution.slice_route",
            "lifecycle.schedule_control",
            "finish.residual_policy");
    }

    [Fact]
    public void Signal_only_legitimately_marks_exposure_execution_and_unwind_not_applicable()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.SignalOnly, SignalOnlySpec());

        var result = Confirm(fixture);

        result.Success.Should().BeTrue(Describe(result));
        fixture.Draft.Requirements.Where(requirement => requirement.RequirementId is
                "exposure.not_applicable" or "execution.not_applicable" or
                "lifecycle.fill_handling_not_applicable" or "finish.not_applicable")
            .Should().HaveCount(4).And.OnlyContain(requirement =>
                (requirement.RequirementId == "exposure.not_applicable" ||
                 requirement.RequirementId == "execution.not_applicable" ||
                 requirement.RequirementId == "lifecycle.fill_handling_not_applicable" ||
                 requirement.RequirementId == "finish.not_applicable") &&
                requirement.Disposition == StrategySemanticDispositionV1.NotApplicable &&
                requirement.Value == null &&
                !string.IsNullOrWhiteSpace(requirement.DispositionRationale));
        fixture.Draft.Requirements.Single(x => x.RequirementId == "decision.signal_publication")
            .Value!.CanonicalValue.Should().ContainAll("confidence", "expires");
    }

    [Fact]
    public void Specialized_classifications_cannot_be_weakened_to_position_target()
    {
        var pair = PairSpec();
        var multiLeg = PairSpec() with
        {
            Id = "spec.multi-leg",
            Name = "spec.multi-leg",
            Context = PairSpec().Context with
            {
                Topology = MarketTopologyKind.MultiLeg,
                Exposure = ExposureGeometryKind.Arbitrage,
            },
        };
        var cases = new[]
        {
            (Classification: pair, Code: "INTENT_CLASSIFICATION_REQUIRES_MULTI_LEG_TARGET"),
            (Classification: multiLeg, Code: "INTENT_CLASSIFICATION_REQUIRES_MULTI_LEG_TARGET"),
            (Classification: OptionsSpec(), Code: "INTENT_CLASSIFICATION_REQUIRES_MULTI_LEG_TARGET"),
            (Classification: PortfolioSpec(), Code: "INTENT_CLASSIFICATION_REQUIRES_PORTFOLIO_TARGET"),
            (Classification: QuoteSpec(), Code: "INTENT_CLASSIFICATION_REQUIRES_QUOTE_SET"),
            (Classification: ExecutionSpec(), Code: "INTENT_CLASSIFICATION_REQUIRES_EXECUTION_SCHEDULE"),
            (Classification: SignalOnlySpec(), Code: "INTENT_CLASSIFICATION_REQUIRES_SIGNAL_ONLY"),
        };

        foreach (var testCase in cases)
        {
            var result = Confirm(CompleteFixture(StrategyIntentKindV1.PositionTarget, testCase.Classification));

            result.Success.Should().BeFalse($"{testCase.Classification.Id} has a specialized decision topology");
            result.Issues.Should().Contain(issue =>
                issue.Code == testCase.Code && issue.Path == "draft.intentModel.kind");
        }
    }

    [Fact]
    public void Specialized_and_general_classifications_accept_their_compatible_intent_kinds()
    {
        var compatible = new[]
        {
            CompleteFixture(StrategyIntentKindV1.MultiLegTarget, PairSpec()),
            CompleteFixture(StrategyIntentKindV1.MultiLegTarget, MultiLegSpec()),
            CompleteFixture(StrategyIntentKindV1.PortfolioTarget, BasketSpec()),
            CompleteFixture(StrategyIntentKindV1.MultiLegTarget, MultiVenueSpec()),
            CompleteFixture(StrategyIntentKindV1.MultiLegTarget, OptionsSpec()),
            CompleteFixture(StrategyIntentKindV1.PortfolioTarget, PortfolioSpec()),
            CompleteFixture(StrategyIntentKindV1.QuoteSet, QuoteSpec()),
            CompleteFixture(StrategyIntentKindV1.ExecutionSchedule, ExecutionSpec()),
            CompleteFixture(StrategyIntentKindV1.SignalOnly, SignalOnlySpec()),
            CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec()),
            CompleteFixture(StrategyIntentKindV1.PositionTarget, HedgingSpec()),
        };

        foreach (var fixture in compatible)
        {
            var result = Confirm(fixture);

            result.Success.Should().BeTrue(Describe(result));
        }
    }

    [Fact]
    public void Multi_leg_basket_and_multi_venue_topologies_have_positive_confirmation_sentinels()
    {
        var fixtures = new[]
        {
            CompleteFixture(StrategyIntentKindV1.MultiLegTarget, MultiLegSpec()),
            CompleteFixture(StrategyIntentKindV1.PortfolioTarget, BasketSpec()),
            CompleteFixture(StrategyIntentKindV1.MultiLegTarget, MultiVenueSpec()),
        };

        foreach (var fixture in fixtures)
        {
            var result = Confirm(fixture);

            result.Success.Should().BeTrue(Describe(result));
            result.Intent!.Requirements.Any(requirement =>
                    string.Equals(requirement.RequirementId, "decision.coordinated_legs", StringComparison.Ordinal) ||
                    string.Equals(requirement.RequirementId, "decision.portfolio_targets", StringComparison.Ordinal))
                .Should().BeTrue();
        }
    }

    [Fact]
    public void Specialized_objectives_cannot_hide_behind_signal_only_axes()
    {
        var signalFixture = CompleteFixture(StrategyIntentKindV1.SignalOnly, SignalOnlySpec());
        var notApplicablePortfolio = new StrategyPortfolioSpec(PortfolioConstructionKind.NotApplicable);
        var notApplicableRisk = new StrategyRiskSpec([StrategyRiskExitKind.NotApplicable]);
        var notApplicableExecution = new StrategyExecutionSpec([StrategyExecutionPolicyKind.NotApplicable]);
        var cases = new[]
        {
            (Classification: QuoteSpec() with
            {
                Portfolio = notApplicablePortfolio,
                Risk = notApplicableRisk,
                Execution = notApplicableExecution,
            }, Code: "INTENT_CLASSIFICATION_REQUIRES_QUOTE_SET"),
            (Classification: ExecutionSpec() with
            {
                Portfolio = notApplicablePortfolio,
                Risk = notApplicableRisk,
                Execution = notApplicableExecution,
            }, Code: "INTENT_CLASSIFICATION_REQUIRES_EXECUTION_SCHEDULE"),
        };

        foreach (var testCase in cases)
        {
            var classificationBinding = new StrategyClassificationBindingV1(
                testCase.Classification.Id,
                StrategySpecCanonicalJsonV1.Hash(testCase.Classification));
            var fixture = signalFixture with
            {
                Classification = testCase.Classification,
                Draft = signalFixture.Draft with { Classification = classificationBinding },
            };

            var result = Confirm(fixture);

            result.Success.Should().BeFalse();
            result.Issues.Should().Contain(issue => issue.Code == testCase.Code);
        }
    }

    [Fact]
    public void Inactive_builtin_requirements_cannot_contradict_the_selected_topology()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var contradictoryRequirement = CompleteFixture(
                StrategyIntentKindV1.SignalOnly,
                SignalOnlySpec())
            .Draft.Requirements.Single(requirement => requirement.RequirementId == "execution.not_applicable");
        var draft = fixture.Draft with
        {
            Requirements = [.. fixture.Draft.Requirements, contradictoryRequirement],
        };

        var result = Confirm(fixture with { Draft = draft });

        result.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_REQUIREMENT_INACTIVE_FOR_TOPOLOGY" &&
            issue.Path.EndsWith(".requirementId", StringComparison.Ordinal));
    }

    [Fact]
    public void Material_research_choice_requires_an_auditable_canonical_resolution()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var unresolvedResearch = fixture.ResearchCase with
        {
            UnresolvedItems =
            [
                new ResearchUnresolvedItemV1(
                    "comparison-staleness",
                    "Decide whether stale comparison data forces no trade.",
                    true,
                    ["statement.confirmed"]),
            ],
        };
        var unresolvedDraft = fixture.Draft with
        {
            ResearchCaseHashSha256 = ResearchCaseCanonicalJsonV1.Hash(unresolvedResearch),
        };

        var unresolvedResult = Confirm(fixture with
        {
            ResearchCase = unresolvedResearch,
            Draft = unresolvedDraft,
        });

        unresolvedResult.Success.Should().BeFalse();
        unresolvedResult.Issues.Should().Contain(issue =>
            issue.Code == "RESEARCH_MATERIAL_ITEM_UNRESOLVED");

        var resolvedItem = new ResearchResolvedItemV1(
            "comparison-staleness",
            "Decide whether stale comparison data forces no trade.",
            "Comparison data older than one completed bar forces no trade.",
            true,
            ["statement.confirmed"],
            "Reviewed and recorded by the user in Strategy request review.");
        var resolvedResearch = unresolvedResearch with
        {
            UnresolvedItems = [],
            ResolvedItems = [resolvedItem],
        };
        var resolvedDraft = fixture.Draft with
        {
            ResearchCaseHashSha256 = ResearchCaseCanonicalJsonV1.Hash(resolvedResearch),
        };

        var resolvedResult = Confirm(fixture with
        {
            ResearchCase = resolvedResearch,
            Draft = resolvedDraft,
        });
        var canonicalJson = ResearchCaseCanonicalJsonV1.Serialize(resolvedResearch);

        resolvedResult.Success.Should().BeTrue(Describe(resolvedResult));
        canonicalJson.Should().Contain("\"resolvedItems\"");
        ResearchCaseCanonicalJsonV1.Deserialize(canonicalJson).ResolvedItems
            .Should().ContainSingle().Which.Should().BeEquivalentTo(resolvedItem);

        var blankResolution = resolvedResearch with
        {
            ResolvedItems = [resolvedItem with { Resolution = " " }],
        };
        var blankDraft = fixture.Draft with
        {
            ResearchCaseHashSha256 = ResearchCaseCanonicalJsonV1.Hash(blankResolution),
        };
        var blankResult = Confirm(fixture with
        {
            ResearchCase = blankResolution,
            Draft = blankDraft,
        });

        blankResult.Success.Should().BeFalse();
        blankResult.Issues.Should().Contain(issue => issue.Code == "RESEARCH_RESOLUTION_REQUIRED");

        var blankProvenance = resolvedResearch with
        {
            ResolvedItems = [resolvedItem with { ResolutionProvenance = " " }],
        };
        var blankProvenanceDraft = fixture.Draft with
        {
            ResearchCaseHashSha256 = ResearchCaseCanonicalJsonV1.Hash(blankProvenance),
        };
        var blankProvenanceResult = Confirm(fixture with
        {
            ResearchCase = blankProvenance,
            Draft = blankProvenanceDraft,
        });

        blankProvenanceResult.Success.Should().BeFalse();
        blankProvenanceResult.Issues.Should().Contain(issue =>
            issue.Code == "RESEARCH_RESOLUTION_PROVENANCE_REQUIRED");
        ResearchCaseCanonicalJsonV1.Serialize(fixture.ResearchCase)
            .Should().NotContain("\"resolvedItems\"",
                "legacy research-case/v1 bytes omit the optional additive collection");
    }

    [Fact]
    public void Governed_extension_confirms_only_when_its_versioned_id_has_a_host_owner()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.Extension, DirectionalSpec());
        fixture = fixture with
        {
            Draft = fixture.Draft with
            {
                IntentModel = new StrategyIntentModelV1(
                    StrategyIntentKindV1.Extension,
                    "example.custom-intent@1"),
            },
        };

        var unowned = Confirm(fixture);
        var owned = Confirm(
            fixture,
            new StubExtensionRegistry(
                (StrategyIntentExtensionRoleV1.IntentModel, "example.custom-intent@1")));
        var wrongRole = Confirm(
            fixture,
            new StubExtensionRegistry(
                (StrategyIntentExtensionRoleV1.ValueType, "example.custom-intent@1")));
        var malformed = Confirm(fixture with
        {
            Draft = fixture.Draft with
            {
                IntentModel = new StrategyIntentModelV1(
                    StrategyIntentKindV1.Extension,
                    "not-versioned"),
            },
        }, new StubExtensionRegistry(
            (StrategyIntentExtensionRoleV1.IntentModel, "not-versioned")));

        unowned.Success.Should().BeFalse("an extension without an installed host owner is unsupported");
        unowned.Issues.Should().Contain(issue => issue.Code == "INTENT_EXTENSION_UNSUPPORTED");
        unowned.Issues.Should().NotContain(issue =>
            issue.Code.StartsWith("INTENT_CLASSIFICATION_REQUIRES_", StringComparison.Ordinal));
        owned.Success.Should().BeTrue(Describe(owned));
        owned.Intent!.IntentModel.ExtensionId.Should().Be("example.custom-intent@1");
        wrongRole.Success.Should().BeFalse("value-schema ownership cannot authorize an intent model");
        wrongRole.Issues.Should().Contain(issue => issue.Code == "INTENT_EXTENSION_UNSUPPORTED");
        malformed.Success.Should().BeFalse();
        malformed.Issues.Should().Contain(issue => issue.Code == "INTENT_EXTENSION_ID_INVALID");
    }

    [Fact]
    public void Custom_requirement_and_value_schemas_require_exact_registry_ownership()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var custom = Requirement(
            "example.custom-requirement@1",
            StrategySemanticStageV1.QualifyEvidence,
            "Use the reviewed custom evidence clause.") with
        {
            Value = new StrategyCandidateValueV1(
                "example.custom-value@1",
                "Use the reviewed custom evidence clause."),
        };
        fixture = fixture with
        {
            Draft = fixture.Draft with
            {
                Requirements = [.. fixture.Draft.Requirements, custom],
            },
        };

        var unowned = Confirm(fixture);
        var owned = Confirm(
            fixture,
            new StubExtensionRegistry(
                (StrategyIntentExtensionRoleV1.Requirement, "example.custom-requirement@1"),
                (StrategyIntentExtensionRoleV1.ValueType, "example.custom-value@1")));
        var swappedRoles = Confirm(
            fixture,
            new StubExtensionRegistry(
                (StrategyIntentExtensionRoleV1.ValueType, "example.custom-requirement@1"),
                (StrategyIntentExtensionRoleV1.Requirement, "example.custom-value@1")));

        unowned.Success.Should().BeFalse();
        unowned.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_REQUIREMENT_EXTENSION_UNSUPPORTED");
        unowned.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_VALUE_TYPE_EXTENSION_UNSUPPORTED");
        owned.Success.Should().BeTrue(Describe(owned));
        swappedRoles.Success.Should().BeFalse("ownership is exact for each schema role");
        swappedRoles.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_REQUIREMENT_EXTENSION_UNSUPPORTED");
        swappedRoles.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_VALUE_TYPE_EXTENSION_UNSUPPORTED");
    }

    [Fact]
    public void Hedging_and_options_add_their_family_specific_questions()
    {
        var hedge = CompleteFixture(StrategyIntentKindV1.PositionTarget, HedgingSpec());
        var options = CompleteFixture(StrategyIntentKindV1.MultiLegTarget, OptionsSpec());

        Confirm(hedge).Success.Should().BeTrue(Describe(Confirm(hedge)));
        Confirm(options).Success.Should().BeTrue(Describe(Confirm(options)));

        AssertRemovingBlocks(hedge, "exposure.hedge_target", "lifecycle.hedge_rebalance");
        AssertRemovingBlocks(options,
            "decision.option_structure",
            "exposure.greeks",
            "finish.option_lifecycle");
    }

    [Fact]
    public void Semantic_mutation_and_stale_review_hash_invalidate_confirmation()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var confirmed = Confirm(fixture).Intent!;
        var mutatedRequirements = confirmed.Requirements.Select((requirement, index) => index == 0
                ? requirement with
                {
                    Value = requirement.Value! with
                    {
                        CanonicalValue = requirement.Value.CanonicalValue + " Changed after review.",
                    },
                }
                : requirement)
            .ToArray();
        var mutated = confirmed with { Requirements = mutatedRequirements };

        var persistedIssues = StrategyIntentConfirmationV1.ValidateConfirmed(
            mutated, fixture.Candidate, fixture.ResearchCase, fixture.Classification);
        var staleDraft = fixture.Draft with
        {
            Requirements = fixture.Draft.Requirements.Select((requirement, index) => index == 0
                    ? requirement with { Description = requirement.Description + " Changed after review." }
                    : requirement)
                .ToArray(),
        };
        var staleResult = StrategyIntentConfirmationV1.Confirm(
            fixture.Candidate,
            fixture.ResearchCase,
            fixture.Classification,
            staleDraft,
            StrategyIntentCanonicalJsonV1.Hash(fixture.Draft));
        var brokenProvenanceDraft = fixture.Draft with
        {
            Requirements = fixture.Draft.Requirements.Select((requirement, index) => index == 0
                    ? requirement with
                    {
                        Provenance = requirement.Provenance with
                        {
                            CandidateStatementIds = ["statement.does-not-exist"],
                        },
                    }
                    : requirement)
                .ToArray(),
        };
        var brokenProvenanceResult = Confirm(fixture with { Draft = brokenProvenanceDraft });

        persistedIssues.Should().Contain(issue => issue.Code == "INTENT_DRAFT_HASH_MISMATCH");
        staleResult.Success.Should().BeFalse();
        staleResult.Issues.Should().Contain(issue => issue.Code == "INTENT_DRAFT_HASH_MISMATCH");
        brokenProvenanceResult.Success.Should().BeFalse();
        brokenProvenanceResult.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_STATEMENT_REFERENCE_UNKNOWN" &&
            issue.Message.Contains("statement.does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Blank_requirement_id_reports_an_issue_instead_of_throwing()
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var malformed = fixture.Draft with
        {
            Requirements =
            [
                .. fixture.Draft.Requirements,
                Requirement(" ", StrategySemanticStageV1.ObserveOrTrigger,
                    "This malformed row intentionally has no identifier."),
            ],
        };
        StrategyIntentConfirmationResultV1? result = null;

        var action = () => result = Confirm(fixture with { Draft = malformed });

        action.Should().NotThrow();
        result!.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "INTENT_REQUIREMENT_ID_REQUIRED");
    }

    [Fact]
    public void Requirement_disposition_payload_is_an_exclusive_union()
    {
        var directional = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var applicableWithRationale = directional.Draft with
        {
            Requirements = directional.Draft.Requirements.Select((requirement, index) => index == 0
                    ? requirement with { DispositionRationale = "This contradicts the applicable value." }
                    : requirement)
                .ToArray(),
        };
        var signalOnly = CompleteFixture(StrategyIntentKindV1.SignalOnly, SignalOnlySpec());
        var notApplicableWithValue = signalOnly.Draft with
        {
            Requirements = signalOnly.Draft.Requirements.Select(requirement =>
                    requirement.RequirementId == "execution.not_applicable"
                        ? requirement with
                        {
                            Value = new StrategyCandidateValueV1(
                                "core.semantic_clause@1",
                                "Submit a market order despite being not applicable."),
                        }
                        : requirement)
                .ToArray(),
        };

        var applicableResult = Confirm(directional with { Draft = applicableWithRationale });
        var notApplicableResult = Confirm(signalOnly with { Draft = notApplicableWithValue });

        applicableResult.Success.Should().BeFalse();
        applicableResult.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_APPLICABLE_RATIONALE_UNEXPECTED" &&
            issue.Path.EndsWith(".dispositionRationale", StringComparison.Ordinal));
        notApplicableResult.Success.Should().BeFalse();
        notApplicableResult.Issues.Should().Contain(issue =>
            issue.Code == "INTENT_DISPOSITION_VALUE_UNEXPECTED" &&
            issue.Path.EndsWith(".value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("signal")]
    [InlineData("portfolio")]
    [InlineData("risk")]
    [InlineData("execution")]
    [InlineData("state")]
    public void Malformed_nested_classification_returns_issues_instead_of_entering_templates(string missingMember)
    {
        var fixture = CompleteFixture(StrategyIntentKindV1.PositionTarget, DirectionalSpec());
        var malformedClassification = missingMember switch
        {
            "context" => fixture.Classification with { Context = null! },
            "signal" => fixture.Classification with { Signal = null! },
            "portfolio" => fixture.Classification with { Portfolio = null! },
            "risk" => fixture.Classification with { Risk = null! },
            "execution" => fixture.Classification with { Execution = null! },
            "state" => fixture.Classification with { State = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(missingMember), missingMember, null),
        };
        var malformedDraft = fixture.Draft with
        {
            Classification = new StrategyClassificationBindingV1(
                malformedClassification.Id,
                StrategySpecCanonicalJsonV1.Hash(malformedClassification)),
        };
        StrategyIntentConfirmationResultV1? result = null;

        var action = () => result = Confirm(fixture with
        {
            Classification = malformedClassification,
            Draft = malformedDraft,
        });

        action.Should().NotThrow();
        result!.Success.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "INTENT_CLASSIFICATION_INVALID");
    }

    private static void AssertRemovingBlocks(Fixture fixture, params string[] requirementIds)
    {
        fixture.Draft.Requirements.Select(x => x.RequirementId).Should().Contain(requirementIds);
        foreach (var requirementId in requirementIds)
        {
            var incomplete = fixture.Draft with
            {
                Requirements = fixture.Draft.Requirements
                    .Where(requirement => requirement.RequirementId != requirementId)
                    .ToArray(),
            };
            var result = Confirm(fixture with { Draft = incomplete });

            result.Success.Should().BeFalse($"{requirementId} is required for this strategy family");
            result.Questions.Should().Contain(question => question.RequirementId == requirementId);
        }
    }

    private static StrategyIntentConfirmationResultV1 Confirm(
        Fixture fixture,
        IStrategyIntentExtensionRegistryV1? extensionRegistry = null) =>
        StrategyIntentConfirmationV1.Confirm(
            fixture.Candidate,
            fixture.ResearchCase,
            fixture.Classification,
            fixture.Draft,
            StrategyIntentCanonicalJsonV1.Hash(fixture.Draft),
            extensionRegistry);

    private static Fixture CompleteFixture(StrategyIntentKindV1 kind, StrategySpec classification)
    {
        var candidate = ConfirmedCandidate();
        var researchCase = ResearchCase(candidate);
        var emptyDraft = new StrategyIntentDraftV1(
            StrategyIntentDraftV1.CurrentSchemaVersion,
            $"intent/{classification.Id}",
            candidate.CandidateId,
            candidate.Revision,
            StrategyCandidateCanonicalJsonV1.Hash(candidate),
            ResearchCaseCanonicalJsonV1.Hash(researchCase),
            new StrategyClassificationBindingV1(
                classification.Id,
                StrategySpecCanonicalJsonV1.Hash(classification)),
            new StrategyIntentModelV1(kind),
            StrategyIntentCompletenessV1.CatalogVersion,
            []);
        var requirements = StrategyIntentCompletenessV1.Questions(emptyDraft, classification)
            .Select(question => question.RequirementId is
                    "exposure.not_applicable" or "execution.not_applicable" or
                    "lifecycle.fill_handling_not_applicable" or "finish.not_applicable"
                ? NotApplicableRequirement(question)
                : Requirement(question.RequirementId, question.Stage, Answer(question.RequirementId)))
            .ToArray();

        return new Fixture(candidate, researchCase, classification, emptyDraft with { Requirements = requirements });
    }

    private static StrategySemanticRequirementV1 Requirement(
        string requirementId,
        StrategySemanticStageV1 stage,
        string answer) =>
        new(
            requirementId,
            stage,
            StrategySemanticDispositionV1.Applicable,
            answer,
            IsMaterial: true,
            Provenance(),
            new StrategyCandidateValueV1("core.semantic_clause@1", answer));

    private static StrategySemanticRequirementV1 NotApplicableRequirement(StrategyIntentQuestionV1 question) =>
        new(
            question.RequirementId,
            question.Stage,
            StrategySemanticDispositionV1.NotApplicable,
            Answer(question.RequirementId),
            IsMaterial: true,
            Provenance(),
            Value: null,
            DispositionRationale: "The confirmed strategy publishes signals and owns no exposure, orders, or positions.");

    private static StrategyRequirementProvenanceV1 Provenance() =>
        new(["statement.confirmed"], ["evidence.point_in_time"],
            "The user explicitly confirmed this plain-language decision during strategy review.");

    private static string Answer(string requirementId) => requirementId switch
    {
        "data.primary_instrument" => "Evaluate AAPL as the primary traded instrument and use SPY and QQQ only as comparison inputs.",
        "data.timeframe" => "Consume completed five-minute OHLCV bars and make decisions once per completed five-minute bar.",
        "observe.trigger" => "Evaluate after each completed five-minute bar during the regular session.",
        "observe.suspected_move" => "Activate when AAPL moves at least 1.5% within three completed five-minute bars.",
        "evidence.qualification" => "Qualify only with completed causal bars; otherwise emit no action.",
        "evidence.primary_threshold" => "Require a 1.5% move, volume at least 1.8 times its 20-bar median, and RSI above 60 for an upward move or below 40 for a downward move.",
        "evidence.point_in_time" => "Use only completed bars aligned to the decision timestamp; comparison data older than one bar is stale.",
        "evidence.data_freshness" => "AAPL, SPY, QQQ, volume, and indicator inputs must share the decision timestamp and be no older than one five-minute bar.",
        "evidence.comparison_inputs" => "Require timestamp-aligned SPY and QQQ completed five-minute returns in addition to AAPL price, volume, and RSI.",
        "evidence.cross_instrument" => "Confirm direction when both SPY and QQQ five-minute returns have the same sign and exceed 0.3% at the same completed timestamp.",
        "evidence.conflict_no_trade" => "Emit no trade when SPY and QQQ disagree, volume confirmation fails, or any required input is missing or stale.",
        "decision.intent" => "Emit the family-specific target, or an explicit no-action decision when qualification fails.",
        "decision.direction_set" => "Target long after confirmed upward evidence, short after confirmed downward evidence, no trade on conflict, exit on invalidation, and reverse only after the opposite signal is freshly confirmed.",
        "decision.long_condition" => "Target long only after AAPL rises 1.5%, volume is at least 1.8 times median, RSI is above 60, and both SPY and QQQ confirm the upward direction.",
        "decision.short_condition" => "Target short only after AAPL falls 1.5%, volume is at least 1.8 times median, RSI is below 40, and both SPY and QQQ confirm the downward direction.",
        "decision.no_trade_condition" => "Emit no trade when evidence conflicts, any threshold fails, or any AAPL, SPY, QQQ, volume, or RSI input is missing or stale.",
        "decision.reversal_condition" => "Reverse only after closing the current filled position and receiving a fresh fully confirmed opposite signal on a later completed bar.",
        "decision.position_actions" => "Remain flat on no trade, reduce only at a profit target, exit on invalidation or risk controls, and reverse only after the existing position is closed.",
        "decision.validity_window" => "The decision is valid for one completed five-minute bar and must then be recomputed.",
        "decision.coordinated_legs" => "Open long AAPL and short MSFT together; never open or keep only one leg after the coordination timeout.",
        "decision.portfolio_targets" => "At each weekly rebalance, produce complete weights for all eligible assets plus cash, summing to 100%.",
        "decision.two_sided_quotes" => "Quote one bid and one ask around fair value with a two-tick base half-spread.",
        "decision.parent_order" => "Execute the confirmed 100,000-share buy parent order without exceeding its limit price or end time.",
        "decision.signal_publication" => "Publish direction, score, and confidence to the research topic; the signal expires after fifteen minutes and is withdrawn when its inputs become stale.",
        "decision.option_structure" => "Trade one 30-day 100/105 call spread as a complete two-leg structure with both contracts and strikes fixed before activation.",
        "exposure.position_target" => "Risk 0.50% of equity per thesis, size from stop distance, cap the position at 10% of equity, and never exceed 1.0 times gross leverage.",
        "exposure.sizing_formula" => "Set total shares to floor(0.50% of marked account equity divided by per-share stop distance), then apply the confirmed position and leverage caps.",
        "exposure.maximum_position" => "Cap absolute position notional at 10% of marked account equity and never exceed the instrument's approved share limit.",
        "exposure.leverage_limit" => "Keep portfolio gross leverage at or below 1.0 times equity and net leverage between -0.5 and +0.5 times equity.",
        "exposure.risk_budget" => "The total thesis risk budget is 0.50% of current marked equity, recomputed before each new tranche.",
        "exposure.early_tranche" => "The first confirmed signal may enter 40% of the final target.",
        "exposure.later_tranches" => "A second confirmation after one completed bar may add the remaining 60%, never exceeding the target.",
        "exposure.leg_targets" => "Target equal dollar exposure in AAPL and MSFT, beta-adjusted daily, with a 2% maximum spread position.",
        "exposure.portfolio_constraints" => "Cap each asset at 8%, sector exposure at 25%, gross exposure at 100%, and one-way rebalance turnover at 15%.",
        "exposure.turnover_limit" => "Reject target changes above 15% one-way turnover, include ten basis points of estimated transaction cost, and cap each order by its confirmed capacity limit.",
        "exposure.inventory_limits" => "Target zero inventory, skew quotes after 200 shares, and stop adding exposure at a hard limit of 500 shares.",
        "exposure.parent_quantity" => "Track filled quantity against the 100,000-share parent and schedule only the exact remaining quantity.",
        "exposure.not_applicable" => "Signal publication does not own sizing or exposure.",
        "exposure.hedge_target" => "Rebalance the ES hedge to keep portfolio beta between -0.05 and +0.05, capped at the portfolio's full delta exposure.",
        "exposure.greeks" => "Keep net delta within 0.10 and total short gamma within the confirmed portfolio cap.",
        "exposure.extension" => "The installed extension owns a bounded target exposure encoded by its versioned schema.",
        "execution.order_policy" => "Convert only the delta between fill-derived current exposure and the confirmed target into order intent.",
        "execution.order_type_selection" => "Use a protected market order only during high-liquidity confirmation; otherwise use the confirmed limit-price formula.",
        "execution.market_policy" => "Market orders require spread below five basis points and a two-tick slippage protection bound.",
        "execution.limit_policy" => "Price a buy limit at best bid plus one tick and a sell limit at best ask minus one tick; cancel and replace once after two minutes, then stop.",
        "execution.time_in_force" => "Use day time-in-force, expire each staged order after two minutes, and cancel any residual at session end.",
        "execution.partial_fill_policy" => "Recompute residual quantity from confirmed fills and never resubmit already filled quantity.",
        "execution.leg_coordination" => "Submit both legs as one coordinated intent; cancel the other leg and flatten any fill if both legs are not filled within five seconds.",
        "execution.rebalance_policy" => "Trade only the delta from current fill-derived holdings to target weights, respecting the 15% turnover cap.",
        "execution.cancel_replace" => "Cancel stale quotes before submitting replacements whenever fair value moves one tick or a quote is two seconds old.",
        "execution.slice_route" => "Slice the remaining parent quantity every minute using 10% participation, passive limits first, and the approved smart router.",
        "execution.not_applicable" => "Signal publication does not choose order type, price, route, or time-in-force.",
        "lifecycle.fill_handling_not_applicable" => "Signal publication does not own orders, fills, partial-fill state, or residual quantity.",
        "execution.extension" => "The installed extension translates only its reviewed target into bounded order intent.",
        "lifecycle.position" => "Enter in at most two tranches, resize only from fresh targets, and manage exposure from confirmed fills.",
        "lifecycle.fill_driven_state" => "Position and remaining quantity update only from fill events, including partial fills and cancellations.",
        "lifecycle.scale_policy" => "Allow one scale-in after renewed confirmation and one scale-out at the first target; never duplicate the same tranche.",
        "lifecycle.leg_rebalance" => "Recompute the hedge ratio daily and rebalance both legs together when beta-adjusted imbalance exceeds 5%.",
        "lifecycle.rebalance_state" => "Rebalance weekly after Friday's close and again only when a hard portfolio constraint is breached.",
        "lifecycle.quote_inventory" => "After each fill, update inventory and skew the next bid and ask away from further inventory accumulation.",
        "lifecycle.quote_refresh" => "Refresh both quotes after a one-tick fair-value move, two seconds of quote age, a spread-state change, or any inventory fill.",
        "lifecycle.schedule_control" => "Pause when spread or volatility exceeds its bound, resume after three healthy observations, and recalculate remaining slices.",
        "lifecycle.publication" => "Update the published signal on each completed bar, expire it after fifteen minutes, and withdraw it immediately when evidence becomes stale.",
        "lifecycle.hedge_rebalance" => "Add, resize, or remove the hedge whenever beta leaves the confirmed band, using fill-derived hedge exposure.",
        "lifecycle.extension" => "The installed extension updates state only from its reviewed causal events and confirmed fills.",
        "finish.position" => "Exit on stop, target, time limit, thesis invalidation, or confirmed reversal; a reversal closes before opening the opposite target.",
        "finish.stop" => "Exit when marked loss reaches 1.0 initial risk unit or the completed bar closes through the invalidation level.",
        "finish.target" => "Reduce half at 1.5 risk units and exit the remainder at 3.0 risk units.",
        "finish.trailing_stop" => "After price reaches 1.0 risk unit of profit, trail the remaining position by 0.75 risk units using completed-bar extremes and exit when that trail is crossed.",
        "finish.time_exit" => "Exit any remaining position after twelve completed bars or five minutes before the session close.",
        "finish.reversal" => "Cancel working orders, close the current filled position, then open the opposite target only after fresh opposite confirmation.",
        "finish.invalidation" => "Invalidate the thesis when the comparison indices reverse or confirmed volume falls below its threshold.",
        "finish.session_boundary" => "Cancel working orders and flatten remaining directional exposure five minutes before the regular-session close.",
        "finish.multi_leg_unwind" => "Unwind both legs together; if one leg fails, immediately hedge or flatten the filled leg under the coordination timeout.",
        "finish.portfolio_deactivation" => "Move all targets to cash when data admission fails or the allocation mandate is disabled.",
        "finish.cancel_all_quotes" => "Cancel every quote on stale data, disconnect, risk halt, or session close, then neutralize inventory under the approved handoff policy.",
        "finish.residual_policy" => "At the deadline, cancel outstanding slices and report the exact residual; do not cross the market without separate approval.",
        "finish.not_applicable" => "Signal publication owns expiry and withdrawal, not position unwind.",
        "finish.option_lifecycle" => "Close both legs together seven days before expiry; do not hold through assignment or exercise without explicit review.",
        "finish.extension" => "The installed extension cancels work and unwinds according to its reviewed versioned completion policy.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(requirementId),
            requirementId,
            "Every generated strategy question in this sentinel requires a concrete human-readable answer."),
    };

    private static StrategyCandidateV1 ConfirmedCandidate() => new(
        StrategyCandidateV1.CurrentSchemaVersion,
        "candidate/research-sentinel",
        1,
        null,
        "Use causal market evidence to implement the selected strategy family.",
        "Confirmed research sentinel",
        StrategyCandidateStatusV1.Confirmed,
        new StrategyCandidateInterpretationV1(
            "The user confirmed the data, decisions, exposure, execution, lifecycle, and completion behavior.",
            StrategyInterpretationConfidenceV1.High,
            []),
        [
            new StrategyCandidateGroupV1(
                "group.confirmed",
                StrategyCandidateGroupKindV1.Custom,
                "Confirmed decisions",
                "Concrete decisions reviewed with the user.",
                [
                    new StrategyCandidateStatementV1(
                        "statement.confirmed",
                        StrategyCandidateStatementKindV1.Rule,
                        "Use the concrete reviewed answers in the confirmed strategy intent.",
                        StrategyCandidateStatementSourceV1.User,
                        StrategyCandidateStatementStateV1.Confirmed,
                        IsMaterial: true,
                        new StrategyCandidateValueV1(
                            "core.semantic_clause@1",
                            "The user confirmed every concrete strategy decision shown in the review.")),
                ],
                []),
        ],
        []);

    private static ResearchCaseV1 ResearchCase(StrategyCandidateV1 candidate) => new(
        ResearchCaseV1.CurrentSchemaVersion,
        "research/sentinel",
        candidate.CandidateId,
        StrategyCandidateCanonicalJsonV1.Hash(candidate),
        "Determine whether the confirmed strategy deserves a controlled experiment.",
        "The confirmed causal evidence may produce the requested intent without using future data.",
        [
            new ResearchEvidenceRequirementV1(
                "evidence.point_in_time",
                "Use only information available at each decision timestamp.",
                "Completed and timestamp-aligned observations only.",
                "Reject missing, stale, or future information.",
                IsMaterial: true,
                ["statement.confirmed"]),
        ],
        [
            new ResearchFalsifierV1(
                "falsifier.no_causal_effect",
                "Reject the hypothesis when causal out-of-sample evidence does not support it.",
                IsMaterial: true,
                ["statement.confirmed"]),
        ],
        []);

    private static StrategySpec DirectionalSpec() => Spec(
        "spec.directional-confirmation",
        StrategyObjectiveKind.ReturnSeeking,
        MarketTopologyKind.CrossAsset,
        ExposureGeometryKind.DirectionalLongShort,
        [AssetClass.Equity],
        [StrategyInformationKind.Bar, StrategyInformationKind.Trade],
        [ReturnHypothesisKind.Momentum, ReturnHypothesisKind.CatalystInformation],
        [StrategyTriggerKind.Bar],
        [SignalModelKind.DeterministicRule],
        PortfolioConstructionKind.RiskBudget,
        [StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TakeProfit, StrategyRiskExitKind.TrailingStop,
            StrategyRiskExitKind.TimeExit, StrategyRiskExitKind.SignalReversal, StrategyRiskExitKind.ExposureCap],
        [StrategyExecutionPolicyKind.Market, StrategyExecutionPolicyKind.Limit],
        [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState]);

    private static StrategySpec PairSpec() => Spec(
        "spec.pair",
        StrategyObjectiveKind.ReturnSeeking,
        MarketTopologyKind.Pair,
        ExposureGeometryKind.Spread,
        [AssetClass.Equity],
        [StrategyInformationKind.Bar, StrategyInformationKind.Trade],
        [ReturnHypothesisKind.Convergence],
        [StrategyTriggerKind.Bar],
        [SignalModelKind.Statistical],
        PortfolioConstructionKind.ExposureNeutral,
        [StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TimeExit, StrategyRiskExitKind.ExposureCap],
        [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.CoordinatedLegs],
        [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState]);

    private static StrategySpec MultiLegSpec() => Spec(
        "spec.multi-leg",
        StrategyObjectiveKind.ReturnSeeking,
        MarketTopologyKind.MultiLeg,
        ExposureGeometryKind.Spread,
        [AssetClass.Equity],
        [StrategyInformationKind.Bar, StrategyInformationKind.Trade],
        [ReturnHypothesisKind.Convergence],
        [StrategyTriggerKind.Bar],
        [SignalModelKind.Statistical],
        PortfolioConstructionKind.ExposureNeutral,
        [StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TimeExit, StrategyRiskExitKind.ExposureCap],
        [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.CoordinatedLegs],
        [StrategyStateKind.PositionAware, StrategyStateKind.FiniteState]);

    private static StrategySpec BasketSpec() => Spec(
        "spec.basket",
        StrategyObjectiveKind.Allocation,
        MarketTopologyKind.Basket,
        ExposureGeometryKind.CrossSectionalLongShort,
        [AssetClass.Equity],
        [StrategyInformationKind.Bar, StrategyInformationKind.Fundamental],
        [ReturnHypothesisKind.Value, ReturnHypothesisKind.Quality],
        [StrategyTriggerKind.Schedule],
        [SignalModelKind.Ranking, SignalModelKind.Optimization],
        PortfolioConstructionKind.Optimized,
        [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
        [StrategyExecutionPolicyKind.Market],
        [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware]);

    private static StrategySpec MultiVenueSpec() => Spec(
        "spec.multi-venue",
        StrategyObjectiveKind.ReturnSeeking,
        MarketTopologyKind.MultiVenue,
        ExposureGeometryKind.Spread,
        [AssetClass.Equity],
        [StrategyInformationKind.Quote, StrategyInformationKind.Trade],
        [ReturnHypothesisKind.Convergence],
        [StrategyTriggerKind.Quote, StrategyTriggerKind.Trade],
        [SignalModelKind.DeterministicRule],
        PortfolioConstructionKind.ExposureNeutral,
        [StrategyRiskExitKind.StopLoss, StrategyRiskExitKind.TimeExit, StrategyRiskExitKind.ExposureCap],
        [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.CoordinatedLegs],
        [StrategyStateKind.PositionAware, StrategyStateKind.EventLifecycle]);

    private static StrategySpec PortfolioSpec() => Spec(
        "spec.portfolio",
        StrategyObjectiveKind.Allocation,
        MarketTopologyKind.CrossSection,
        ExposureGeometryKind.CrossSectionalLongShort,
        [AssetClass.Equity],
        [StrategyInformationKind.Bar, StrategyInformationKind.Fundamental],
        [ReturnHypothesisKind.Value, ReturnHypothesisKind.Quality],
        [StrategyTriggerKind.Schedule],
        [SignalModelKind.Ranking, SignalModelKind.Optimization],
        PortfolioConstructionKind.Optimized,
        [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
        [StrategyExecutionPolicyKind.Market],
        [StrategyStateKind.PositionAware, StrategyStateKind.RegimeAware]);

    private static StrategySpec QuoteSpec() => Spec(
        "spec.quote",
        StrategyObjectiveKind.LiquidityProvision,
        MarketTopologyKind.SingleInstrument,
        ExposureGeometryKind.MarketNeutral,
        [AssetClass.Equity],
        [StrategyInformationKind.Depth, StrategyInformationKind.Trade],
        [ReturnHypothesisKind.LiquidityProvision],
        [StrategyTriggerKind.Depth, StrategyTriggerKind.Trade],
        [SignalModelKind.DeterministicRule],
        PortfolioConstructionKind.InventoryTarget,
        [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.LiquidityCap, StrategyRiskExitKind.DrawdownKillSwitch],
        [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.Passive,
            StrategyExecutionPolicyKind.ContinuousQuoting],
        [StrategyStateKind.InventoryAware]);

    private static StrategySpec ExecutionSpec() => Spec(
        "spec.execution",
        StrategyObjectiveKind.Execution,
        MarketTopologyKind.SingleInstrument,
        ExposureGeometryKind.LongOnly,
        [AssetClass.Equity],
        [StrategyInformationKind.Quote, StrategyInformationKind.Trade],
        [ReturnHypothesisKind.None],
        [StrategyTriggerKind.OrderEvent, StrategyTriggerKind.Schedule],
        [SignalModelKind.DeterministicRule],
        PortfolioConstructionKind.FixedQuantity,
        [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.TimeExit],
        [StrategyExecutionPolicyKind.Vwap, StrategyExecutionPolicyKind.SmartRouting],
        [StrategyStateKind.EventLifecycle]);

    private static StrategySpec SignalOnlySpec() => Spec(
        "spec.signal-only",
        StrategyObjectiveKind.ReturnSeeking,
        MarketTopologyKind.SingleInstrument,
        ExposureGeometryKind.LongOnly,
        [AssetClass.Equity],
        [StrategyInformationKind.Bar],
        [ReturnHypothesisKind.Momentum],
        [StrategyTriggerKind.Bar],
        [SignalModelKind.Statistical],
        PortfolioConstructionKind.NotApplicable,
        [StrategyRiskExitKind.NotApplicable],
        [StrategyExecutionPolicyKind.NotApplicable],
        [StrategyStateKind.Stateless]);

    private static StrategySpec HedgingSpec() => Spec(
        "spec.hedge",
        StrategyObjectiveKind.Hedging,
        MarketTopologyKind.CrossAsset,
        ExposureGeometryKind.MarketNeutral,
        [AssetClass.Equity, AssetClass.Future],
        [StrategyInformationKind.Quote, StrategyInformationKind.Bar],
        [ReturnHypothesisKind.Defensive],
        [StrategyTriggerKind.Bar],
        [SignalModelKind.Statistical],
        PortfolioConstructionKind.VolatilityTarget,
        [StrategyRiskExitKind.ExposureCap, StrategyRiskExitKind.DrawdownKillSwitch],
        [StrategyExecutionPolicyKind.Market],
        [StrategyStateKind.PositionAware]);

    private static StrategySpec OptionsSpec() => Spec(
        "spec.options",
        StrategyObjectiveKind.ReturnSeeking,
        MarketTopologyKind.UnderlyingAndDerivative,
        ExposureGeometryKind.VolatilityExposure,
        [AssetClass.Equity, AssetClass.Option],
        [StrategyInformationKind.Quote, StrategyInformationKind.ImpliedVolatilitySurface],
        [ReturnHypothesisKind.VolatilityInsurance],
        [StrategyTriggerKind.ContractLifecycle, StrategyTriggerKind.Quote],
        [SignalModelKind.Statistical],
        PortfolioConstructionKind.RiskBudget,
        [StrategyRiskExitKind.GreekCap, StrategyRiskExitKind.EventResolution, StrategyRiskExitKind.TimeExit],
        [StrategyExecutionPolicyKind.Limit, StrategyExecutionPolicyKind.CoordinatedLegs],
        [StrategyStateKind.PositionAware, StrategyStateKind.EventLifecycle]);

    private static StrategySpec Spec(
        string id,
        StrategyObjectiveKind objective,
        MarketTopologyKind topology,
        ExposureGeometryKind exposure,
        IReadOnlyList<AssetClass> assets,
        IReadOnlyList<StrategyInformationKind> information,
        IReadOnlyList<ReturnHypothesisKind> hypotheses,
        IReadOnlyList<StrategyTriggerKind> triggers,
        IReadOnlyList<SignalModelKind> models,
        PortfolioConstructionKind portfolio,
        IReadOnlyList<StrategyRiskExitKind> risk,
        IReadOnlyList<StrategyExecutionPolicyKind> execution,
        IReadOnlyList<StrategyStateKind> state) =>
        new(
            id,
            id,
            objective,
            new StrategyContextSpec(
                assets,
                topology,
                exposure,
                information,
                new StrategyTimeSemantics(
                    StrategyHorizonKind.Intraday,
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromHours(2))),
            new StrategySignalSpec(hypotheses, triggers, models),
            new StrategyPortfolioSpec(portfolio),
            new StrategyRiskSpec(risk),
            new StrategyExecutionSpec(execution),
            new StrategyStateSpec(state, StrategyAdaptationKind.Fixed),
            []);

    private static string Describe(StrategyIntentConfirmationResultV1 result) => string.Join(
        Environment.NewLine,
        result.Issues.Select(issue => $"{issue.Code} {issue.Path}: {issue.Message}")
            .Concat(result.Questions.Select(question =>
                $"QUESTION {question.RequirementId}: {question.Reason} {question.Prompt}")));

    private sealed record Fixture(
        StrategyCandidateV1 Candidate,
        ResearchCaseV1 ResearchCase,
        StrategySpec Classification,
        StrategyIntentDraftV1 Draft);

    private sealed class StubExtensionRegistry(
        params (StrategyIntentExtensionRoleV1 Role, string Id)[] ownedIds)
        : IStrategyIntentExtensionRegistryV1
    {
        private readonly HashSet<(StrategyIntentExtensionRoleV1 Role, string Id)> _ownedIds =
            new(ownedIds);

        public bool Owns(StrategyIntentExtensionRoleV1 role, string extensionId) =>
            _ownedIds.Contains((role, extensionId));
    }
}
