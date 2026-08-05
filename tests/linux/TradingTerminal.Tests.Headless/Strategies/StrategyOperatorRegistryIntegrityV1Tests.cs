using FluentAssertions;
using TradingTerminal.Core.Strategies.Definition;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class StrategyOperatorRegistryIntegrityV1Tests
{
    private const string SemanticContractHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Descriptor_and_registry_collections_are_defensive_read_only_snapshots()
    {
        var requiredPorts = new List<string> { "value" };
        var optionalPorts = new List<string> { "fallback" };
        var capabilities = new List<StrategyCapabilityRequirementV1>
        {
            new("test.capability", "Required by the test operator."),
        };
        var descriptor = Descriptor(
            "test.operator",
            requiredPorts,
            optionalPorts,
            StrategyOperatorPlacementV1.RestrictedCompute,
            capabilities,
            BindNumber);
        var registry = new StrategyOperatorRegistryV1("test.catalog", "1.0.0", [descriptor]);
        var catalogHash = registry.Catalog.CatalogHashSha256;

        requiredPorts[0] = "mutated";
        optionalPorts.Clear();
        capabilities.Clear();

        descriptor.RequiredInputPorts.Should().Equal("value");
        descriptor.OptionalInputPorts.Should().Equal("fallback");
        descriptor.Capabilities.Should().ContainSingle()
            .Which.CapabilityId.Should().Be("test.capability");
        registry.Catalog.CatalogHashSha256.Should().Be(catalogHash);

        var mutatePorts = () => ((IList<string>)descriptor.RequiredInputPorts)[0] = "mutated";
        var mutateCapabilities = () => ((IList<StrategyCapabilityRequirementV1>)descriptor.Capabilities).Clear();
        var mutateKeys = () => ((IList<StrategyOperatorKeyV1>)registry.Keys).Clear();

        mutatePorts.Should().Throw<NotSupportedException>();
        mutateCapabilities.Should().Throw<NotSupportedException>();
        mutateKeys.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Claimed_hash_cannot_cover_changed_ports_placement_or_type_rule()
    {
        var trustedDescriptor = Descriptor(
            "test.operator",
            ["value"],
            [],
            StrategyOperatorPlacementV1.RestrictedCompute,
            [],
            BindNumber);
        var trustedRegistry = new StrategyOperatorRegistryV1("test.catalog", "1.0.0", [trustedDescriptor]);
        var trustedReference = trustedRegistry.Catalog;

        var mutations = new[]
        {
            Descriptor(
                "test.operator",
                ["renamed"],
                [],
                StrategyOperatorPlacementV1.RestrictedCompute,
                [],
                BindNumber),
            Descriptor(
                "test.operator",
                ["value"],
                [],
                StrategyOperatorPlacementV1.HostRisk,
                [],
                BindNumber),
            Descriptor(
                "test.operator",
                ["value"],
                [],
                StrategyOperatorPlacementV1.RestrictedCompute,
                [],
                BindBoolean),
        };

        new StrategyOperatorRegistryV1(trustedReference, [trustedDescriptor]).Catalog
            .Should().Be(trustedReference);
        foreach (var mutation in mutations)
        {
            var derived = new StrategyOperatorRegistryV1("test.catalog", "1.0.0", [mutation]);
            derived.Catalog.CatalogHashSha256.Should().NotBe(trustedReference.CatalogHashSha256);

            var verifyClaim = () => new StrategyOperatorRegistryV1(trustedReference, [mutation]);
            verifyClaim.Should().Throw<ArgumentException>().WithMessage("*does not match derived descriptor hash*");
        }

        var context = EmptyContext();
        trustedDescriptor.Binder(context).OutputType!.TypeId.Should().Be(StrategyIrTypeIdsV1.Number);
        mutations[2].Binder(context).OutputType!.TypeId.Should().Be(StrategyIrTypeIdsV1.Boolean);
    }

    [Fact]
    public void Catalog_hash_is_independent_of_descriptor_enumeration_order()
    {
        var first = Descriptor(
            "test.alpha",
            ["left", "right"],
            [],
            StrategyOperatorPlacementV1.RestrictedCompute,
            [new StrategyCapabilityRequirementV1("capability.b", "B"),
             new StrategyCapabilityRequirementV1("capability.a", "A")],
            BindNumber);
        var second = Descriptor(
            "test.beta",
            [],
            [],
            StrategyOperatorPlacementV1.HostPortfolio,
            [],
            BindBoolean);

        var forward = new StrategyOperatorRegistryV1("test.catalog", "1.0.0", [first, second]);
        var reverse = new StrategyOperatorRegistryV1("test.catalog", "1.0.0", [second, first]);

        reverse.Catalog.Should().Be(forward.Catalog);
        reverse.Keys.Should().Equal(forward.Keys);
    }

    [Fact]
    public void Mutable_captured_binder_state_is_rejected()
    {
        var selectedType = StrategyIrTypeIdsV1.Number;
        StrategyOperatorBinderV1 captured = _ => Bind(selectedType);

        var create = () => Descriptor(
            "test.operator",
            [],
            [],
            StrategyOperatorPlacementV1.RestrictedCompute,
            [],
            captured);

        create.Should().Throw<ArgumentException>().WithMessage("*single static method*");
    }

    private static StrategyOperatorDescriptorV1 Descriptor(
        string operatorId,
        IReadOnlyList<string> requiredPorts,
        IReadOnlyList<string> optionalPorts,
        StrategyOperatorPlacementV1 placement,
        IReadOnlyList<StrategyCapabilityRequirementV1> capabilities,
        StrategyOperatorBinderV1 binder) => new(
            new StrategyOperatorKeyV1(operatorId, 1),
            requiredPorts,
            optionalPorts,
            StrategyOperatorStateKindV1.Stateless,
            placement,
            capabilities,
            SemanticContractHash,
            binder);

    private static StrategyOperatorBindingContextV1 EmptyContext() => new(
        new StrategyIrNodeV1(
            "node",
            "test.operator",
            1,
            new Dictionary<string, string>(),
            new Dictionary<string, StrategyLiteralV1>()),
        new Dictionary<string, StrategyValueTypeV1>(),
        []);

    private static StrategyOperatorBindingResultV1 BindNumber(StrategyOperatorBindingContextV1 _) =>
        Bind(StrategyIrTypeIdsV1.Number);

    private static StrategyOperatorBindingResultV1 BindBoolean(StrategyOperatorBindingContextV1 _) =>
        Bind(StrategyIrTypeIdsV1.Boolean);

    private static StrategyOperatorBindingResultV1 Bind(string typeId) => new(
        new StrategyValueTypeV1(
            typeId,
            [],
            "unitless",
            StrategyValueAvailabilityV1.Ready,
            Nullable: false),
        0,
        []);
}
