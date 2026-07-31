using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;

namespace DaxAlgo.Daxq.Host;

/// <summary>Runtime and licensing controls for the official protected-strategy host.</summary>
public sealed class DaxqProtectedStrategyEngineOptions
{
    /// <summary>Uses the managed reference VM even when the native ABI library is available.</summary>
    public bool ForceReferenceVm { get; set; }

    /// <summary>
    /// Absolute path to the protected native VM. Defaults to daxq_vm.dll on Windows and
    /// libdaxq_vm.dylib on macOS beside the app.
    /// </summary>
    public string? NativeLibraryPath { get; set; }

    /// <summary>Post-protection, post-signing SHA-256. Normally injected as release metadata.</summary>
    public string? ExpectedNativeLibrarySha256 { get; set; }

    /// <summary>SHA-256 thumbprint of the allowed Authenticode leaf certificate.</summary>
    public string? ExpectedNativePublisherCertificateSha256 { get; set; }

    internal IDaxqAuthenticodeVerifier? AuthenticodeVerifier { get; set; }

    /// <summary>Apple Developer Team identifier required on the signed macOS native VM.</summary>
    public string? ExpectedNativeMacTeamIdentifier { get; set; }

    internal IDaxqMacCodeSignatureVerifier? MacCodeSignatureVerifier { get; set; }

    /// <summary>
    /// Enables the in-process development entitlement adapter. This must be set explicitly by a
    /// Development/Testing composition; production remains fail-closed without a platform transport.
    /// </summary>
    public bool EnableLocalDevelopmentProtocol { get; set; }

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan OfflineGraceLimit { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan MaximumRevocationFeedAge { get; set; } = TimeSpan.FromHours(2);

    public string ExpectedIssuer { get; set; } = "daxalgo-platform-development";

    public string ExpectedAudience { get; set; } = "daxalgo-daxq-host";

    /// <summary>
    /// Last-good revocation state protected with Windows DPAPI or a macOS Keychain-held key.
    /// Null keeps only the current-process view.
    /// </summary>
    public string? RevocationStatePath { get; set; }

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public IDaxqDeliveryContextResolver? DeliveryContextResolver { get; set; }

    public IDaxqDeviceIdentityProvider? DeviceIdentityProvider { get; set; }

    public IDaxqLicensingTransport? LicensingTransport { get; set; }

    public DaxqEs256PublicKeyRing? ReleaseSigningKeys { get; set; }

    public DaxqEs256PublicKeyRing? LicensingSigningKeys { get; set; }
}

/// <summary>Official-installer implementation of the public protected-strategy seam.</summary>
public sealed class DaxqProtectedStrategyEngine : IProtectedStrategyEngine
{
    private readonly ILogger<DaxqProtectedStrategyEngine> _logger;
    private readonly DaxqProtectedStrategyEngineOptions _options;
    private readonly DaxqEs256PublicKeyRing _releaseTrust;
    private readonly DaxqLicensingRuntime _licensingRuntime;
    private readonly string? _nativeRuntimeFailure;

    public DaxqProtectedStrategyEngine(ILogger<DaxqProtectedStrategyEngine> logger)
        : this(logger, new DaxqProtectedStrategyEngineOptions())
    {
    }

