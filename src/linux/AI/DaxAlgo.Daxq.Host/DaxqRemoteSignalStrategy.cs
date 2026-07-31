using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.UI;

namespace DaxAlgo.Daxq.Host;

/// <summary>
/// Buyer-visible metadata for one licensed Tier-3 release. Discovery is deliberately external to
/// this host: the marketplace caller must supply these ids after authenticating its licensed-release
/// feed. No artifact path or strategy bytes are accepted by this registration API.
/// </summary>
public sealed record DaxqSignalStrategyMetadata
{
    public DaxqSignalStrategyMetadata(
        string strategyId,
        string displayName,
        string description,
        string version,
        Guid licenseId,
        Guid releaseId,
        StrategyDataRequirement dataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars,
        string? linkUrl = null)
    {
        StrategyId = Required(strategyId, nameof(strategyId), 200);
        DisplayName = Required(displayName, nameof(displayName), 200);
        Description = Required(description, nameof(description), 2_000);
        Version = Required(version, nameof(version), 128);
        if (licenseId == Guid.Empty) throw new ArgumentException("A marketplace license id is required.", nameof(licenseId));
        if (releaseId == Guid.Empty) throw new ArgumentException("A marketplace release id is required.", nameof(releaseId));
        var knownRequirements = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars |
                                StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;
        if ((dataRequirement & ~knownRequirements) != 0)
            throw new ArgumentOutOfRangeException(nameof(dataRequirement));
        if (linkUrl is not null &&
            (!Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A signal-strategy link must be an absolute HTTPS URL.", nameof(linkUrl));
        }

        LicenseId = licenseId;
        ReleaseId = releaseId;
        DataRequirement = dataRequirement;
        LinkUrl = linkUrl;
    }

    public string StrategyId { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Version { get; }

    public Guid LicenseId { get; }

    public Guid ReleaseId { get; }

    public StrategyDataRequirement DataRequirement { get; }

    public string? LinkUrl { get; }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximumLength)
            throw new ArgumentException($"{parameterName} is required and must be at most {maximumLength} characters.", parameterName);
        return normalized;
    }
}

/// <summary>
/// Supplies a fresh user bearer for a previously discovered licensed release. The default used by
/// <see cref="DaxqSignalStrategyRegistrationService"/> fails closed until the account client wires a
/// provider; access tokens never live in catalog metadata.
/// </summary>
public interface IDaxqSignalSessionContextResolver
{
    ValueTask<DaxqDeliveryContext> ResolveAsync(
        DaxqSignalStrategyMetadata strategy,
        CancellationToken cancellationToken);
}

/// <summary>
/// Registers a remote-only release into the normal Pro strategy catalog. This service intentionally
/// does not discover licenses itself: the authenticated marketplace account client remains the source
/// of truth and calls <see cref="Register"/> with its licensed-strategy metadata.
/// </summary>
public sealed class DaxqSignalStrategyRegistrationService
{
    private readonly IStrategyFactory _catalog;
    private readonly IDaxqSignalSessionClient _client;
    private readonly IDaxqSignalSessionContextResolver _contextResolver;

    public DaxqSignalStrategyRegistrationService(
        IStrategyFactory catalog,
        IDaxqSignalSessionClient client,
        IDaxqSignalSessionContextResolver? contextResolver = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _contextResolver = contextResolver ?? FailClosedSignalSessionContextResolver.Instance;
    }

    public ITradingStrategy Register(DaxqSignalStrategyMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var descriptor = new DaxqRemoteTradingStrategyDescriptor(metadata);
        var registration = new StrategyFactoryRegistration(
            descriptor.Id,
            services => services.GetRequiredService<IAuthoredStrategyViewComposer>().ComposeView(descriptor),
            services => ActivatorUtilities.CreateInstance<DaxqRemoteSignalStrategyViewModel>(
                services,
                metadata,
                _client,
                _contextResolver));
        _catalog.Register(descriptor, registration);
        return descriptor;
    }
}

internal sealed class DaxqRemoteTradingStrategyDescriptor(DaxqSignalStrategyMetadata metadata)
    : ITradingStrategy
{
    public string Id => metadata.StrategyId;

    public string? BacktestStrategyId => null;

    public string DisplayName => metadata.DisplayName;

    public string Description =>
        $"Tier-3 server signal (opt-in; network latency applies). {metadata.Description}";

    public StrategyDataRequirement DataRequirement => metadata.DataRequirement;

    public string? LinkUrl => metadata.LinkUrl;
}

internal sealed class DaxqRemoteSignalStrategyViewModel : LiveSignalStrategyViewModelBase
{
    private readonly DaxqSignalStrategyMetadata _metadata;
    private readonly IDaxqSignalSessionClient _client;
    private readonly IDaxqSignalSessionContextResolver _contextResolver;

