using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Recording;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using Xunit;

namespace TradingTerminal.Tests.Recording;

public sealed class RecorderContractsTests
{
    [Fact]
    public void L3_is_explicitly_unavailable()
    {
        RecorderEntry.SupportsL3.Should().BeFalse(
            "no broker client, canonical store stream, or feed currently produces market-by-order data");
    }

    [Fact]
    public void Watchlist_item_round_trips_the_full_instrument_and_pinned_broker()
    {
        var instrument = new SignalInstrument(
            "Bitcoin / US Dollar",
            "Crypto",
            new Contract("BTCUSDT", "CRYPTO", "BINANCE", "USD", "BINANCE"),
            BrokerKind.Binance);

        var saved = RecorderWatchlistItem.From(instrument, BrokerKind.Binance);

        saved.PinnedBroker.Should().Be(BrokerKind.Binance);
        saved.ToInstrument().Should().Be(instrument);
    }

    [Fact]
    public void Registration_uses_one_singleton_for_the_service_and_hosted_lifetime()
    {
        var descriptors = new ServiceCollection();

        descriptors.AddRecordingSurface();

        var recorderDescriptor = descriptors.Single(d => d.ServiceType == typeof(TickRecordingService));
        recorderDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var hostedDescriptor = descriptors.Single(d => d.ServiceType == typeof(IHostedService));
        hostedDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        hostedDescriptor.ImplementationFactory.Should().NotBeNull();

        var archiveOptions = Substitute.For<IOptionsMonitor<ArchiveOptions>>();
        archiveOptions.CurrentValue.Returns(new ArchiveOptions());
        var telegramOptions = Substitute.For<IOptionsMonitor<TelegramArchiveOptions>>();
        telegramOptions.CurrentValue.Returns(new TelegramArchiveOptions());

        using var recorder = new TickRecordingService(
            Substitute.For<IMarketDataIngest>(),
            Substitute.For<IMarketDataHub>(),
            Substitute.For<IBrokerSelector>(),
            Substitute.For<IMarketDataArchiver>(),
            archiveOptions,
            telegramOptions,
            new InMemoryLogSink(),
            NullLogger<TickRecordingService>.Instance);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(TickRecordingService)).Returns(recorder);

        hostedDescriptor.ImplementationFactory!(provider).Should().BeSameAs(recorder);
    }
}
