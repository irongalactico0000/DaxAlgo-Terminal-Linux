using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies;

namespace DaxAlgo.Daxq.Host;

/// <summary>Bounds and transport policy for the Tier-3 signal-session client.</summary>
public sealed class DaxqSignalSessionClientOptions
{
    /// <summary>Allows plaintext WebSockets only for an explicit loopback development endpoint.</summary>
    public bool AllowInsecureLoopback { get; set; }

    public int MaximumMessageBytes { get; set; } = 16 * 1024;

    public TimeSpan MaximumSessionTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public int MaximumHeartbeatIntervalSeconds { get; set; } = 300;

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>
/// Opens one authenticated Tier-3 session. The caller supplies the already-authenticated marketplace
/// delivery context; the client proves the registered device key and never downloads strategy bytes.
/// </summary>
public interface IDaxqSignalSessionClient
{
    ValueTask<DaxqSignalSession> OpenAsync(
        DaxqDeliveryContext context,
        CancellationToken cancellationToken = default);
}

public sealed class DaxqSignalSessionClient : IDaxqSignalSessionClient
{
    private readonly IDaxqLicensingTransport _licensingTransport;
    private readonly IDaxqSignalSessionTransport _sessionTransport;
    private readonly IDaxqDeviceIdentityProvider _deviceIdentityProvider;
    private readonly IDaxqSignalSocketFactory _socketFactory;
    private readonly DaxqSignalSessionClientOptions _options;

    public DaxqSignalSessionClient(
        HttpClient httpClient,
        IDaxqDeviceIdentityProvider deviceIdentityProvider,
        DaxqSignalSessionClientOptions? options = null)
        : this(
            new HttpDaxqLicensingTransport(httpClient),
            new HttpDaxqSignalSessionTransport(httpClient),
            deviceIdentityProvider,
            ClientWebSocketFactory.Instance,
            options ?? new DaxqSignalSessionClientOptions())
    {
    }

    internal DaxqSignalSessionClient(
        IDaxqLicensingTransport licensingTransport,
        IDaxqSignalSessionTransport sessionTransport,
        IDaxqDeviceIdentityProvider deviceIdentityProvider,
        IDaxqSignalSocketFactory socketFactory,
        DaxqSignalSessionClientOptions options)
    {
        _licensingTransport = licensingTransport ?? throw new ArgumentNullException(nameof(licensingTransport));
        _sessionTransport = sessionTransport ?? throw new ArgumentNullException(nameof(sessionTransport));
        _deviceIdentityProvider = deviceIdentityProvider ??
                                  throw new ArgumentNullException(nameof(deviceIdentityProvider));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MaximumMessageBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Signal messages must be bounded to 1 KiB-1 MiB.");
        if (options.MaximumSessionTokenLifetime <= TimeSpan.Zero ||
            options.MaximumSessionTokenLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The maximum signal-session token lifetime must be positive and at most one hour.");
        }
        if (options.MaximumHeartbeatIntervalSeconds is < 1 or > 3_600)
            throw new ArgumentOutOfRangeException(nameof(options), "The heartbeat bound must be 1-3600 seconds.");
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
    }