    public DaxqRemoteSignalStrategyViewModel(
        DaxqSignalStrategyMetadata metadata,
        IDaxqSignalSessionClient client,
        IDaxqSignalSessionContextResolver contextResolver,
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<DaxqRemoteSignalStrategyViewModel> logger)
        : base(
            metadata.StrategyId,
            metadata.DisplayName,
            services,
            notifications,
            clock,
            routerFactory,
            logger)
    {
        _metadata = metadata;
        _client = client;
        _contextResolver = contextResolver;
    }

    protected override StrategyDataRequirement DataRequirement => _metadata.DataRequirement;

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new DaxqRemoteSignalStrategy(
            _metadata,
            _client,
            _contextResolver,
            OnRemoteTerminated);

    private void OnRemoteTerminated(DaxqSignalSessionTermination termination) =>
        _ = StopAfterRemoteTerminationAsync(termination);

    private async Task StopAfterRemoteTerminationAsync(DaxqSignalSessionTermination termination)
    {
        // The receive loop calls us immediately before its Task completes. Yield first so StopAsync's
        // strategy disposal can await that Task without forming a receive-loop/UI-command deadlock.
        await Task.Yield();
        await UiThread.RunAsync(async () =>
        {
            await StopCommand.ExecuteAsync(null);
            Status = termination.CloseStatus == System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation
                ? "Signal session revoked by the server."
                : $"Signal session ended{FormatReason(termination.Reason)}.";
        });
    }

    private static string FormatReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason.Trim()}";
}

internal sealed class DaxqRemoteSignalStrategy : IBacktestStrategy, IAsyncDisposable
{
    private readonly DaxqSignalStrategyMetadata _metadata;
    private readonly IDaxqSignalSessionClient _client;
    private readonly IDaxqSignalSessionContextResolver _contextResolver;
    private readonly Action<DaxqSignalSessionTermination> _onTerminated;
    private CancellationTokenSource? _lifetime;
    private DaxqSignalSession? _session;
    private Task? _receiveLoop;
    private int _startState;
    private int _expectedStop;

    public DaxqRemoteSignalStrategy(
        DaxqSignalStrategyMetadata metadata,
        IDaxqSignalSessionClient client,
        IDaxqSignalSessionContextResolver contextResolver,
        Action<DaxqSignalSessionTermination> onTerminated)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _onTerminated = onTerminated ?? throw new ArgumentNullException(nameof(onTerminated));
    }

    public async Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _startState, 1, 0) != 0)
            throw new InvalidOperationException("A remote signal strategy cannot be started twice.");
        if (router is not IStrategySignalSink sink)
            throw new InvalidOperationException("The active strategy host cannot render remote DAXQ signals.");

        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _lifetime = lifetime;
        try
        {
            var context = await _contextResolver.ResolveAsync(_metadata, lifetime.Token).ConfigureAwait(false);
            ValidateContext(context);
            var session = await _client.OpenAsync(context, lifetime.Token).ConfigureAwait(false);
            _session = session;
            _receiveLoop = ReceiveLoopAsync(session, sink, lifetime.Token);
        }
        catch
        {
            _lifetime = null;
            lifetime.Dispose();
            Volatile.Write(ref _startState, 0);
            throw;
        }
    }

    public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnBarAsync(Bar bar, IClock clock, IOrderRouter router, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => StopAsync();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReceiveLoopAsync(
        DaxqSignalSession session,
        IStrategySignalSink sink,
        CancellationToken cancellationToken)
    {
        DaxqSignalSessionTermination? termination = null;
        try
        {
            termination = await session.ReceiveAsync(
                    (signal, token) => new ValueTask(UiThread.RunAsync(
                        () => sink.EmitSignalAsync(signal.Signal, token))),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            termination = new DaxqSignalSessionTermination(
                CloseStatus: null,
                Reason: exception.Message,
                RemoteClose: false);
        }

        if (termination is not null && Volatile.Read(ref _expectedStop) == 0)
            _onTerminated(termination);
    }

    private async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _expectedStop, 1) != 0) return;
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        var session = Interlocked.Exchange(ref _session, null);
        var receiveLoop = Interlocked.Exchange(ref _receiveLoop, null);
        try { lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        if (receiveLoop is not null)
        {
            try { await receiveLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lifetime?.Dispose();
    }

    private void ValidateContext(DaxqDeliveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.LicenseId != _metadata.LicenseId || context.ReleaseId != _metadata.ReleaseId)
        {
            throw new DaxqLicenseDeniedException(
                "The authenticated signal-session context does not match the catalog license and release.");
        }
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new DaxqLicenseDeniedException("The signal-session context omitted its user bearer.");
    }
}

internal sealed class FailClosedSignalSessionContextResolver : IDaxqSignalSessionContextResolver
{
    public static FailClosedSignalSessionContextResolver Instance { get; } = new();

    public ValueTask<DaxqDeliveryContext> ResolveAsync(
        DaxqSignalStrategyMetadata strategy,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<DaxqDeliveryContext>(new InvalidOperationException(
            "Tier-3 signal-session discovery/authentication is not configured. " +
            "An authenticated licensed-strategy context resolver is required."));
}
