using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using DaxAlgo.Daxq.Compiler;
using DaxAlgo.Daxq.Contracts;
using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.UI.Strategies;
using AuthoringContext = DaxAlgo.Sdk.IStrategyContext;
using AuthoringStrategy = DaxAlgo.Sdk.IBacktestStrategy;

namespace DaxAlgo.Daxq.Host.Tests;

public sealed class DaxqHostEndToEndTests
{
    private const string ParitySource = """
        using DaxAlgo.Sdk;

        public sealed class ParityStrategy : IBacktestStrategy
        {
            public void OnBar(IStrategyContext context)
            {
                if (context.Bar(BarField.Close, 0) >= 100.0)
                    context.Emit(SignalKind.Long, 0.75, 17);
                else
                    context.Emit(SignalKind.Short, 0.25, 9);
            }
        }
        """;

    [Fact]
    public async Task Compiler_artifact_loads_through_dual_loader_and_matches_managed_signals()
    {
        using var directory = new TestDirectory();
        var artifact = Compile(ParitySource, "example.parity");
        var artifactPath = Path.Combine(directory.Path, "example.parity.daxq");
        File.WriteAllBytes(artifactPath, artifact.Package.PackageBytes);

        var services = new ServiceCollection();
        var engine = ReferenceEngine();
        var report = PluginLoader.LoadWithReport(
            services,
            directory.Path,
            SdkInfo.Version,
            protectedStrategyEngine: engine);

        var loaded = Assert.Single(report.Loaded);
        Assert.Equal(artifactPath, loaded.AssemblyPath);
        Assert.Empty(report.Problems);

        using var provider = services.BuildServiceProvider();
        var descriptor = Assert.Single(provider.GetServices<ITradingStrategy>());
        var option = Assert.Single(provider.GetServices<BacktestStrategyOption>());
        var factory = Assert.Single(provider.GetServices<StrategyFactoryRegistration>());
        Assert.Equal("example.parity", descriptor.Id);
        Assert.Equal(descriptor.Id, option.Id);
        Assert.Equal(descriptor.Id, factory.StrategyId);
        var catalog = new StrategyFactory(
            provider,
            provider.GetServices<ITradingStrategy>(),
            provider.GetServices<StrategyFactoryRegistration>());
        Assert.Single(catalog.All, strategy => strategy.Id == "example.parity");

        var bars = new[]
        {
            new Bar(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 98, 100, 97, 99, 10),
            new Bar(new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc), 99, 102, 98, 101, 12),
        };