    public async ValueTask<DaxqSignalSession> OpenAsync(
        DaxqDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        var identity = await _deviceIdentityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var idempotencyKey = Guid.NewGuid().ToString("N");
        byte[]? clientNonce = null;
        byte[]? challengeNonce = null;
        byte[]? proof = null;
        byte[]? signature = null;
        try
        {
            clientNonce = RandomNumberGenerator.GetBytes(32);
            var binding = DaxqCryptography.Sha256Hex(clientNonce);
            var challenge = await _licensingTransport.CreateChallengeAsync(
                    context,
                    new DaxqChallengeRequest(
                        identity.DeviceId,
                        context.LicenseId,
                        context.ReleaseId,
                        DaxqCryptography.SignalSessionOperation,
                        binding),
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            challengeNonce = ValidateChallenge(challenge);
            CryptographicOperations.ZeroMemory(challengeNonce);
            challengeNonce = null;

            proof = DaxqCryptography.BuildDeviceProof(
                DaxqCryptography.SignalSessionOperation,
                challenge,
                context,
                identity.DeviceId,
                binding,
                idempotencyKey);
            signature = identity.Sign(proof);
            var response = await _sessionTransport.OpenAsync(
                    context,
                    context.LicenseId,
                    new DaxqSignalSessionOpenRequest(
                        context.ReleaseId,
                        identity.DeviceId,
                        challenge.ChallengeId,
                        DaxqCryptography.Base64Url(clientNonce),
                        DaxqCryptography.Base64Url(signature)),
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);

            var validated = ValidateResponse(response);
            var socket = _socketFactory.Create();
            try
            {
                await socket.ConnectAsync(
                        validated.WebSocketUri,
                        response.SessionToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new DaxqSignalSession(
                    response.SessionId,
                    response.ExpiresAt,
                    TimeSpan.FromSeconds(response.HeartbeatIntervalSeconds),
                    socket,
                    _options.MaximumMessageBytes);
            }
            catch
            {
                await socket.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (clientNonce is not null) CryptographicOperations.ZeroMemory(clientNonce);
            if (challengeNonce is not null) CryptographicOperations.ZeroMemory(challengeNonce);
            if (proof is not null) CryptographicOperations.ZeroMemory(proof);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    private byte[] ValidateChallenge(DaxqChallengeResponse challenge)
    {
        if (challenge.ChallengeId == Guid.Empty || challenge.ExpiresAt <= _options.TimeProvider.GetUtcNow())
            throw new InvalidDataException("The signal-session device challenge is invalid or expired.");
        var nonce = DaxqCryptography.DecodeBase64Url(challenge.Nonce);
        if (nonce.Length is >= 16 and <= 64) return nonce;
        CryptographicOperations.ZeroMemory(nonce);
        throw new InvalidDataException("The signal-session challenge nonce has an invalid size.");
    }

    private ValidatedSignalSessionResponse ValidateResponse(DaxqSignalSessionOpenResponse response)
    {
        var now = _options.TimeProvider.GetUtcNow();
        if (response.SessionId == Guid.Empty)
            throw new InvalidDataException("The signal-session response omitted its session id.");
        if (string.IsNullOrWhiteSpace(response.SessionToken) || response.SessionToken.Length > 8_192)
            throw new InvalidDataException("The signal-session response returned an invalid session token.");
        if (response.ExpiresAt <= now || response.ExpiresAt - now > _options.MaximumSessionTokenLifetime)
            throw new InvalidDataException("The signal-session token is expired or is not short-lived.");
        if (response.HeartbeatIntervalSeconds is < 1 ||
            response.HeartbeatIntervalSeconds > _options.MaximumHeartbeatIntervalSeconds)
        {
            throw new InvalidDataException("The signal-session heartbeat interval is outside policy.");
        }
        if (!Uri.TryCreate(response.WebSocketUrl, UriKind.Absolute, out var webSocketUri) ||
            !string.IsNullOrEmpty(webSocketUri.UserInfo) ||
            !string.IsNullOrEmpty(webSocketUri.Fragment))
        {
            throw new InvalidDataException("The signal-session WebSocket URL is invalid.");
        }

        var secure = string.Equals(webSocketUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
        var allowedLoopback = _options.AllowInsecureLoopback && webSocketUri.IsLoopback &&
                              string.Equals(webSocketUri.Scheme, "ws", StringComparison.OrdinalIgnoreCase);
        if (!secure && !allowedLoopback)
            throw new InvalidDataException("Signal sessions require WSS outside explicit loopback development.");
        return new ValidatedSignalSessionResponse(webSocketUri);
    }

    private static void ValidateContext(DaxqDeliveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.LicenseId == Guid.Empty || context.ReleaseId == Guid.Empty)
            throw new InvalidOperationException("The signal-session context omitted a license or release id.");
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new InvalidOperationException("An access token is required for a signal session.");
    }

    private sealed record ValidatedSignalSessionResponse(Uri WebSocketUri);
}

/// <summary>Terminal state of a WebSocket receive loop. No reconnect is attempted.</summary>
public sealed record DaxqSignalSessionTermination(
    WebSocketCloseStatus? CloseStatus,
    string? Reason,
    bool RemoteClose);

/// <summary>One connected, single-use Tier-3 signal stream.</summary>
public sealed class DaxqSignalSession : IAsyncDisposable
{
    private readonly IDaxqSignalSocket _socket;
    private readonly int _maximumMessageBytes;
    private readonly CancellationTokenSource _stop = new();
    private int _receiveStarted;
    private int _disposeState;
    private long _lastSequence;

    internal DaxqSignalSession(
        Guid sessionId,
        DateTimeOffset expiresAt,
        TimeSpan heartbeatInterval,
        IDaxqSignalSocket socket,
        int maximumMessageBytes)
    {
        SessionId = sessionId;
        ExpiresAt = expiresAt;
        HeartbeatInterval = heartbeatInterval;
        _socket = socket;
        _maximumMessageBytes = maximumMessageBytes;
    }

    public Guid SessionId { get; }

    public DateTimeOffset ExpiresAt { get; }

    public TimeSpan HeartbeatInterval { get; }

    public async Task<DaxqSignalSessionTermination> ReceiveAsync(
        Func<StrategySignalEvent, CancellationToken, ValueTask> onSignal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSignal);
        if (Interlocked.Exchange(ref _receiveStarted, 1) != 0)
            throw new InvalidOperationException("A signal session can only be consumed once.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var buffer = new byte[_maximumMessageBytes];
        try
        {
            while (true)
            {
                var count = 0;
                DaxqSocketReceiveResult result;
                do
                {
                    if (count == buffer.Length)
                        throw new InvalidDataException("A signal-session message exceeded its size bound.");
                    result = await ReceiveWithHeartbeatTimeoutAsync(
                            buffer.AsMemory(count),
                            linked.Token,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return new DaxqSignalSessionTermination(
                            result.CloseStatus,
                            result.CloseDescription,
                            RemoteClose: true);
                    }
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidDataException("Signal sessions accept text frames only.");
                    count = checked(count + result.Count);
                } while (!result.EndOfMessage);

                var signal = ParseMessage(buffer.AsMemory(0, count));
                if (signal is { } emitted)
                    await onSignal(emitted, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _stop.Cancel();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "client_stop",
                        timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            await _socket.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }

    private async ValueTask<DaxqSocketReceiveResult> ReceiveWithHeartbeatTimeoutAsync(
        Memory<byte> buffer,
        CancellationToken linkedCancellation,
        CancellationToken callerCancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linkedCancellation);
        var silenceLimit = TimeSpan.FromSeconds(Math.Max(5d, HeartbeatInterval.TotalSeconds * 3d));
        timeout.CancelAfter(silenceLimit);
        try
        {
            return await _socket.ReceiveAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested &&
                                                  !_stop.IsCancellationRequested)
        {
            throw new TimeoutException("The signal-session heartbeat timed out.");
        }
    }

    private StrategySignalEvent? ParseMessage(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The signal-session frame is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The signal-session frame must be a JSON object.");
            var properties = ReadUniquePropertyNames(root);
            var type = RequiredString(root, "type");
            return type switch
            {
                "signal" => ParseSignal(root, properties),
                "heartbeat" => ParseHeartbeat(root, properties),
                _ => throw new InvalidDataException("The signal-session frame type is unknown."),
            };
        }
    }

    private StrategySignalEvent ParseSignal(JsonElement root, HashSet<string> properties)
    {
        RequireExactProperties(
            properties,
            "type",
            "sequence",
            "emitted_at",
            "kind",
            "strength",
            "note_id");
        AcceptSequence(RequiredInt64(root, "sequence"));
        var timestamp = RequiredUtcTimestamp(root, "emitted_at");
        var kind = RequiredString(root, "kind") switch
        {
            "long" => StrategySignalKind.Long,
            "short" => StrategySignalKind.Short,
            "flat" => StrategySignalKind.Flat,
            _ => throw new InvalidDataException("The signal-session signal kind is unknown."),
        };
        var strength = RequiredDouble(root, "strength");
        var noteId = RequiredInt64(root, "note_id");
        if (!double.IsFinite(strength) || strength is < 0d or > 1d || noteId < 0)
            throw new InvalidDataException("The signal-session signal payload is outside its bounds.");
        return new StrategySignalEvent(
            timestamp.UtcDateTime,
            new StrategySignal(kind, strength, noteId));
    }

    private StrategySignalEvent? ParseHeartbeat(JsonElement root, HashSet<string> properties)
    {
        RequireExactProperties(properties, "type", "sequence", "sent_at");
        AcceptSequence(RequiredInt64(root, "sequence"));
        _ = RequiredUtcTimestamp(root, "sent_at");
        return null;
    }

    private void AcceptSequence(long sequence)
    {
        var expected = checked(_lastSequence + 1);
        if (sequence != expected)
            throw new InvalidDataException("The signal-session frame sequence is missing, duplicated, or reordered.");
        _lastSequence = sequence;
    }

    private static HashSet<string> ReadUniquePropertyNames(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException("The signal-session frame contains a duplicate property.");
        }
        return names;
    }

    private static void RequireExactProperties(HashSet<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length || expected.Any(name => !actual.Contains(name)))
            throw new InvalidDataException("The signal-session frame schema is not recognized.");
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw new InvalidDataException($"The signal-session frame omitted {name}.");
        }
        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"The signal-session frame omitted {name}.");
        }
        return result;
    }

    private static double RequiredDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result))
        {
            throw new InvalidDataException($"The signal-session frame omitted {name}.");
        }
        return result;
    }

    private static DateTimeOffset RequiredUtcTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !value.TryGetDateTimeOffset(out var timestamp) || timestamp.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"The signal-session frame omitted a UTC {name}.");
        }
        return timestamp;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DaxqSignalSessionOpenRequest(
    [property: JsonPropertyName("release_id")] Guid ReleaseId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("challenge_id")] Guid ChallengeId,
    [property: JsonPropertyName("client_session_nonce")] string ClientSessionNonce,
    [property: JsonPropertyName("device_signature")] string DeviceSignature);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DaxqSignalSessionOpenResponse(
    [property: JsonPropertyName("session_id")] Guid SessionId,
    [property: JsonPropertyName("websocket_url")] string WebSocketUrl,
    [property: JsonPropertyName("session_token")] string SessionToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("heartbeat_interval_seconds")] int HeartbeatIntervalSeconds);

