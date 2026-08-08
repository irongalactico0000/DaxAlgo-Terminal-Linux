using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TradingTerminal.Infrastructure.StrategyAgent;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

public sealed class StrategyAgentHostTests
{
    [Fact]
    public void Default_port_is_separate_from_existing_daxalgo_ml_sidecar()
    {
        new StrategyAgentOptions().Port.Should().Be(8766).And.NotBe(8765);
    }

    [Fact]
    public void Default_request_timeout_exceeds_python_research_timeout()
    {
        new StrategyAgentOptions().RequestTimeoutSeconds
            .Should().Be(300).And.BeGreaterThan(180);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\",\"service\":\"daxalgo-native-strategy-agent\"}", true)]
    [InlineData("{\"status\":\"ok\",\"service\":\"daxalgo-ml\"}", false)]
    [InlineData("not-json", false)]
    public void Host_accepts_only_the_exact_strategy_agent_health_payload(
        string payload,
        bool expected)
    {
        StrategyAgentHostService.IsExpectedHealthPayload(
                System.Text.Encoding.UTF8.GetBytes(payload))
            .Should().Be(expected);
    }

    [Fact]
    public void Host_resolves_packaged_mac_python_runtime_with_real_cli_shape()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("DaxAlgo Terminal.app", "Contents", "MacOS");
        var packageDir = tree.Directory(
            "DaxAlgo Terminal.app",
            "Contents",
            "Resources",
            "strategy-agent");
        tree.File(
            "DaxAlgo Terminal.app",
            "Contents",
            "Resources",
            "strategy-agent",
            "pyproject.toml");
        tree.File(
            "DaxAlgo Terminal.app",
            "Contents",
            "Resources",
            "strategy-agent",
            "daxalgo_strategy_agent",
            "cli.py");
        var python = tree.File(
            "DaxAlgo Terminal.app",
            "Contents",
            "Resources",
            "strategy-agent",
            ".venv",
            "bin",
            "python3");

        var launch = StrategyAgentHostService.ResolveLaunchForPlatform(
            new StrategyAgentOptions(),
            8766,
            baseDir,
            userAppDir: null,
            isWindows: false);

        launch.Should().NotBeNull();
        launch!.Value.FileName.Should().Be(python);
        launch.Value.Args.Should().Equal(
            "-m",
            "daxalgo_strategy_agent.cli",
            "serve",
            "--port",
            "8766");
        launch.Value.WorkDir.Should().Be(packageDir);
    }

    [Fact]
    public void Host_does_not_walk_source_tree_outside_explicit_debug_discovery()
    {
        using var tree = new TempTree();
        var baseDir = tree.Directory("repo", "artifacts", "app");
        tree.File("repo", "tools", "strategy-agent", "pyproject.toml");
        tree.File(
            "repo",
            "tools",
            "strategy-agent",
            "daxalgo_strategy_agent",
            "cli.py");
        var python = tree.File(
            "repo",
            "tools",
            "strategy-agent",
            ".venv",
            "bin",
            "python3");

        StrategyAgentHostService.ResolveLaunchForPlatform(
                new StrategyAgentOptions(),
                8766,
                baseDir,
                userAppDir: null,
                isWindows: false)
            .Should().BeNull();

        var debugLaunch = StrategyAgentHostService.ResolveLaunchForPlatform(
            new StrategyAgentOptions(),
            8766,
            baseDir,
            userAppDir: null,
            isWindows: false,
            allowDevelopmentSourceDiscovery: true);
        debugLaunch.Should().NotBeNull();
        debugLaunch!.Value.FileName.Should().Be(python);
    }

    [Fact]
    public void Registration_keeps_one_host_instance_and_configures_loopback_client()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["StrategyAgent:Enabled"] = "false",
                ["StrategyAgent:Port"] = "9877",
                ["StrategyAgent:RequestTimeoutSeconds"] = "33",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStrategyAgent(configuration);
        using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<IStrategyAgentHost>();
        var hosted = provider.GetServices<IHostedService>()
            .OfType<StrategyAgentHostService>()
            .Single();

        host.Should().BeSameAs(hosted);
        provider.GetRequiredService<IOptionsMonitor<StrategyAgentOptions>>()
            .CurrentValue.Port.Should().Be(9877);
        provider.GetRequiredService<IStrategyAgentClient>().Should().NotBeNull();
    }

    private sealed class TempTree : IDisposable
    {
        public TempTree()
        {
            Root = Path.Combine(Path.GetTempPath(), $"daxalgo-strategy-agent-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string Directory(params string[] segments)
        {
            var path = Combine(segments);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(params string[] segments)
        {
            var path = Combine(segments);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, string.Empty);
            return path;
        }

        private string Combine(IEnumerable<string> segments) =>
            segments.Aggregate(
                Root,
                static (current, segment) => Path.Combine(current, segment));

        public void Dispose()
        {
            var root = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
                System.IO.Directory.Exists(root))
                System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
