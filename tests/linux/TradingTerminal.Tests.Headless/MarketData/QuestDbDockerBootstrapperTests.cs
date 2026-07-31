using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.MarketData.Store;
using Xunit;

namespace TradingTerminal.Tests.Headless.MarketData;

public sealed class QuestDbDockerBootstrapperTests
{
    [Fact]
    public void Managed_defaults_are_loopback_safe()
    {
        var options = new MarketDataStoreOptions
        {
            Provider = MarketDataProvider.QuestDb,
            QuestDbLaunchMode = QuestDbLaunchMode.Native,
        };

        QuestDbDockerBootstrapper.HasSafeEndpoints(options, out var reason)
            .Should().BeTrue(reason);
    }

    [Fact]
    public void Managed_mode_rejects_remote_pg_host()
    {
        var options = new MarketDataStoreOptions
        {
            Provider = MarketDataProvider.QuestDb,
            QuestDbPgConnectionString =
                "Host=db.example.com;Port=8812;Database=qdb;Username=admin;Password=quest",
        };

        QuestDbDockerBootstrapper.HasSafeEndpoints(options, out var reason)
            .Should().BeFalse();
        reason.Should().Contain("loopback");
    }

    [Fact]
    public void Managed_mode_rejects_nonstandard_local_ports()
    {
        var options = new MarketDataStoreOptions
        {
            Provider = MarketDataProvider.QuestDb,
            QuestDbPgConnectionString =
                "Host=127.0.0.1;Port=18812;Database=qdb;Username=admin;Password=quest",
            QuestDbIlpConfig = "http::addr=127.0.0.1:19000;auto_flush=off;",
        };

        QuestDbDockerBootstrapper.HasSafeEndpoints(options, out var reason)
            .Should().BeFalse();
        reason.Should().Contain("port 8812");
    }

    [Fact]
    public void External_mode_allows_alternate_loopback_ports()
    {
        var options = new MarketDataStoreOptions
        {
            Provider = MarketDataProvider.QuestDb,
            QuestDbLaunchMode = QuestDbLaunchMode.External,
            QuestDbPgConnectionString =
                "Host=127.0.0.1;Port=18812;Database=qdb;Username=admin;Password=quest",
            QuestDbIlpConfig = "https::addr=localhost:19000;auto_flush=off;",
        };

        QuestDbDockerBootstrapper.HasSafeEndpoints(options, out var reason)
            .Should().BeTrue(reason);
    }

    [Fact]
    public void External_mode_rejects_remote_ilp_host()
    {
        var options = new MarketDataStoreOptions
        {
            Provider = MarketDataProvider.QuestDb,
            QuestDbLaunchMode = QuestDbLaunchMode.External,
            QuestDbIlpConfig = "https::addr=questdb.example.com:9000;auto_flush=off;",
        };

        QuestDbDockerBootstrapper.HasSafeEndpoints(options, out var reason)
            .Should().BeFalse();
        reason.Should().Contain("loopback");
    }
}
