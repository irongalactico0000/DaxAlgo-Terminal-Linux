using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaxAlgo.Daxq.Host;

public sealed record DaxqDeliveryContext(
    Guid LicenseId,
    Guid ReleaseId,
    string? AccessToken = null,
    Guid? AccountId = null);

public interface IDaxqDeliveryContextResolver
{
    ValueTask<DaxqDeliveryContext> ResolveAsync(
        string strategyId,
        string version,
        string contentKeyId,
        CancellationToken cancellationToken);
}

public sealed record DaxqDeviceRegistration(
    Guid DeviceId,
    string PublicKeySpki,
    string FingerprintSha256);

public interface IDaxqDeviceIdentityProvider
{
    ValueTask<DaxqDeviceIdentity> GetAsync(CancellationToken cancellationToken);
}

public interface IDaxqLicensingTransport
{
    ValueTask<DaxqChallengeResponse> CreateChallengeAsync(
        DaxqDeliveryContext context,
        DaxqChallengeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<DaxqContentKeyResponse> ReleaseContentKeyAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqContentKeyRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<DaxqHeartbeatResponse> HeartbeatAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqHeartbeatRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<DaxqSignedEnvelope> GetRevocationsAsync(
        DaxqDeliveryContext context,
        long afterSequence,
        CancellationToken cancellationToken);
}

public sealed record DaxqChallengeRequest(
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("license_id")] Guid LicenseId,
    [property: JsonPropertyName("release_id")] Guid ReleaseId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("binding_sha256")] string BindingSha256);

public sealed record DaxqChallengeResponse(
    [property: JsonPropertyName("challenge_id")] Guid ChallengeId,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record DaxqContentKeyRequest(
    [property: JsonPropertyName("release_id")] Guid ReleaseId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("challenge_id")] Guid ChallengeId,
    [property: JsonPropertyName("session_public_key")] string SessionPublicKey,
    [property: JsonPropertyName("device_signature")] string DeviceSignature);

public sealed record DaxqContentKeyResponse(
    [property: JsonPropertyName("wrapped_key")] string WrappedKey,
    [property: JsonPropertyName("key_wrap_algorithm")] string KeyWrapAlgorithm,
    [property: JsonPropertyName("content_algorithm")] string ContentAlgorithm,
    [property: JsonPropertyName("content_key_id")] string ContentKeyId,
    [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
    [property: JsonPropertyName("server_ephemeral_public_key")] string ServerEphemeralPublicKey,
    [property: JsonPropertyName("run_token")] DaxqSignedEnvelope RunToken,
    [property: JsonPropertyName("offline_lease")] DaxqSignedEnvelope? OfflineLease);

public sealed record DaxqHeartbeatRequest(
    [property: JsonPropertyName("release_id")] Guid ReleaseId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("challenge_id")] Guid ChallengeId,
    [property: JsonPropertyName("run_token")] DaxqSignedEnvelope RunToken,
    [property: JsonPropertyName("device_signature")] string DeviceSignature);

public sealed record DaxqHeartbeatResponse(
    [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
    [property: JsonPropertyName("run_token")] DaxqSignedEnvelope RunToken,
    [property: JsonPropertyName("offline_lease")] DaxqSignedEnvelope? OfflineLease);

public sealed record DaxqSignedEnvelope(
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("encoded_payload")] string EncodedPayload,
    [property: JsonPropertyName("encoded_signature")] string EncodedSignature);

internal sealed record DaxqLicenseTokenClaims(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("token_kind")] string TokenKind,
    [property: JsonPropertyName("token_id")] string TokenId,
    [property: JsonPropertyName("license_id")] Guid LicenseId,
    [property: JsonPropertyName("release_id")] Guid ReleaseId,
    [property: JsonPropertyName("account_id")] Guid AccountId,
    [property: JsonPropertyName("device_id")] Guid DeviceId,
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("access_valid_until")] DateTimeOffset AccessValidUntil,
    [property: JsonPropertyName("revocation_seq")] long RevocationSequence);

internal sealed record DaxqRevocationFeedClaims(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("from_seq")] long FromSequence,
    [property: JsonPropertyName("through_seq")] long ThroughSequence,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("revocations")] IReadOnlyList<DaxqRevocationEntry> Revocations);

internal sealed record DaxqRevocationEntry(
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("target_type")] string TargetType,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("issued_at")] DateTimeOffset IssuedAt);

public sealed class DaxqLicenseDeniedException : InvalidOperationException
{
    public DaxqLicenseDeniedException(string message) : base(message)
    {
    }
}

public sealed class HttpDaxqLicensingTransport : IDaxqLicensingTransport
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private readonly HttpClient _httpClient;

    public HttpDaxqLicensingTransport(HttpClient httpClient) =>
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public ValueTask<DaxqChallengeResponse> CreateChallengeAsync(
        DaxqDeliveryContext context,
        DaxqChallengeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync<DaxqChallengeRequest, DaxqChallengeResponse>(
            HttpMethod.Post,
            "/v1/devices/challenges",
            context,
            request,
            idempotencyKey,
            cancellationToken);

    public ValueTask<DaxqContentKeyResponse> ReleaseContentKeyAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqContentKeyRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync<DaxqContentKeyRequest, DaxqContentKeyResponse>(
            HttpMethod.Post,
            $"/v1/marketplace/licenses/{licenseId:D}/content-keys",
            context,
            request,
            idempotencyKey,
            cancellationToken);

    public ValueTask<DaxqHeartbeatResponse> HeartbeatAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqHeartbeatRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync<DaxqHeartbeatRequest, DaxqHeartbeatResponse>(
            HttpMethod.Post,
            $"/v1/marketplace/licenses/{licenseId:D}/heartbeats",
            context,
            request,
            idempotencyKey,
            cancellationToken);

    public async ValueTask<DaxqSignedEnvelope> GetRevocationsAsync(
        DaxqDeliveryContext context,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v1/marketplace/revocations?after_seq={afterSequence}");
        AddBearer(request, context);
        return await SendAsync<DaxqSignedEnvelope>(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        DaxqDeliveryContext context,
        TRequest body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        AddBearer(request, context);
        request.Headers.Add(IdempotencyHeader, idempotencyKey);
        return await SendAsync<TResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TResponse> SendAsync<TResponse>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadProblemDetailAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or
                (HttpStatusCode)429)
            {
                throw new HttpRequestException(
                    detail ?? "The licensing service is temporarily unavailable.",
                    inner: null,
                    response.StatusCode);
            }
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new DaxqLicenseDeniedException(detail ?? "The licensing request was denied.");
            throw new HttpRequestException(
                detail ?? "The licensing service is unavailable.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("The licensing service returned an empty response.");
    }

    private static void AddBearer(HttpRequestMessage request, DaxqDeliveryContext context)
    {
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new InvalidOperationException("An access token is required for platform licensing.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
    }

    private static async Task<string?> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString();
            if (document.RootElement.TryGetProperty("title", out var title))
                return title.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
