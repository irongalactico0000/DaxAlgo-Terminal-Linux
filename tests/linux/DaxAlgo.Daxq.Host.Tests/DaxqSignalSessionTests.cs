using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace DaxAlgo.Daxq.Host.Tests;

public sealed class DaxqSignalSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Http_open_proves_device_and_streams_strict_signal_frames_without_reconnect()
    {
        using var identity = new TestIdentityProvider();
        var challenge = new DaxqChallengeResponse(
            Guid.NewGuid(),
            DaxqCryptography.Base64Url(RandomNumberGenerator.GetBytes(32)),
            Now.AddMinutes(1));
        var sessionId = Guid.NewGuid();
        var handler = new SignalSessionHttpHandler(challenge, sessionId, Now.AddMinutes(2));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://platform.test") };
        var socket = new ScriptedSignalSocket(
            Text("""
                 {"type":"signal","sequence":1,"emitted_at":"2026-07-27T10:00:01Z","kind":"long","strength":0.75,"note_id":9}
                 """),
            Text("""
                 {"type":"heartbeat","sequence":2,"sent_at":"2026-07-27T10:00:02Z"}
                 """),
            Close(WebSocketCloseStatus.PolicyViolation, "license_revoked"));
        var sockets = new SingleSocketFactory(socket);
        var client = new DaxqSignalSessionClient(
            new HttpDaxqLicensingTransport(http),
            new HttpDaxqSignalSessionTransport(http),
            identity,
            sockets,
            Options(allowInsecureLoopback: true));
        var context = Context();

        await using var session = await client.OpenAsync(context);
        var emitted = new List<StrategySignalEvent>();
        var termination = await session.ReceiveAsync((signal, _) =>
        {
            emitted.Add(signal);
            return ValueTask.CompletedTask;
        });

        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, termination.CloseStatus);
        Assert.Equal("license_revoked", termination.Reason);
        Assert.True(termination.RemoteClose);
        var signal = Assert.Single(emitted);
        Assert.Equal(StrategySignalKind.Long, signal.Signal.Kind);
        Assert.Equal(0.75d, signal.Signal.Strength);
        Assert.Equal(9, signal.Signal.NoteId);
        Assert.Equal(new DateTime(2026, 7, 27, 10, 0, 1, DateTimeKind.Utc), signal.TimestampUtc);
        Assert.Equal(1, sockets.CreateCalls);
        Assert.Equal(1, socket.ConnectCalls);
        Assert.Equal("opaque-session-token", socket.SessionToken);
        Assert.Equal(new Uri("ws://127.0.0.1:5080/v1/signals/" + sessionId), socket.Uri);

        var challengeRequest = Assert.Single(
            handler.Requests,
            item => item.Path == "/v1/devices/challenges");
        var openRequest = Assert.Single(
            handler.Requests,
            item => item.Path == $"/v1/marketplace/licenses/{context.LicenseId:D}/signal-sessions");
        Assert.Equal("Bearer access-token", challengeRequest.Authorization);
        Assert.Equal("Bearer access-token", openRequest.Authorization);
        Assert.False(string.IsNullOrWhiteSpace(challengeRequest.IdempotencyKey));
        Assert.Equal(challengeRequest.IdempotencyKey, openRequest.IdempotencyKey);

        using var challengeJson = JsonDocument.Parse(challengeRequest.Body);
        using var openJson = JsonDocument.Parse(openRequest.Body);
        var binding = challengeJson.RootElement.GetProperty("binding_sha256").GetString()!;
        Assert.Equal(DaxqCryptography.SignalSessionOperation,
            challengeJson.RootElement.GetProperty("operation").GetString());
        var clientNonce = DaxqCryptography.DecodeBase64Url(
            openJson.RootElement.GetProperty("client_session_nonce").GetString());
        try
        {
            Assert.Equal(binding, DaxqCryptography.Sha256Hex(clientNonce));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clientNonce);
        }

        var proof = DaxqCryptography.BuildDeviceProof(
            DaxqCryptography.SignalSessionOperation,
            challenge,
            context,
            identity.Identity.DeviceId,
            binding,
            openRequest.IdempotencyKey!);
        var signature = DaxqCryptography.DecodeBase64Url(
            openJson.RootElement.GetProperty("device_signature").GetString(),
            expectedLength: 64);
        var publicKey = DaxqCryptography.DecodeBase64Url(identity.Identity.Registration.PublicKeySpki);
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var read);
            Assert.Equal(publicKey.Length, read);
            Assert.True(verifier.VerifyData(
                proof,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    [Fact]
    public async Task Non_loopback_plaintext_websocket_is_rejected_before_connect()
    {
        using var identity = new TestIdentityProvider();
        var licensing = new FakeLicensingTransport(Now);
        var transport = new FakeSignalSessionTransport(
            new DaxqSignalSessionOpenResponse(
                Guid.NewGuid(),
                "ws://signals.example.test/v1/signals/session",
                "opaque-session-token",
                Now.AddMinutes(2),
                10));
        var socket = new ScriptedSignalSocket();
        var sockets = new SingleSocketFactory(socket);
        var client = new DaxqSignalSessionClient(
            licensing,
            transport,
            identity,
            sockets,
            Options(allowInsecureLoopback: true));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await client.OpenAsync(Context()));

        Assert.Contains("require WSS", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, sockets.CreateCalls);
        Assert.Equal(0, socket.ConnectCalls);
    }

    [Fact]
    public async Task Reordered_frame_is_rejected_and_session_is_not_reconnected()
    {
        using var identity = new TestIdentityProvider();
        var socket = new ScriptedSignalSocket(Text("""
            {"type":"signal","sequence":2,"emitted_at":"2026-07-27T10:00:01Z","kind":"flat","strength":0.5,"note_id":0}
            """));
        var sockets = new SingleSocketFactory(socket);
        var client = Client(identity, sockets);
        await using var session = await client.OpenAsync(Context());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.ReceiveAsync((_, _) => ValueTask.CompletedTask));

        Assert.Contains("sequence", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, sockets.CreateCalls);
        Assert.Equal(1, socket.ConnectCalls);
    }

    [Fact]
    public async Task Remote_adapter_routes_sink_signal_and_stops_after_one_server_close()
    {
        using var identity = new TestIdentityProvider();
        var socket = new ScriptedSignalSocket(
            Text("""
                 {"type":"signal","sequence":1,"emitted_at":"2026-07-27T10:00:01Z","kind":"short","strength":1.0,"note_id":4}
                 """),
            Close(WebSocketCloseStatus.PolicyViolation, "license_revoked"));
        var sockets = new SingleSocketFactory(socket);
        var client = Client(identity, sockets);
        var metadata = Metadata();
        var router = new SignalRouterSpy();
        var terminated = new TaskCompletionSource<DaxqSignalSessionTermination>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var strategy = new DaxqRemoteSignalStrategy(
            metadata,
            client,
            new FixedContextResolver(Context()),
            value => terminated.TrySetResult(value));

        await strategy.OnStartAsync(TestClock.Instance, router, CancellationToken.None);
        var close = await terminated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var signal = Assert.Single(router.Signals);
        Assert.Equal(StrategySignalKind.Short, signal.Kind);
        Assert.Equal(1d, signal.Strength);
        Assert.Equal(4, signal.NoteId);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        await strategy.OnTickAsync(
            new Tick(Now.UtcDateTime, 100, 101, 1, 1),
            TestClock.Instance,
            router,
            CancellationToken.None);
        Assert.Equal(1, socket.ConnectCalls);
        Assert.Equal(1, sockets.CreateCalls);
    }

    [Fact]
    public async Task Remote_adapter_rejects_context_that_does_not_match_catalog_metadata()
    {
        var client = new SignalClientSpy();
        var metadata = Metadata();
        var wrong = Context() with { ReleaseId = Guid.NewGuid() };
        await using var strategy = new DaxqRemoteSignalStrategy(
            metadata,
            client,
            new FixedContextResolver(wrong),
            _ => { });

        await Assert.ThrowsAsync<DaxqLicenseDeniedException>(() =>
            strategy.OnStartAsync(TestClock.Instance, new SignalRouterSpy(), CancellationToken.None));

        Assert.Equal(0, client.OpenCalls);
    }

    [Fact]
    public void Registration_adds_remote_descriptor_to_normal_catalog_without_backtest_or_artifact()
    {
        var catalog = new CatalogSpy();
        var service = new DaxqSignalStrategyRegistrationService(catalog, new SignalClientSpy());
        var metadata = Metadata();

        var descriptor = service.Register(metadata);

        Assert.Same(descriptor, catalog.Strategy);
        Assert.Equal(metadata.StrategyId, catalog.Registration?.StrategyId);
        Assert.Null(descriptor.BacktestStrategyId);
        Assert.Equal(metadata.DisplayName, descriptor.DisplayName);
        Assert.StartsWith("Tier-3 server signal (opt-in", descriptor.Description, StringComparison.Ordinal);
        Assert.Equal(metadata.DataRequirement, descriptor.DataRequirement);
        Assert.Equal(metadata.LinkUrl, descriptor.LinkUrl);
    }

    private static DaxqSignalSessionClient Client(
        IDaxqDeviceIdentityProvider identity,
        IDaxqSignalSocketFactory sockets) =>
        new(
            new FakeLicensingTransport(Now),
            new FakeSignalSessionTransport(new DaxqSignalSessionOpenResponse(
                Guid.NewGuid(),
                "ws://127.0.0.1:5080/v1/signals/session",
                "opaque-session-token",
                Now.AddMinutes(2),
                10)),
            identity,
            sockets,
            Options(allowInsecureLoopback: true));

    private static DaxqSignalSessionClientOptions Options(bool allowInsecureLoopback) => new()
    {
        AllowInsecureLoopback = allowInsecureLoopback,
        TimeProvider = new FixedTimeProvider(Now),
    };

    private static DaxqDeliveryContext Context() => new(
        Guid.Parse("71000000-0000-0000-0000-000000000001"),
        Guid.Parse("72000000-0000-0000-0000-000000000001"),
        "access-token",
        Guid.Parse("73000000-0000-0000-0000-000000000001"));

    private static DaxqSignalStrategyMetadata Metadata() => new(
        "remote.signal.test",
        "Remote signal test",
        "Tier-3 server-side signal strategy.",
        "1.0.0",
        Context().LicenseId,
        Context().ReleaseId,
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars,
        "https://example.test/strategies/remote-signal");

    private static SocketFrame Text(string json) => new(
        WebSocketMessageType.Text,
        Encoding.UTF8.GetBytes(json.Trim()),
        null,
        null);

    private static SocketFrame Close(WebSocketCloseStatus status, string description) => new(
        WebSocketMessageType.Close,
        [],
        status,
        description);

    private sealed class SignalSessionHttpHandler(
        DaxqChallengeResponse challenge,
        Guid sessionId,
        DateTimeOffset expiresAt) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null,
                body));
            if (request.RequestUri.AbsolutePath == "/v1/devices/challenges")
            {
                return JsonResponse($$"""
                    {"challenge_id":"{{challenge.ChallengeId:D}}","nonce":"{{challenge.Nonce}}","expires_at":"{{challenge.ExpiresAt:O}}"}
                    """);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/signal-sessions", StringComparison.Ordinal))
            {
                return JsonResponse($$"""
                    {"session_id":"{{sessionId:D}}","websocket_url":"ws://127.0.0.1:5080/v1/signals/{{sessionId:D}}","session_token":"opaque-session-token","expires_at":"{{expiresAt:O}}","heartbeat_interval_seconds":10}
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record CapturedRequest(
        string Path,
        string? Authorization,
        string? IdempotencyKey,
        string Body);

    private sealed class FakeLicensingTransport(DateTimeOffset now) : IDaxqLicensingTransport
    {
        public ValueTask<DaxqChallengeResponse> CreateChallengeAsync(
            DaxqDeliveryContext context,
            DaxqChallengeRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DaxqChallengeResponse(
                Guid.NewGuid(),
                DaxqCryptography.Base64Url(RandomNumberGenerator.GetBytes(32)),
                now.AddMinutes(1)));

        public ValueTask<DaxqContentKeyResponse> ReleaseContentKeyAsync(
            DaxqDeliveryContext context,
            Guid licenseId,
            DaxqContentKeyRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DaxqHeartbeatResponse> HeartbeatAsync(
            DaxqDeliveryContext context,
            Guid licenseId,
            DaxqHeartbeatRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DaxqSignedEnvelope> GetRevocationsAsync(
            DaxqDeliveryContext context,
            long afterSequence,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeSignalSessionTransport(DaxqSignalSessionOpenResponse response)
        : IDaxqSignalSessionTransport
    {
        public ValueTask<DaxqSignalSessionOpenResponse> OpenAsync(
            DaxqDeliveryContext context,
            Guid licenseId,
            DaxqSignalSessionOpenRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);
    }

    private sealed class TestIdentityProvider : IDaxqDeviceIdentityProvider, IDisposable
    {
        public TestIdentityProvider() =>
            Identity = new DaxqDeviceIdentity(
                Guid.Parse("74000000-0000-0000-0000-000000000001"),
                ECDsa.Create(ECCurve.NamedCurves.nistP256),
                nonExportable: false);

        public DaxqDeviceIdentity Identity { get; }

        public ValueTask<DaxqDeviceIdentity> GetAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Identity);

        public void Dispose() => Identity.Dispose();
    }

    private sealed class SingleSocketFactory(IDaxqSignalSocket socket) : IDaxqSignalSocketFactory
    {
        public int CreateCalls { get; private set; }

        public IDaxqSignalSocket Create()
        {
            CreateCalls++;
            return socket;
        }
    }

    private sealed class ScriptedSignalSocket(params SocketFrame[] frames) : IDaxqSignalSocket
    {
        private readonly Queue<SocketFrame> _frames = new(frames);

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public int ConnectCalls { get; private set; }

        public Uri? Uri { get; private set; }

        public string? SessionToken { get; private set; }

        public ValueTask ConnectAsync(Uri uri, string sessionToken, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            Uri = uri;
            SessionToken = sessionToken;
            State = WebSocketState.Open;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<DaxqSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_frames.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
            var frame = _frames.Dequeue();
            if (frame.Payload.Length > buffer.Length)
                throw new InvalidOperationException("The test frame does not fit the supplied buffer.");
            frame.Payload.CopyTo(buffer);
            if (frame.MessageType == WebSocketMessageType.Close) State = WebSocketState.CloseReceived;
            return new DaxqSocketReceiveResult(
                frame.Payload.Length,
                EndOfMessage: true,
                frame.MessageType,
                frame.CloseStatus,
                frame.CloseDescription);
        }

        public ValueTask CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string closeDescription,
            CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SocketFrame(
        WebSocketMessageType MessageType,
        byte[] Payload,
        WebSocketCloseStatus? CloseStatus,
        string? CloseDescription);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedContextResolver(DaxqDeliveryContext context)
        : IDaxqSignalSessionContextResolver
    {
        public ValueTask<DaxqDeliveryContext> ResolveAsync(
            DaxqSignalStrategyMetadata strategy,
            CancellationToken cancellationToken) => ValueTask.FromResult(context);
    }

    private sealed class SignalClientSpy : IDaxqSignalSessionClient
    {
        public int OpenCalls { get; private set; }

        public ValueTask<DaxqSignalSession> OpenAsync(
            DaxqDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return ValueTask.FromException<DaxqSignalSession>(new InvalidOperationException("Not configured."));
        }
    }

    private sealed class SignalRouterSpy : IOrderRouter, IStrategySignalSink
    {
        public List<StrategySignal> Signals { get; } = [];

        public IObservable<OrderEvent> OrderEvents { get; } = new EmptyObservable<OrderEvent>();

        public Task EmitSignalAsync(StrategySignal signal, CancellationToken ct = default)
        {
            Signals.Add(signal);
            return Task.CompletedTask;
        }

        public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
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

    private sealed class CatalogSpy : IStrategyFactory
    {
        public ITradingStrategy? Strategy { get; private set; }

        public StrategyFactoryRegistration? Registration { get; private set; }

        public IReadOnlyList<ITradingStrategy> All => Strategy is null ? [] : [Strategy];

        public event EventHandler<StrategyCatalogChange>? Changed;

        public StrategyHost Create(string strategyId) => throw new NotSupportedException();

        public void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration)
        {
            Strategy = strategy;
            Registration = registration;
            Changed?.Invoke(this, new StrategyCatalogChange(strategy, Replaced: false));
        }
    }

    private sealed class TestClock : IClock
    {
        public static TestClock Instance { get; } = new();

        public DateTime UtcNow => Now.UtcDateTime;
    }
}
