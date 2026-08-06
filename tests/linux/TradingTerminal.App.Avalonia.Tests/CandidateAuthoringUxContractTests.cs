using System.Xml.Linq;
using FluentAssertions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class CandidateAuthoringUxContractTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void Candidate_picker_is_a_two_by_two_grid_with_unambiguous_state_and_action_labels()
    {
        var root = LoadAuthoringWindow();
        var candidateList = root.Descendants(Avalonia + "ListBox").Single(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding GeneratedCandidateOptions}");

        var candidateGrid = candidateList.Descendants(Avalonia + "UniformGrid").Single();
        candidateGrid.Attribute("Columns")!.Value.Should().Be("2");
        candidateGrid.Attribute("Rows")!.Value.Should().Be("2");

        candidateList.Descendants(Avalonia + "Border").Should().Contain(border =>
            (string?)border.Attribute("IsVisible") == "{Binding IsPreviewed}" &&
            border.Descendants(Avalonia + "TextBlock").Any(label =>
                (string?)label.Attribute("Text") == "PREVIEW"));
        candidateList.Descendants(Avalonia + "Border").Should().Contain(border =>
            (string?)border.Attribute("IsVisible") == "{Binding IsChosen}" &&
            border.Descendants(Avalonia + "TextBlock").Any(label =>
                (string?)label.Attribute("Text") == "ACTIVE IN EDITOR"));

        var candidateAction = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Command") == "{Binding ChooseGeneratedCandidateCommand}");
        candidateAction.Attribute("Content")!.Value.Should().Be("{Binding CandidateActionText}");

        root.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value)
            .Should().NotContain("Choose & edit");
    }

    [Fact]
    public void Generation_mode_switch_is_secondary_and_candidate_generation_has_truthful_phase_feedback()
    {
        var root = LoadAuthoringWindow();

        var modeAction = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "{Binding GenerationModeActionText}");
        modeAction.Attribute("Command")!.Value.Should().Be("{Binding ToggleGenerationModeCommand}");
        modeAction.Attribute("Classes")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain("ghost").And.NotContain("aiAction");

        var progressRegion = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("IsVisible") == "{Binding IsGeneratingCandidates}");
        progressRegion.Descendants(Avalonia + "ProgressBar").Should().BeEmpty(
            "generation has no provider percentage or ETA, so a progress bar would imply false precision");
        progressRegion.Descendants(Avalonia + "ItemsControl").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding GenerationLaneProgressRows}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StateLabel}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding PipelineText}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StateDetail}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding ElapsedCompact}");
        progressRegion.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Nothing is compiled, tested, run, or backtested here.");
    }

    [Fact]
    public void New_strategy_uses_a_filterable_axis_catalog_instead_of_three_hard_coded_prompts()
    {
        var root = LoadAuthoringWindow();

        root.Descendants(Avalonia + "ListBox").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding VisibleStarterBriefs}");
        root.Descendants(Avalonia + "TextBox").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding StarterSearchText, Mode=TwoWay}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StarterFamilyOptions}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StarterHorizonOptions}");
        root.Descendants(Avalonia + "ComboBox").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding StarterDataOptions}");

        root.Descendants().SelectMany(element => element.Attributes()).Select(attribute => attribute.Value)
            .Should().NotContain("{Binding SuggestionBriefs}");
    }

    [Fact]
    public void Package_valid_graph_exposes_an_explicit_synthetic_smoke_action_and_boundary()
    {
        var root = LoadAuthoringWindow();
        var backtestAction = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "{Binding BacktestActionText}");

        backtestAction.Attribute("IsEnabled")!.Value
            .Should().Be("{Binding CanPrepareGeneratedCandidateForBacktest}");
        backtestAction.Attribute("Command")!.Value
            .Should().Be("{Binding RunTradeIrSimulatedBacktestCommand}");
        root.Descendants(Avalonia + "ItemsControl").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding BacktestReadinessStages}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding TradeIrBacktestBoundaryText}");
        root.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding TradeIrBacktestSummary}");
    }

    [Fact]
    public void Candidate_outcome_panel_explains_selection_failure_truth_and_next_actions_above_the_grid()
    {
        var root = LoadAuthoringWindow();
        var outcome = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Candidate outcome and next steps"));

        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CandidateBatchHeadline}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding FirstBlockedGeneratedCandidateOption.FirstIssueCode}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding FirstBlockedGeneratedCandidateOption.FirstIssuePath}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "{Binding FirstBlockedGeneratedCandidateOption.FirstIssueMessage}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Generated = structurally shaped draft only — not proven correct, runnable, tested, or backtest-ready.");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CandidateBacktestAvailabilityText}");
        outcome.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            (string?)element.Attribute("Text") ==
                "Graph validation and smoke compatibility are separate. Invalid or unsupported graphs cannot run; the installed runner supports the known QuoteL1 EMA smoke profile only.");

        var smokeStarter = outcome.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Load QuoteL1 EMA smoke starter");
        smokeStarter.Attribute("Command")!.Value
            .Should().Be("{Binding UseQuoteL1EmaSmokeStarterCommand}");
        smokeStarter.Attribute("IsEnabled")!.Value
            .Should().Be("{Binding !IsGenerating}");

        var expert = outcome.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Expert C# (separate path)");
        expert.Attribute("Command")!.Value.Should().Be("{Binding ToggleGenerationModeCommand}");
    }

    [Fact]
    public void TradeIr_synthesis_is_an_explicit_fifth_artifact_with_separate_use_action()
    {
        var root = LoadAuthoringWindow();
        var synthesis = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "TradeIR synthesis bridge"));

        var synthesize = synthesis.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "Synthesize valid drafts → TradeIR");
        synthesize.Attribute("Command")!.Value.Should().Be("{Binding SynthesizeTradeIrCommand}");
        synthesize.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanSynthesizeTradeIr}");

        var use = synthesis.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Content") == "{Binding CombinedTradeIrActionText}");
        use.Attribute("Command")!.Value.Should().Be("{Binding UseCombinedTradeIrCommand}");
        use.Attribute("IsEnabled")!.Value.Should().Be("{Binding CanUseCombinedTradeIr}");

        synthesis.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CombinedTradeIrTargetHash}");
        synthesis.Descendants(Avalonia + "Run").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding CombinedTradeIrReceiptHash}");
    }

    [Fact]
    public void Generated_candidate_body_scrolls_as_one_surface_at_the_supported_compact_size()
    {
        var root = LoadAuthoringWindow();
        var scroller = root.Descendants(Avalonia + "ScrollViewer").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" &&
                attribute.Value == "Scrollable candidate results"));

        scroller.Attribute("VerticalScrollBarVisibility")!.Value.Should().Be("Auto");
        scroller.Attribute("HorizontalScrollBarVisibility")!.Value.Should().Be("Disabled");
        scroller.Descendants(Avalonia + "ListBox").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding GeneratedCandidateOptions}");
        scroller.Descendants(Avalonia + "ItemsControl").Should().ContainSingle(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding BacktestReadinessStages}");
    }

    [Fact]
    public void Failed_lane_option_exposes_the_first_error_code_path_and_message_without_flattening_it()
    {
        var firstError = new StrategyCandidateGenerationIssueV1(
            StrategyCandidateGenerationIssueSeverityV1.Error,
            "LANE_JSON_INVALID",
            "$.definition.dataRequirements[0].instrumentSelector.references[0].assetClass",
            "The JSON value could not be converted to AssetClass.");
        var option = new StrategyGenerationCandidateOption(new StrategyGenerationLaneResultV1(
            StrategyGenerationLaneV1.TypedGraph,
            StrategyGenerationReadinessV1.Invalid,
            null,
            null,
            [firstError],
            new StrategyGenerationAgentRunV1(
                "graph-agent",
                "test-provider",
                null,
                true,
                null,
                null,
                CodegenUsage.None)));

        option.LaneName.Should().Be("Graph · Typed");
        option.FirstIssueCode.Should().Be(firstError.Code);
        option.FirstIssuePath.Should().Be(firstError.Path);
        option.FirstIssueMessage.Should().Be(firstError.Message);
    }

    [Fact]
    public void Composer_keeps_send_anchored_while_secondary_controls_wrap_at_narrow_widths()
    {
        var root = LoadAuthoringWindow();
        var send = root.Descendants(Avalonia + "Button").Single(element =>
            (string?)element.Attribute("Command") == "{Binding SendCommand}");
        var actionGrid = send.Ancestors(Avalonia + "Grid").First(element =>
            (string?)element.Attribute("ColumnDefinitions") == "*,Auto");

        actionGrid.Descendants(Avalonia + "WrapPanel").Should().ContainSingle(panel =>
            (string?)panel.Attribute("Grid.Column") == "0");
        send.Ancestors(Avalonia + "StackPanel").First()
            .Attribute("Grid.Column")!.Value.Should().Be("1");
    }

    [Fact]
    public void Cli_workspace_footer_wraps_buttons_and_scrolls_vertically_in_narrow_workbenches()
    {
        var root = LoadAuthoringWindow();
        var footer = root.Descendants(Avalonia + "Border").Single(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "CliWorkspaceFooter"));
        var scroller = footer.Descendants(Avalonia + "ScrollViewer").Single();

        scroller.Attribute("HorizontalScrollBarVisibility")!.Value.Should().Be("Disabled");
        scroller.Attribute("VerticalScrollBarVisibility")!.Value.Should().Be("Auto");
        scroller.Attribute("MaxHeight")!.Value.Should().Be("84");

        var cliList = footer.Descendants(Avalonia + "ItemsControl").Single(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding AvailableClis}");
        cliList.Descendants(Avalonia + "ItemsPanelTemplate").Single()
            .Descendants(Avalonia + "WrapPanel").Should().ContainSingle();
        cliList.Descendants(Avalonia + "ItemsPanelTemplate").Single()
            .Descendants(Avalonia + "StackPanel").Should().BeEmpty();
    }

    private static XElement LoadAuthoringWindow() =>
        XDocument.Load(Fixture("StrategyAuthoringWindow.axaml")).Root
        ?? throw new InvalidOperationException("The strategy authoring fixture has no root element.");

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
