using System.Diagnostics;
using System.Security.Cryptography;
using DaxAlgo.Daxq.Compiler;
using DaxAlgo.Daxq.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Plugins;

namespace DaxAlgo.Daxq.Host.Tests;

public sealed class DaxqLicensingTests
{
    private const string TickSource = """
        using DaxAlgo.Sdk;

        public sealed class LicensedTickStrategy : IBacktestStrategy
        {
            private double state;

            public void OnTick(
                IStrategyContext context,
                double bid,
                double ask,
                double last,
                double volume)
            {
                state = bid + ask + last + volume;
            }
        }
        """;

    private const string InitializationSignalSource = """
        using DaxAlgo.Sdk;

        public sealed class InitializationSignalStrategy : IBacktestStrategy
        {
            public void Initialize(IStrategyContext context)
            {
                context.Emit(SignalKind.Long, 1.0, 1);
            }

            public void OnTick(
                IStrategyContext context,
                double bid,
                double ask,
                double last,
                double volume)
            {
            }
        }
        """;

    [DesktopSecurityFact]
    public async Task Software_device_key_is_platform_protected_and_stable_across_restart()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "device.json");
        Guid firstId;
        string firstFingerprint;
        using (var provider = new DaxqDeviceIdentityProvider(path, forceSoftwareKey: true))
        {
            var identity = await provider.GetAsync(CancellationToken.None);
            firstId = identity.DeviceId;
            firstFingerprint = identity.Registration.FingerprintSha256;
            Assert.False(identity.NonExportable);
        }

        var stored = File.ReadAllText(path);
        var expectedMode = OperatingSystem.IsMacOS() ? "keychain" : "dpapi";
        Assert.Contains($"\"mode\":\"{expectedMode}\"", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", stored, StringComparison.Ordinal);
        using var reopened = new DaxqDeviceIdentityProvider(path, forceSoftwareKey: true);
        var second = await reopened.GetAsync(CancellationToken.None);
        Assert.Equal(firstId, second.DeviceId);
        Assert.Equal(firstFingerprint, second.Registration.FingerprintSha256);
    }

    [Fact]
    public async Task Revocation_denial_stops_callbacks_within_next_heartbeat()
    {
        using var harness = new LicensingHarness(
            heartbeat: TimeSpan.FromMilliseconds(40),
            runToken: TimeSpan.FromSeconds(2),
            offlineLease: TimeSpan.FromSeconds(2));
        var strategy = harness.BuildStrategy("example.bounded-revocation");
        await strategy.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        var started = Stopwatch.StartNew();

        harness.Licensing.RevokeEntitlement();
        var exception = await WaitForLicenseStopAsync(strategy, TimeSpan.FromSeconds(2));

        Assert.Contains("revoked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"Stop took {started.Elapsed}.");
        ((IDisposable)strategy).Dispose();
    }

    [Fact]
    public async Task Network_outage_uses_only_absolute_signed_offline_lease()
    {
        using var harness = new LicensingHarness(
            heartbeat: TimeSpan.FromMilliseconds(35),
            runToken: TimeSpan.FromMilliseconds(180),
            offlineLease: TimeSpan.FromMilliseconds(450));
        var strategy = harness.BuildStrategy("example.offline-grace");
        await strategy.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        harness.Licensing.SetUnavailable(true);

        await Task.Delay(250);
        await strategy.OnTickAsync(TestTick, TestClock.Instance, TestRouter.Instance, CancellationToken.None);

        var exception = await WaitForLicenseStopAsync(strategy, TimeSpan.FromSeconds(2));
        Assert.Contains("offline", exception.Message, StringComparison.OrdinalIgnoreCase);
        ((IDisposable)strategy).Dispose();
    }

    [Fact]
    public async Task Hung_heartbeat_cannot_extend_the_signed_offline_lease()
    {
        using var harness = new LicensingHarness(
            heartbeat: TimeSpan.FromMilliseconds(35),
            runToken: TimeSpan.FromMilliseconds(180),
            offlineLease: TimeSpan.FromMilliseconds(450),
            hangHeartbeat: true);
        var strategy = harness.BuildStrategy("example.hung-heartbeat");
        await strategy.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        var started = Stopwatch.StartNew();

        var exception = await WaitForLicenseStopAsync(strategy, TimeSpan.FromSeconds(2));

        Assert.Contains("offline", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"Stop took {started.Elapsed}.");
        ((IDisposable)strategy).Dispose();
    }

    [Fact]
    public async Task Market_data_hot_path_never_calls_licensing_transport()
    {
        using var harness = new LicensingHarness(
            heartbeat: TimeSpan.FromHours(1),
            runToken: TimeSpan.FromMinutes(30),
            offlineLease: TimeSpan.FromHours(2));
        var strategy = harness.BuildStrategy("example.no-hot-path-network");
        await strategy.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        var before = harness.TotalTransportCalls;

        for (var index = 0; index < 2_000; index++)
            await strategy.OnTickAsync(TestTick, TestClock.Instance, TestRouter.Instance, CancellationToken.None);

        Assert.Equal(before, harness.TotalTransportCalls);
        ((IDisposable)strategy).Dispose();
    }

    [DesktopSecurityFact]
    public async Task Content_key_and_plaintext_are_never_persisted()
    {
        using var directory = new TestDirectory();
        var devicePath = Path.Combine(directory.Path, "device.json");
        using var provider = new DaxqDeviceIdentityProvider(devicePath, forceSoftwareKey: true);
        using var licensing = new DaxqDevelopmentLicensing(
            provider,
            TimeProvider.System,
            runTokenLifetime: TimeSpan.FromMinutes(5),
            offlineLeaseLifetime: TimeSpan.FromMinutes(5));
        var engine = CreateEngine(
            provider,
            licensing,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));
        var packagePath = WritePackage(directory.Path, "example.memory-only");
        var strategy = Assert.Single(engine.LoadStrategies(packagePath))
            .BacktestStrategy.Build(Contract.UsStock("TEST"));

        await strategy.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        await strategy.OnTickAsync(TestTick, TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        ((IDisposable)strategy).Dispose();

        var contentKey = SHA256.HashData("DAXQ-LOCAL-DEV-CONTENT-KEY"u8);
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories))
                Assert.Equal(-1, IndexOf(File.ReadAllBytes(file), contentKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }

    [Fact]
    public async Task Signed_revocation_feed_rollback_is_rejected()
    {
        using var harness = new LicensingHarness(
            heartbeat: TimeSpan.FromHours(1),
            runToken: TimeSpan.FromMinutes(30),
            offlineLease: TimeSpan.FromHours(2));
        var registration = harness.Load("example.feed-rollback");
        var first = registration.BacktestStrategy.Build(Contract.UsStock("TEST"));
        await first.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        ((IDisposable)first).Dispose();

        harness.Licensing.PublishRevocation(
            "release",
            Guid.NewGuid().ToString("D"),
            "unrelated release");
        var second = registration.BacktestStrategy.Build(Contract.UsStock("TEST"));
        await second.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        ((IDisposable)second).Dispose();

        harness.Licensing.ForceRollback(0);
        var third = registration.BacktestStrategy.Build(Contract.UsStock("TEST"));
        var exception = await Assert.ThrowsAsync<DaxqStrategyRuntimeException>(() =>
            third.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None));
        Assert.IsType<CryptographicException>(exception.InnerException);
        Assert.Contains("rollback", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        ((IDisposable)third).Dispose();
    }

    [DesktopSecurityFact]
    public async Task Last_good_revocation_cursor_survives_process_runtime_restart()
    {
        using var directory = new TestDirectory();
        using var identity = new TestDeviceIdentityProvider();
        using var licensing = new DaxqDevelopmentLicensing(
            identity,
            TimeProvider.System,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1));
        var statePath = Path.Combine(directory.Path, "revocations.dat");
        var packagePath = WritePackage(directory.Path, "example.persisted-feed");
        licensing.PublishRevocation("release", Guid.NewGuid().ToString("D"), "unrelated release");
        var firstEngine = CreateEngine(
            identity,
            licensing,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            statePath);
        var first = Assert.Single(firstEngine.LoadStrategies(packagePath))
            .BacktestStrategy.Build(Contract.UsStock("TEST"));
        await first.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None);
        ((IDisposable)first).Dispose();
        Assert.True(File.Exists(statePath));

        licensing.ForceRollback(0);
        var restartedEngine = CreateEngine(
            identity,
            licensing,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
            statePath);
        var restarted = Assert.Single(restartedEngine.LoadStrategies(packagePath))
            .BacktestStrategy.Build(Contract.UsStock("TEST"));
        var exception = await Assert.ThrowsAsync<DaxqStrategyRuntimeException>(() =>
            restarted.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None));
        Assert.Contains("rollback", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        ((IDisposable)restarted).Dispose();
    }

    [Fact]
    public async Task Failed_initialization_disposes_licensing_heartbeat()
    {
        using var directory = new TestDirectory();
        using var identity = new TestDeviceIdentityProvider();
        using var licensing = new DaxqDevelopmentLicensing(
            identity,
            TimeProvider.System,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
        var engine = CreateEngine(
            identity,
            licensing,
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromSeconds(2));
        var packagePath = WritePackage(
            directory.Path,
            "example.failed-initialize",
            source: InitializationSignalSource);
        var strategy = Assert.Single(engine.LoadStrategies(packagePath))
            .BacktestStrategy.Build(Contract.UsStock("TEST"));

        await Assert.ThrowsAsync<DaxqStrategyRuntimeException>(() =>
            strategy.OnStartAsync(TestClock.Instance, TestRouter.Instance, CancellationToken.None));
        var callsAfterFailure = licensing.HeartbeatCalls;
        await Task.Delay(150);

        Assert.Equal(callsAfterFailure, licensing.HeartbeatCalls);
        ((IDisposable)strategy).Dispose();
    }

    [Fact]
    public void Production_content_and_release_key_ids_are_not_hard_coded_to_development()
    {
        using var directory = new TestDirectory();
        using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string releaseKeyId = "marketplace-release-key-2026-07";
        const string contentKeyId = "ck_marketplace_example_v1";
        var packagePath = WritePackage(
            directory.Path,
            "example.production-ids",
            contentKeyId,
            releaseKeyId,
            releaseKey);
        var releaseTrust = new DaxqEs256PublicKeyRing().Add(
            releaseKeyId,
            releaseKey.ExportSubjectPublicKeyInfo());

        var package = DaxqPackageReader.Read(packagePath, releaseTrust);

        Assert.Equal(contentKeyId, package.Manifest.Protection.ContentKeyId);
        Assert.Equal(releaseKeyId, package.ReleaseSigningKeyId);
    }

    private static readonly Tick TestTick = new(
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        99.5,
        100.5,
        7,
        11);

    private static async Task<DaxqStrategyRuntimeException> WaitForLicenseStopAsync(
        IBacktestStrategy strategy,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var exception = await Record.ExceptionAsync(() =>
                strategy.OnTickAsync(TestTick, TestClock.Instance, TestRouter.Instance, CancellationToken.None));
            if (exception is DaxqStrategyRuntimeException stopped)
                return stopped;
            Assert.Null(exception);
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException("The licensed strategy did not stop before the test timeout.");
    }

    private static DaxqProtectedStrategyEngine CreateEngine(
        IDaxqDeviceIdentityProvider provider,
        DaxqDevelopmentLicensing licensing,
        TimeSpan heartbeat,
        TimeSpan offlineGrace,
        string? revocationStatePath = null,
        IDaxqLicensingTransport? transport = null) => new(
        NullLogger<DaxqProtectedStrategyEngine>.Instance,
        new DaxqProtectedStrategyEngineOptions
        {
            ForceReferenceVm = true,
            EnableLocalDevelopmentProtocol = true,
            HeartbeatInterval = heartbeat,
            OfflineGraceLimit = offlineGrace,
            DeviceIdentityProvider = provider,
            DeliveryContextResolver = licensing,
            LicensingTransport = transport ?? licensing,
            LicensingSigningKeys = licensing.VerificationKeys,
            ReleaseSigningKeys = DaxqEs256PublicKeyRing.DevelopmentReleaseKey(),
            RevocationStatePath = revocationStatePath,
        });

    private static string WritePackage(
        string directory,
        string strategyId,
        string? contentKeyId = null,
        string? releaseKeyId = null,
        ECDsa? releaseKey = null,
        string? source = null)
    {
        var ownsKey = releaseKey is null;
        releaseKey ??= CreateDevelopmentSigningKey();
        try
        {
            var artifact = new DaxqCompiler().Compile(source ?? TickSource, new DaxqCompilerOptions
            {
                StrategyId = strategyId,
                Version = "1.0.0",
                DataRequirements = ["ticks"],
                DiversificationSeed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
                ContentKeyId = contentKeyId ?? $"dev:{strategyId}:1.0.0",
                ContentKey = SHA256.HashData("DAXQ-LOCAL-DEV-CONTENT-KEY"u8),
                Nonce = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray(),
                ReleaseKeyId = releaseKeyId ?? DaxqPackageReader.DevelopmentReleaseKeyId,
                ReleaseSigningKey = releaseKey,
            });
            var path = Path.Combine(directory, strategyId + ".daxq");
            File.WriteAllBytes(path, artifact.Package.PackageBytes);
            return path;
        }
        finally
        {
            if (ownsKey) releaseKey.Dispose();
        }
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

    private sealed class LicensingHarness : IDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly TestDeviceIdentityProvider _identity = new();
        private readonly DaxqProtectedStrategyEngine _engine;
        private readonly IDisposable? _transportLifetime;

        public LicensingHarness(
            TimeSpan heartbeat,
            TimeSpan runToken,
            TimeSpan offlineLease,
            bool hangHeartbeat = false)
        {
            Licensing = new DaxqDevelopmentLicensing(
                _identity,
                TimeProvider.System,
                runToken,
                offlineLease);
            IDaxqLicensingTransport transport = Licensing;
            if (hangHeartbeat)
            {
                var hanging = new HangingHeartbeatTransport(Licensing);
                _transportLifetime = hanging;
                transport = hanging;
            }
            _engine = CreateEngine(
                _identity,
                Licensing,
                heartbeat,
                offlineLease,
                transport: transport);
        }

        public DaxqDevelopmentLicensing Licensing { get; }

        public int TotalTransportCalls => Licensing.ChallengeCalls + Licensing.KeyReleaseCalls +
                                          Licensing.HeartbeatCalls + Licensing.FeedCalls;

        public ProtectedStrategyRegistration Load(string strategyId)
        {
            var path = WritePackage(_directory.Path, strategyId);
            return Assert.Single(_engine.LoadStrategies(path));
        }

        public IBacktestStrategy BuildStrategy(string strategyId) =>
            Load(strategyId).BacktestStrategy.Build(Contract.UsStock("TEST"));

        public void Dispose()
        {
            _transportLifetime?.Dispose();
            Licensing.Dispose();
            _identity.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class HangingHeartbeatTransport(
        IDaxqLicensingTransport inner) : IDaxqLicensingTransport, IDisposable
    {
        private readonly TaskCompletionSource<DaxqHeartbeatResponse> _pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<DaxqChallengeResponse> CreateChallengeAsync(
            DaxqDeliveryContext context,
            DaxqChallengeRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            inner.CreateChallengeAsync(context, request, idempotencyKey, cancellationToken);

        public ValueTask<DaxqContentKeyResponse> ReleaseContentKeyAsync(
            DaxqDeliveryContext context,
            Guid licenseId,
            DaxqContentKeyRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            inner.ReleaseContentKeyAsync(context, licenseId, request, idempotencyKey, cancellationToken);

        public ValueTask<DaxqHeartbeatResponse> HeartbeatAsync(
            DaxqDeliveryContext context,
            Guid licenseId,
            DaxqHeartbeatRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => new(_pending.Task);

        public ValueTask<DaxqSignedEnvelope> GetRevocationsAsync(
            DaxqDeliveryContext context,
            long afterSequence,
            CancellationToken cancellationToken) =>
            inner.GetRevocationsAsync(context, afterSequence, cancellationToken);

        public void Dispose() => _pending.TrySetCanceled();
    }

    private sealed class TestDeviceIdentityProvider : IDaxqDeviceIdentityProvider, IDisposable
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

        public void Dispose() => _identity.Dispose();
    }

    private sealed class TestClock : IClock
    {
        public static TestClock Instance { get; } = new();

        public DateTime UtcNow => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class TestRouter : IOrderRouter
    {
        public static TestRouter Instance { get; } = new();

        public IObservable<OrderEvent> OrderEvents { get; } = new EmptyObservable<OrderEvent>();

        public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

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

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(),
                "tmp",
                "daxq-licensing-tests",
                Guid.NewGuid().ToString("N"));
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