internal interface IDaxqSignalSessionTransport
{
    ValueTask<DaxqSignalSessionOpenResponse> OpenAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqSignalSessionOpenRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed class HttpDaxqSignalSessionTransport(HttpClient httpClient) : IDaxqSignalSessionTransport
{
    public async ValueTask<DaxqSignalSessionOpenResponse> OpenAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqSignalSessionOpenRequest body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/marketplace/licenses/{licenseId:D}/signal-sessions")
        {
            Content = JsonContent.Create(body),
        };
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new InvalidOperationException("An access token is required for a signal session.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadProblemDetailAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429)
            {
                throw new HttpRequestException(
                    detail ?? "The signal-session service is temporarily unavailable.",
                    inner: null,
                    response.StatusCode);
            }
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new DaxqLicenseDeniedException(detail ?? "The signal-session request was denied.");
            throw new HttpRequestException(
                detail ?? "The signal-session service is unavailable.",
                inner: null,
                response.StatusCode);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<DaxqSignalSessionOpenResponse>(
                       cancellationToken: cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw new InvalidDataException("The signal-session service returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The signal-session service returned an invalid response.", exception);
        }
    }

    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("detail", out var detail)) return detail.GetString();
            if (document.RootElement.TryGetProperty("title", out var title)) return title.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }
}

