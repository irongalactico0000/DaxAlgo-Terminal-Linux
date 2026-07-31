using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaxAlgo.Daxq.Host;

/// <summary>
/// Development-only entitlement adapter. It deliberately implements the same nonce, device-proof,
/// ECDH, AES-KW, signed-token, heartbeat, and revocation-feed protocol as the HTTP transport.
/// </summary>
internal sealed class DaxqDevelopmentLicensing :
    IDaxqDeliveryContextResolver,
    IDaxqLicensingTransport,
    IDisposable
{
    private static readonly Guid AccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid LicenseId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private const string SigningKeyId = "daxq-local-dev-licensing-es256-v1";
    private readonly IDaxqDeviceIdentityProvider _deviceIdentityProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _runTokenLifetime;
    private readonly TimeSpan _offlineLeaseLifetime;
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly object _signGate = new();
    private readonly ConcurrentDictionary<Guid, StoredChallenge> _challenges = new();
    private readonly ConcurrentDictionary<Guid, string> _contentKeyIds = new();
    private readonly List<DaxqRevocationEntry> _revocations = [];
    private int _unavailable;
    private int _entitlementActive = 1;
    private long _rollbackSequence = -1;
    private int _challengeCalls;
    private int _keyReleaseCalls;
    private int _heartbeatCalls;
    private int _feedCalls;
    private int _disposed;

    public DaxqDevelopmentLicensing(
        IDaxqDeviceIdentityProvider deviceIdentityProvider,
        TimeProvider timeProvider,
        TimeSpan? runTokenLifetime = null,
        TimeSpan? offlineLeaseLifetime = null)
    {
        _deviceIdentityProvider = deviceIdentityProvider;
        _timeProvider = timeProvider;
        _runTokenLifetime = runTokenLifetime ?? TimeSpan.FromMinutes(45);
        _offlineLeaseLifetime = offlineLeaseLifetime ?? TimeSpan.FromHours(24);
        if (_runTokenLifetime <= TimeSpan.Zero || _runTokenLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(runTokenLifetime));
        if (_offlineLeaseLifetime < TimeSpan.Zero || _offlineLeaseLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(offlineLeaseLifetime));
        VerificationKeys = new DaxqEs256PublicKeyRing().Add(
            SigningKeyId,
            _signingKey.ExportSubjectPublicKeyInfo());
    }

    public DaxqEs256PublicKeyRing VerificationKeys { get; }

    public int ChallengeCalls => Volatile.Read(ref _challengeCalls);

    public int KeyReleaseCalls => Volatile.Read(ref _keyReleaseCalls);

    public int HeartbeatCalls => Volatile.Read(ref _heartbeatCalls);

    public int FeedCalls => Volatile.Read(ref _feedCalls);

    public ValueTask<DaxqDeliveryContext> ResolveAsync(
        string strategyId,
        string version,
        string contentKeyId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(strategyId + "\0" + version + "\0" + contentKeyId));
        try
        {
            var releaseId = new Guid(digest.AsSpan(0, 16));
            _contentKeyIds[releaseId] = contentKeyId;
            return ValueTask.FromResult(new DaxqDeliveryContext(
                LicenseId,
                releaseId,
                AccessToken: "development-local-entitlement",
                AccountId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public async ValueTask<DaxqChallengeResponse> CreateChallengeAsync(
        DaxqDeliveryContext context,
        DaxqChallengeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        EnsureEntitled();
        Interlocked.Increment(ref _challengeCalls);
        if (request.DeviceId == Guid.Empty || request.LicenseId != context.LicenseId ||
            request.ReleaseId != context.ReleaseId ||
            request.Operation is not (DaxqCryptography.ContentKeyOperation or
                DaxqCryptography.HeartbeatOperation) ||
            request.BindingSha256.Length != 64)
        {
            throw new DaxqLicenseDeniedException("The development device challenge is invalid.");
        }
        var identity = await _deviceIdentityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        if (identity.DeviceId != request.DeviceId)
            throw new DaxqLicenseDeniedException("The development device is unknown.");
        var nonce = RandomNumberGenerator.GetBytes(32);
        var response = new DaxqChallengeResponse(
            Guid.NewGuid(),
            DaxqCryptography.Base64Url(nonce),
            _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(2));
        CryptographicOperations.ZeroMemory(nonce);
        _challenges[response.ChallengeId] = new StoredChallenge(request, idempotencyKey, response);
        return response;
    }

    public async ValueTask<DaxqContentKeyResponse> ReleaseContentKeyAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqContentKeyRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        EnsureEntitled();
        Interlocked.Increment(ref _keyReleaseCalls);
        if (!_contentKeyIds.TryGetValue(context.ReleaseId, out var contentKeyId))
            throw new DaxqLicenseDeniedException("The development release is unknown.");
        var challenge = RequireChallenge(
            context,
            request.DeviceId,
            request.ReleaseId,
            request.ChallengeId,
            licenseId,
            DaxqCryptography.ContentKeyOperation,
            idempotencyKey);
        var sessionPublicKey = DaxqCryptography.DecodeBase64Url(request.SessionPublicKey);
        try
        {
            if (DaxqCryptography.Sha256Hex(sessionPublicKey) != challenge.Request.BindingSha256)
                throw new DaxqLicenseDeniedException("The development session key binding changed.");
            await VerifyDeviceProofAsync(
                    context,
                    request.DeviceId,
                    challenge,
                    request.DeviceSignature,
                    cancellationToken)
                .ConfigureAwait(false);

            using var clientEphemeral = ECDiffieHellman.Create();
            clientEphemeral.ImportSubjectPublicKeyInfo(sessionPublicKey, out var read);
            if (read != sessionPublicKey.Length || clientEphemeral.ExportParameters(false).Curve.Oid.Value !=
                ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new DaxqLicenseDeniedException("The development session key is not P-256.");
            }
            using var serverEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var sharedSecret = serverEphemeral.DeriveRawSecretAgreement(clientEphemeral.PublicKey);
            var nonce = DaxqCryptography.DecodeBase64Url(challenge.Response.Nonce);
            byte[]? kek = null;
            byte[]? contentKey = null;
            byte[]? wrapped = null;
            try
            {
                kek = DaxqCryptography.DeriveContentKeyEncryptionKey(
                    sharedSecret,
                    nonce,
                    context,
                    request.DeviceId,
                    contentKeyId);
                contentKey = SHA256.HashData("DAXQ-LOCAL-DEV-CONTENT-KEY"u8);
                wrapped = DaxqCryptography.WrapKey(kek, contentKey);
                var now = _timeProvider.GetUtcNow();
                return new DaxqContentKeyResponse(
                    DaxqCryptography.Base64Url(wrapped),
                    DaxqCryptography.KeyWrapAlgorithm,
                    DaxqCryptography.ContentAlgorithm,
                    contentKeyId,
                    900,
                    DaxqCryptography.Base64Url(serverEphemeral.ExportSubjectPublicKeyInfo()),
                    SignToken("run_token", context, request.DeviceId, now, now + _runTokenLifetime),
                    _offlineLeaseLifetime == TimeSpan.Zero
                        ? null
                        : SignToken(
                            "offline_lease",
                            context,
                            request.DeviceId,
                            now,
                            now + _offlineLeaseLifetime));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
                CryptographicOperations.ZeroMemory(nonce);
                if (kek is not null) CryptographicOperations.ZeroMemory(kek);
                if (contentKey is not null) CryptographicOperations.ZeroMemory(contentKey);
                if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionPublicKey);
        }
    }

    public async ValueTask<DaxqHeartbeatResponse> HeartbeatAsync(
        DaxqDeliveryContext context,
        Guid licenseId,
        DaxqHeartbeatRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        EnsureEntitled();
        Interlocked.Increment(ref _heartbeatCalls);
        var challenge = RequireChallenge(
            context,
            request.DeviceId,
            request.ReleaseId,
            request.ChallengeId,
            licenseId,
            DaxqCryptography.HeartbeatOperation,
            idempotencyKey);
        var bindingBytes = Encoding.UTF8.GetBytes(
            request.RunToken.EncodedPayload + "." + request.RunToken.EncodedSignature);
        try
        {
            if (DaxqCryptography.Sha256Hex(bindingBytes) != challenge.Request.BindingSha256)
                throw new DaxqLicenseDeniedException("The development run-token binding changed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bindingBytes);
        }
        var prior = DaxqCryptography.VerifyEnvelope<DaxqLicenseTokenClaims>(
            request.RunToken,
            VerificationKeys);
        if (prior.LicenseId != context.LicenseId || prior.ReleaseId != context.ReleaseId ||
            prior.DeviceId != request.DeviceId)
        {
            throw new DaxqLicenseDeniedException("The development run token is invalid.");
        }
        await VerifyDeviceProofAsync(
                context,
                request.DeviceId,
                challenge,
                request.DeviceSignature,
                cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        return new DaxqHeartbeatResponse(
            TtlSeconds(),
            SignToken("run_token", context, request.DeviceId, now, now + _runTokenLifetime),
            _offlineLeaseLifetime == TimeSpan.Zero
                ? null
                : SignToken(
                    "offline_lease",
                    context,
                    request.DeviceId,
                    now,
                    now + _offlineLeaseLifetime));
    }

    public ValueTask<DaxqSignedEnvelope> GetRevocationsAsync(
        DaxqDeliveryContext context,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        EnsureEntitled();
        Interlocked.Increment(ref _feedCalls);
        DaxqRevocationEntry[] entries;
        long through;
        lock (_revocations)
        {
            entries = _revocations.Where(entry => entry.Sequence > afterSequence).ToArray();
            through = _revocations.Count == 0 ? 0 : _revocations[^1].Sequence;
        }
        var forcedRollback = Interlocked.Read(ref _rollbackSequence);
        if (forcedRollback >= 0)
        {
            through = forcedRollback;
            entries = [];
        }
        var claims = new DaxqRevocationFeedClaims(
            1,
            afterSequence,
            through,
            _timeProvider.GetUtcNow(),
            entries);
        return ValueTask.FromResult(Sign(claims));
    }

    public void RevokeEntitlement() => Volatile.Write(ref _entitlementActive, 0);

    public void SetUnavailable(bool unavailable) => Volatile.Write(ref _unavailable, unavailable ? 1 : 0);

    public void ForceRollback(long sequence) => Interlocked.Exchange(ref _rollbackSequence, sequence);

    public long PublishRevocation(string targetType, string targetId, string reason)
    {
        lock (_revocations)
        {
            var sequence = _revocations.Count == 0 ? 1 : _revocations[^1].Sequence + 1;
            _revocations.Add(new DaxqRevocationEntry(
                sequence,
                targetType,
                targetId,
                reason,
                _timeProvider.GetUtcNow()));
            return sequence;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _signingKey.Dispose();
    }

    private async ValueTask VerifyDeviceProofAsync(
        DaxqDeliveryContext context,
        Guid deviceId,
        StoredChallenge challenge,
        string encodedSignature,
        CancellationToken cancellationToken)
    {
        var identity = await _deviceIdentityProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        if (identity.DeviceId != deviceId)
            throw new DaxqLicenseDeniedException("The development device is unknown.");
        var proof = DaxqCryptography.BuildDeviceProof(
            challenge.Request.Operation,
            challenge.Response,
            context,
            deviceId,
            challenge.Request.BindingSha256,
            challenge.IdempotencyKey);
        var signature = DaxqCryptography.DecodeBase64Url(encodedSignature, 64);
        var publicKey = identity.ExportPublicKey();
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || !verifier.VerifyData(
                    proof,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new DaxqLicenseDeniedException("The development device proof is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private StoredChallenge RequireChallenge(
        DaxqDeliveryContext context,
        Guid deviceId,
        Guid releaseId,
        Guid challengeId,
        Guid licenseId,
        string operation,
        string idempotencyKey)
    {
        if (!_challenges.TryRemove(challengeId, out var challenge) ||
            challenge.Response.ExpiresAt <= _timeProvider.GetUtcNow() ||
            challenge.Request.DeviceId != deviceId || challenge.Request.LicenseId != licenseId ||
            challenge.Request.ReleaseId != releaseId || challenge.Request.Operation != operation ||
            challenge.IdempotencyKey != idempotencyKey || context.LicenseId != licenseId ||
            context.ReleaseId != releaseId)
        {
            throw new DaxqLicenseDeniedException("The development device challenge is invalid or replayed.");
        }
        return challenge;
    }

    private DaxqSignedEnvelope SignToken(
        string kind,
        DaxqDeliveryContext context,
        Guid deviceId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var accessValidUntil = issuedAt + TimeSpan.FromDays(30);
        if (expiresAt > accessValidUntil)
            expiresAt = accessValidUntil;
        return Sign(new DaxqLicenseTokenClaims(
            1,
            kind,
            Guid.NewGuid().ToString("N"),
            context.LicenseId,
            context.ReleaseId,
            AccountId,
            deviceId,
            "daxalgo-platform-development",
            "daxalgo-daxq-host",
            issuedAt,
            expiresAt,
            accessValidUntil,
            LatestRevocationSequence()));
    }

    private DaxqSignedEnvelope Sign<T>(T claims)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        byte[] signature;
        lock (_signGate)
        {
            signature = _signingKey.SignData(
                payload,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        try
        {
            return new DaxqSignedEnvelope(
                SigningKeyId,
                DaxqCryptography.Es256,
                DaxqCryptography.Base64Url(payload),
                DaxqCryptography.Base64Url(signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private void EnsureAvailable()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _unavailable) != 0)
            throw new HttpRequestException("The development licensing service is unavailable.");
    }

    private void EnsureEntitled()
    {
        if (Volatile.Read(ref _entitlementActive) == 0)
            throw new DaxqLicenseDeniedException("The development entitlement was revoked.");
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private long LatestRevocationSequence()
    {
        lock (_revocations)
            return _revocations.Count == 0 ? 0 : _revocations[^1].Sequence;
    }

    private int TtlSeconds() => checked((int)Math.Ceiling(_runTokenLifetime.TotalSeconds));

    private sealed record StoredChallenge(
        DaxqChallengeRequest Request,
        string IdempotencyKey,
        DaxqChallengeResponse Response);
}