    public DaxqProtectedStrategyEngine(
        ILogger<DaxqProtectedStrategyEngine> logger,
        DaxqProtectedStrategyEngineOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nativeRuntimeFailure = options.ForceReferenceVm
            ? null
            : DaxqNativeVmIntegrity.VerifyAndConfigure(options);
        var identityProvider = options.DeviceIdentityProvider ??
                               DaxqDeviceIdentityProvider.CreateDefault();
        IDaxqDeliveryContextResolver contextResolver;
        IDaxqLicensingTransport transport;
        DaxqEs256PublicKeyRing licensingTrust;
        if (options.EnableLocalDevelopmentProtocol)
        {
            if (options.DeliveryContextResolver is not null || options.LicensingTransport is not null ||
                options.LicensingSigningKeys is not null)
            {
                contextResolver = options.DeliveryContextResolver ??
                                  throw new InvalidOperationException(
                                      "A custom development context resolver is required with a custom transport.");
                transport = options.LicensingTransport ??
                            throw new InvalidOperationException(
                                "A custom development licensing transport is required with a custom resolver.");
                licensingTrust = options.LicensingSigningKeys ??
                                 throw new InvalidOperationException(
                                     "Development licensing verification keys are required with a custom transport.");
            }
            else
            {
                var development = new DaxqDevelopmentLicensing(
                    identityProvider,
                    options.TimeProvider,
                    offlineLeaseLifetime: options.OfflineGraceLimit);
                contextResolver = development;
                transport = development;
                licensingTrust = development.VerificationKeys;
            }
            _releaseTrust = options.ReleaseSigningKeys ??
                            DaxqEs256PublicKeyRing.DevelopmentReleaseKey();
        }
        else
        {
            contextResolver = options.DeliveryContextResolver ?? FailClosedLicensing.Instance;
            transport = options.LicensingTransport ?? FailClosedLicensing.Instance;
            licensingTrust = options.LicensingSigningKeys ?? new DaxqEs256PublicKeyRing();
            _releaseTrust = options.ReleaseSigningKeys ?? new DaxqEs256PublicKeyRing();
        }
        _licensingRuntime = new DaxqLicensingRuntime(
            contextResolver,
            identityProvider,
            transport,
            licensingTrust,
            options.TimeProvider,
            options.HeartbeatInterval,
            options.OfflineGraceLimit,
            options.MaximumRevocationFeedAge,
            options.ExpectedIssuer,
            options.ExpectedAudience,
            options.RevocationStatePath ?? (options.EnableLocalDevelopmentProtocol
                ? null
                : DefaultRevocationStatePath()),
            logger);
    }

    private static string DefaultRevocationStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        "daxq-revocations-v1.dat");

    public IReadOnlyList<ProtectedStrategyRegistration> LoadStrategies(string daxqPath)
    {
        var package = DaxqPackageReader.Read(daxqPath, _releaseTrust);
        var definition = new DaxqStrategyDefinition(
            package,
            _licensingRuntime,
            Path.GetFileNameWithoutExtension(daxqPath),
            _options.ForceReferenceVm,
            _nativeRuntimeFailure);
        var descriptor = new DaxqTradingStrategyDescriptor(definition);
        var option = new BacktestStrategyOption(
            descriptor.Id,
            descriptor.DisplayName,
            contract => definition.CreateKernel(contract))
        {
            Schema = definition.Schema,
            ParameterizedBuild = (contract, parameters) => definition.CreateKernel(contract, parameters),
            DataRequirement = definition.DataRequirement,
        };
        var factory = new StrategyFactoryRegistration(
            descriptor.Id,
            sp => sp.GetRequiredService<IAuthoredStrategyViewComposer>().ComposeView(descriptor),
            sp => ActivatorUtilities.CreateInstance<DaxqLiveSignalStrategyViewModel>(sp, definition));

        _logger.LogInformation(
            "Loaded protected DAXQ strategy {StrategyId} version {Version}",
            package.Manifest.StrategyId,
            package.Manifest.Version);
        return [new ProtectedStrategyRegistration(descriptor, option, factory)];
    }
}

internal sealed class FailClosedLicensing :
    IDaxqDeliveryContextResolver,
    IDaxqLicensingTransport
{
    public static FailClosedLicensing Instance { get; } = new();

    public ValueTask<DaxqDeliveryContext> ResolveAsync(
        string strategyId,
        string version,
        string contentKeyId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DaxqDeliveryContext>(NotConfigured());

    public ValueTask<DaxqChallengeResponse> CreateChallengeAsync(
        DaxqDeliveryContext context,
        DaxqChallengeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DaxqChallengeResponse>(NotConfigured());

    public ValueTask<DaxqContentKeyResponse> ReleaseContentKeyAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqContentKeyRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DaxqContentKeyResponse>(NotConfigured());

    public ValueTask<DaxqHeartbeatResponse> HeartbeatAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqHeartbeatRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DaxqHeartbeatResponse>(NotConfigured());

    public ValueTask<DaxqSignedEnvelope> GetRevocationsAsync(
        DaxqDeliveryContext context,
        long afterSequence,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DaxqSignedEnvelope>(NotConfigured());

    private static InvalidOperationException NotConfigured() => new(
        "DAXQ platform licensing is not configured. Local development licensing must be enabled explicitly.");
}

public static class DaxqHostServiceCollectionExtensions
{
    /// <summary>Lights up protected DAXQ discovery in the official Pro composition.</summary>
    public static IServiceCollection AddDaxqProtectedStrategyEngine(
        this IServiceCollection services,
        Action<DaxqProtectedStrategyEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new DaxqProtectedStrategyEngineOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IProtectedStrategyEngine, DaxqProtectedStrategyEngine>();
        return services;
    }
}