internal readonly record struct DaxqSocketReceiveResult(
    int Count,
    bool EndOfMessage,
    WebSocketMessageType MessageType,
    WebSocketCloseStatus? CloseStatus,
    string? CloseDescription);

internal interface IDaxqSignalSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    ValueTask ConnectAsync(Uri uri, string sessionToken, CancellationToken cancellationToken);

    ValueTask<DaxqSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    ValueTask CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string closeDescription,
        CancellationToken cancellationToken);
}

internal interface IDaxqSignalSocketFactory
{
    IDaxqSignalSocket Create();
}

internal sealed class ClientWebSocketFactory : IDaxqSignalSocketFactory
{
    public static ClientWebSocketFactory Instance { get; } = new();

    public IDaxqSignalSocket Create() => new ClientWebSocketAdapter();
}

internal sealed class ClientWebSocketAdapter : IDaxqSignalSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public async ValueTask ConnectAsync(
        Uri uri,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {sessionToken}");
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DaxqSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new DaxqSocketReceiveResult(
            result.Count,
            result.EndOfMessage,
            result.MessageType,
            _socket.CloseStatus,
            _socket.CloseStatusDescription);
    }

    public async ValueTask CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string closeDescription,
        CancellationToken cancellationToken) =>
        await _socket.CloseOutputAsync(closeStatus, closeDescription, cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