        var managedSignals = RunManaged(artifact, bars);
        var protectedSignals = await RunProtectedAsync(option, bars);
        Assert.Equal(managedSignals, protectedSignals);
        Assert.Equal(
            new[]
            {
                new StrategySignal(StrategySignalKind.Short, 0.25, 9),
                new StrategySignal(StrategySignalKind.Long, 0.75, 17),
            },
            protectedSignals);
    }

    [Fact]
    public void Installer_activates_now_and_persists_for_restart_discovery()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "download.daxq");
        File.WriteAllBytes(sourcePath, Compile(ParitySource, "example.install").Package.PackageBytes);
        var pluginsRoot = Path.Combine(directory.Path, "plugins");
        var state = new PluginStateStore(pluginsRoot);
        var context = new PluginHostContext(
            pluginsRoot, PluginTrustPolicy.Permissive, [], PluginLoadReport.Empty, state);
        var backtests = new RecordingBacktestRegistry();
        var catalog = new RecordingStrategyFactory();
        var installer = new DaxqStrategyInstaller(
            ReferenceEngine(), backtests, catalog, context,
            NullLogger<DaxqStrategyInstaller>.Instance);

        var result = installer.Install(sourcePath);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Active);
        Assert.True(result.Persisted);
        Assert.Single(backtests.All, option => option.Id == "example.install");
        Assert.Single(catalog.All, strategy => strategy.Id == "example.install");
        Assert.Single(context.RuntimeInstalledThisSession);
        var installedPath = Path.Combine(
            pluginsRoot, "example.install", "example.install.daxq");
        Assert.True(File.Exists(installedPath));

        var restartServices = new ServiceCollection();
        var restart = PluginLoader.LoadWithReport(
            restartServices, pluginsRoot, SdkInfo.Version, state,
            protectedStrategyEngine: ReferenceEngine());
        Assert.Single(restart.Loaded, plugin => plugin.AssemblyPath == installedPath);
        Assert.Empty(restart.Problems);
    }

    [Fact]
    public void Installer_rejects_a_revoked_strategy_before_activation()
    {
        using var directory = new TestDirectory();
        var sourcePath = Path.Combine(directory.Path, "download.daxq");
        File.WriteAllBytes(sourcePath, Compile(ParitySource, "example.revoked").Package.PackageBytes);
        var pluginsRoot = Path.Combine(directory.Path, "plugins");
        PluginRevocationList.Merge(
            pluginsRoot,
            [new RevokedPlugin(Id: "example.revoked", Reason: "withdrawn test build")]);
        var context = new PluginHostContext(
            pluginsRoot, PluginTrustPolicy.Permissive, [], PluginLoadReport.Empty,
            new PluginStateStore(pluginsRoot));
        var backtests = new RecordingBacktestRegistry();
        var catalog = new RecordingStrategyFactory();
        var installer = new DaxqStrategyInstaller(
            ReferenceEngine(), backtests, catalog, context,
            NullLogger<DaxqStrategyInstaller>.Instance);

        var result = installer.Install(sourcePath);

        Assert.False(result.Success);
        Assert.Contains("withdrawn test build", result.Message, StringComparison.Ordinal);
        Assert.Empty(backtests.All);
        Assert.Empty(catalog.All);
        Assert.Empty(context.RuntimeInstalledThisSession);
    }

    [Fact]
    public void Tampered_release_signature_is_rejected_before_program_load()
    {
        using var directory = new TestDirectory();
        var artifact = Compile(ParitySource, "example.tampered");
        var bytes = (byte[])artifact.Package.PackageBytes.Clone();
        var marker = Encoding.UTF8.GetBytes("\"sig\":\"");
        var markerIndex = IndexOf(bytes, marker);
        Assert.True(markerIndex >= 0);
        var signatureIndex = markerIndex + marker.Length;
        bytes[signatureIndex] = bytes[signatureIndex] == (byte)'A' ? (byte)'B' : (byte)'A';
        var artifactPath = Path.Combine(directory.Path, "example.tampered.daxq");
        File.WriteAllBytes(artifactPath, bytes);

        var exception = Record.Exception(() => ReferenceEngine().LoadStrategies(artifactPath));
        Assert.True(exception is InvalidDataException or CryptographicException, exception?.ToString());
    }

    [Fact]
    public async Task Reference_runtime_tick_path_is_allocation_free_after_warmup_and_disposes_with_attribution()
    {
        const string source = """
            using DaxAlgo.Sdk;

            public sealed class TickOnly : IBacktestStrategy
            {
                private double previous;

                public void OnTick(
                    IStrategyContext context,
                    double bid,
                    double ask,
                    double last,
                    double volume)
                {
                    previous = last + volume + bid - ask;
                }
            }
            """;
        using var directory = new TestDirectory();
        var artifact = Compile(source, "example.tick-only", ["ticks"]);
        var artifactPath = Path.Combine(directory.Path, "protected-tick-runtime.daxq");
        File.WriteAllBytes(artifactPath, artifact.Package.PackageBytes);
        var registration = Assert.Single(ReferenceEngine().LoadStrategies(artifactPath));
        var strategy = registration.BacktestStrategy.Build(Contract.UsStock("TEST"));
        var clock = new FixedClock();
        var router = new SignalSinkRouter();
        var tick = new Tick(clock.UtcNow, 99.5, 100.5, 7, 11);
        await strategy.OnStartAsync(clock, router, CancellationToken.None);
        await strategy.OnTickAsync(tick, clock, router, CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
            await strategy.OnTickAsync(tick, clock, router, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);

        ((IDisposable)strategy).Dispose();
        var exception = await Assert.ThrowsAsync<DaxqStrategyRuntimeException>(() =>
            strategy.OnTickAsync(tick, clock, router, CancellationToken.None));
        Assert.Equal("protected-tick-runtime", ((IPluginFaultAttribution)exception).PluginName);
    }

    private static DaxqCompilationArtifact Compile(
        string source,
        string strategyId,
        IReadOnlyList<string>? requirements = null)
    {
        using var signingKey = CreateDevelopmentSigningKey();
        return new DaxqCompiler().Compile(source, new DaxqCompilerOptions
        {
            StrategyId = strategyId,
            Version = "1.0.0",
            DataRequirements = requirements ?? ["bars"],
            DiversificationSeed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            ContentKeyId = $"dev:{strategyId}:1.0.0",
            ContentKey = SHA256.HashData("DAXQ-LOCAL-DEV-CONTENT-KEY"u8),
            Nonce = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray(),
            ReleaseKeyId = DaxqPackageReader.DevelopmentReleaseKeyId,
            ReleaseSigningKey = signingKey,
        });
    }

    private static DaxqProtectedStrategyEngine ReferenceEngine() => new(
        NullLogger<DaxqProtectedStrategyEngine>.Instance,
        new DaxqProtectedStrategyEngineOptions
        {
            ForceReferenceVm = true,
            EnableLocalDevelopmentProtocol = true,
            DeviceIdentityProvider = new EphemeralDeviceIdentityProvider(),
        });

    private static IReadOnlyList<StrategySignal> RunManaged(
        DaxqCompilationArtifact artifact,
        IReadOnlyList<Bar> bars)
    {
        var assembly = Assembly.Load(artifact.Lowering.ManagedAssembly);
        var type = Assert.Single(assembly.GetTypes(), candidate =>
            candidate is { IsClass: true, IsAbstract: false } &&
            typeof(AuthoringStrategy).IsAssignableFrom(candidate));
        var strategy = Assert.IsAssignableFrom<AuthoringStrategy>(Activator.CreateInstance(type));
        var context = new ManagedContext();
        strategy.Initialize(context);
        foreach (var bar in bars)
        {
            context.Append(bar);
            strategy.OnBar(context);
        }
        return context.Signals;
    }

    private static async Task<IReadOnlyList<StrategySignal>> RunProtectedAsync(
        BacktestStrategyOption option,
        IReadOnlyList<Bar> bars)
    {
        var id = new InstrumentId(1);
        var contract = Contract.UsStock("TEST");
        var events = bars.Select(bar => MarketEvent.OfBar(
            id,
            OhlcvBar.FromBar(bar, id, BarSize.OneMinute, BrokerKind.Simulated, isFinal: true)));
        var spec = new RunSpec(
            Universe.Single(new InstrumentSpec(id, contract, TickSize: 0.01, ContractMultiplier: 1d)),
            new DataSpec());
        var report = await new BacktestEngine(new InMemoryMarketDataFeed(events)).RunAsync(
            spec,
            new BacktestStrategyKernelAdapter(option.Build(contract)));
        return report.Signals?.Select(sample => sample.Signal).ToArray() ?? [];
    }

    private static ECDsa CreateDevelopmentSigningKey()
    {
        var d = new byte[32];
        d[^1] = 1;
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = d,
            Q = new ECPoint
            {
                X = Convert.FromHexString(
                    "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"),
                Y = Convert.FromHexString(
                    "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5"),
            },
        });
    }

    private static int IndexOf(byte[] source, byte[] value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.AsSpan(index, value.Length).SequenceEqual(value))
                return index;
        }
        return -1;
    }

    private sealed class ManagedContext : AuthoringContext
    {
        private readonly List<Bar> _bars = [];

        public List<StrategySignal> Signals { get; } = [];

        public void Append(Bar bar) => _bars.Add(bar);

        public double Indicator(Ind indicator, long period, BarField sourceField = BarField.Close) => 0d;

        public void Emit(SignalKind kind, double strength, long noteId = 0) =>
            Signals.Add(new StrategySignal((StrategySignalKind)(long)kind, strength, noteId));

        public double Param(long parameterId) => 0d;

        public double Bar(BarField field, long lookback = 0)
        {
            var bar = _bars[^checked((int)lookback + 1)];
            return field switch
            {
                BarField.Open => bar.Open,
                BarField.High => bar.High,
                BarField.Low => bar.Low,
                BarField.Close => bar.Close,
                BarField.Volume => bar.Volume,
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
        }

        public long TimeIndex() => _bars.Count - 1L;

        public double Random() => 0.5d;

        public void Log(long messageId, double value)
        {
        }
    }

    private sealed class SignalSinkRouter : IOrderRouter, IStrategySignalSink
    {
        public List<StrategySignal> Signals { get; } = [];

        public IObservable<OrderEvent> OrderEvents { get; } = new EmptyObservable<OrderEvent>();

        public Task EmitSignalAsync(StrategySignal signal, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Signals.Add(signal);
            return Task.CompletedTask;
        }

        public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("DAXQ signals must not be reinterpreted as orders.");

        public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyObservable<T> : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer) => EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingBacktestRegistry : IBacktestStrategyRegistry
    {
        private readonly List<BacktestStrategyOption> _options = [];

        public IReadOnlyList<BacktestStrategyOption> All => _options;

        public event EventHandler? Changed;

        public BacktestStrategyOption? Find(string id) =>
            _options.FirstOrDefault(option => option.Id == id);

        public void Register(BacktestStrategyOption option)
        {
            _options.RemoveAll(existing => existing.Id == option.Id);
            _options.Add(option);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool Remove(string id)
        {
            var removed = _options.RemoveAll(option => option.Id == id) > 0;
            if (removed) Changed?.Invoke(this, EventArgs.Empty);
            return removed;
        }
    }

    private sealed class RecordingStrategyFactory : IStrategyFactory
    {
        private readonly List<ITradingStrategy> _strategies = [];

        public IReadOnlyList<ITradingStrategy> All => _strategies;

        public event EventHandler<StrategyCatalogChange>? Changed;

        public StrategyHost Create(string strategyId) => throw new NotSupportedException();

        public void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration)
        {
            var replaced = _strategies.RemoveAll(existing => existing.Id == strategy.Id) > 0;
            _strategies.Add(strategy);
            Changed?.Invoke(this, new StrategyCatalogChange(strategy, replaced));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class EphemeralDeviceIdentityProvider : IDaxqDeviceIdentityProvider
    {
        private readonly DaxqDeviceIdentity _identity = new(
            Guid.NewGuid(),
            ECDsa.Create(ECCurve.NamedCurves.nistP256),
            nonExportable: false);

        public ValueTask<DaxqDeviceIdentity> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_identity);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(), "tmp", "daxq-host-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
