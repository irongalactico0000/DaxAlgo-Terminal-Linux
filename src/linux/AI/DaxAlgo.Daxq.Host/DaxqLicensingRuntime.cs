using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaxAlgo.Daxq.Contracts;
using DaxAlgo.Daxq.Vm;
using Microsoft.Extensions.Logging;

namespace DaxAlgo.Daxq.Host;

internal sealed class DaxqLicenseGate
{
    private readonly TaskCompletionSource<string> _revoked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _reason;
    private int _authorized = 1;

    public bool IsAuthorized => Volatile.Read(ref _authorized) != 0;

    public string Reason => Volatile.Read(ref _reason) ?? "The DAXQ license is no longer active.";

    public Task<string> Revoked => _revoked.Task;

    public void Revoke(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Interlocked.Exchange(ref _authorized, 0) == 0)
            return;
        Volatile.Write(ref _reason, reason);
        _revoked.TrySetResult(reason);
    }
}

internal sealed class DaxqLicensedProgramSession : IDisposable
{
    private readonly DaxqHeartbeatController _heartbeat;
    private DaxqProgram? _program;
    private int _disposed;
    private int _started;

    public DaxqLicensedProgramSession(DaxqProgram program, DaxqHeartbeatController heartbeat)
    {
        _program = program;
        _heartbeat = heartbeat;
        Gate = heartbeat.Gate;
    }

    public DaxqProgram Program => _program ??
        throw new InvalidOperationException("The managed DAXQ plaintext has already been cleared.");

    public DaxqLicenseGate Gate { get; }

    public void AttachNativeVm(DaxqNativeVm vm)
    {
        _heartbeat.AttachNativeVm(vm);
        StartHeartbeat();
    }

    public void StartReferenceVm() => StartHeartbeat();

    public void ReleaseManagedProgram()
    {
        var program = Interlocked.Exchange(ref _program, null);
        program?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _heartbeat.Dispose();
            ReleaseManagedProgram();
        }
    }

    private void StartHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DaxqLicensedProgramSession));
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _heartbeat.Start();
    }
}

internal sealed class DaxqLicensingRuntime
{
    private static readonly TimeSpan MaximumOfflineGrace = TimeSpan.FromHours(24);
    private readonly IDaxqDeliveryContextResolver _contextResolver;
    private readonly IDaxqDeviceIdentityProvider _deviceIdentityProvider;
    private readonly IDaxqLicensingTransport _transport;
    private readonly DaxqEs256PublicKeyRing _licensingTrust;
    private readonly DaxqRevocationState _revocationState;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _offlineGraceLimit;
    private readonly TimeSpan _maximumFeedAge;
    private readonly string _expectedIssuer;
    private readonly string _expectedAudience;
    private readonly ILogger _logger;

    public DaxqLicensingRuntime(
        IDaxqDeliveryContextResolver contextResolver,
        IDaxqDeviceIdentityProvider deviceIdentityProvider,
        IDaxqLicensingTransport transport,
        DaxqEs256PublicKeyRing licensingTrust,
        TimeProvider timeProvider,
        TimeSpan heartbeatInterval,
        TimeSpan offlineGraceLimit,
        TimeSpan maximumFeedAge,
        string expectedIssuer,
        string expectedAudience,
        string? revocationStatePath,
        ILogger logger)
    {
        _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
        _deviceIdentityProvider = deviceIdentityProvider ??
                                  throw new ArgumentNullException(nameof(deviceIdentityProvider));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _licensingTrust = licensingTrust ?? throw new ArgumentNullException(nameof(licensingTrust));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (heartbeatInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        if (offlineGraceLimit < TimeSpan.Zero || offlineGraceLimit > MaximumOfflineGrace)
            throw new ArgumentOutOfRangeException(
                nameof(offlineGraceLimit),
                "DAXQ offline grace must be between zero and 24 hours.");
        if (maximumFeedAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumFeedAge));
        _heartbeatInterval = heartbeatInterval;
        _offlineGraceLimit = offlineGraceLimit;
        _maximumFeedAge = maximumFeedAge;
        _expectedIssuer = !string.IsNullOrWhiteSpace(expectedIssuer)
            ? expectedIssuer
            : throw new ArgumentException("A licensing issuer is required.", nameof(expectedIssuer));
        _expectedAudience = !string.IsNullOrWhiteSpace(expectedAudience)
            ? expectedAudience
            : throw new ArgumentException("A licensing audience is required.", nameof(expectedAudience));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _revocationState = new DaxqRevocationState(revocationStatePath, licensingTrust);
    }

    public async ValueTask<DaxqLicensedProgramSession> ActivateAsync(
        LoadedDaxqPackage package,
        string pluginName,
        CancellationToken cancellationToken)
    {
        var manifest = package.Manifest;
        var context = await _contextResolver.ResolveAsync(
                manifest.StrategyId,
                manifest.Version,
                manifest.Protection.ContentKeyId,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateContext(context);
        var identity = await _deviceIdentityProvider.GetAsync(cancellationToken).ConfigureAwait(false);

        await RefreshRevocationsAsync(
                package,
                context,
                accountId: context.AccountId,
                cancellationToken)
            .ConfigureAwait(false);

        using var clientEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[]? clientPublicKey = null;
        byte[]? nonce = null;
        byte[]? proof = null;
        byte[]? signature = null;
        byte[]? sharedSecret = null;
        byte[]? keyEncryptionKey = null;
        byte[]? wrappedKey = null;
        byte[]? contentKey = null;
        byte[]? plaintext = null;
        DaxqProgram? program = null;
        try
        {
            clientPublicKey = clientEphemeral.ExportSubjectPublicKeyInfo();
            var idempotencyKey = Guid.NewGuid().ToString("N");
            var binding = DaxqCryptography.Sha256Hex(clientPublicKey);
            var challenge = await _transport.CreateChallengeAsync(
                    context,
                    new DaxqChallengeRequest(
                        identity.DeviceId,
                        context.LicenseId,
                        context.ReleaseId,
                        DaxqCryptography.ContentKeyOperation,
                        binding),
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            nonce = ValidateChallenge(challenge);
            proof = DaxqCryptography.BuildDeviceProof(
                DaxqCryptography.ContentKeyOperation,
                challenge,
                context,
                identity.DeviceId,
                binding,
                idempotencyKey);
            signature = identity.Sign(proof);
            var response = await _transport.ReleaseContentKeyAsync(
                    context,
                    context.LicenseId,
                    new DaxqContentKeyRequest(
                        context.ReleaseId,
                        identity.DeviceId,
                        challenge.ChallengeId,
                        DaxqCryptography.Base64Url(clientPublicKey),
                        DaxqCryptography.Base64Url(signature)),
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(proof);
            proof = null;
            CryptographicOperations.ZeroMemory(signature);
            signature = null;
            CryptographicOperations.ZeroMemory(clientPublicKey);
            clientPublicKey = null;

            ValidateContentKeyResponse(response, manifest);
            var token = ValidateToken(
                response.RunToken,
                "run_token",
                context,
                identity.DeviceId,
                ttlSeconds: 3_600);
            var offlineExpiry = ValidateOfflineLease(
                response.OfflineLease,
                context,
                identity.DeviceId,
                token);
            var serverPublicKey = DaxqCryptography.DecodeBase64Url(response.ServerEphemeralPublicKey);
            try
            {
                using var serverEphemeral = ECDiffieHellman.Create();
                serverEphemeral.ImportSubjectPublicKeyInfo(serverPublicKey, out var read);
                if (read != serverPublicKey.Length ||
                    serverEphemeral.ExportParameters(false).Curve.Oid.Value !=
                    ECCurve.NamedCurves.nistP256.Oid.Value)
                {
                    throw new CryptographicException("The server ephemeral key is not canonical P-256 SPKI.");
                }
                sharedSecret = clientEphemeral.DeriveRawSecretAgreement(serverEphemeral.PublicKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serverPublicKey);
            }

            keyEncryptionKey = DaxqCryptography.DeriveContentKeyEncryptionKey(
                sharedSecret,
                nonce,
                context,
                identity.DeviceId,
                response.ContentKeyId);
            wrappedKey = DaxqCryptography.DecodeBase64Url(response.WrappedKey);
            contentKey = DaxqCryptography.UnwrapKey(keyEncryptionKey, wrappedKey);
            if (contentKey.Length != 32)
                throw new CryptographicException("The released DAXQ content key is not 256 bits.");
            plaintext = Decrypt(manifest, package.Ciphertext, contentKey);
            var fault = DaxqProgram.TryLoad(plaintext, out program);
            if (fault != DaxqFault.Ok || program is null)
                throw new InvalidDataException($"The decrypted DAXQ program failed verification: {fault}.");
            await RefreshRevocationsAsync(
                    package,
                    context,
                    token.AccountId,
                    cancellationToken)
                .ConfigureAwait(false);

            var heartbeat = new DaxqHeartbeatController(
                this,
                package,
                pluginName,
                context,
                identity,
                response.RunToken,
                token,
                response.OfflineLease,
                offlineExpiry,
                _heartbeatInterval,
                _timeProvider,
                _logger);
            var session = new DaxqLicensedProgramSession(program, heartbeat);
            program = null;
            return session;
        }
        finally
        {
            if (clientPublicKey is not null) CryptographicOperations.ZeroMemory(clientPublicKey);
            if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            if (proof is not null) CryptographicOperations.ZeroMemory(proof);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (sharedSecret is not null) CryptographicOperations.ZeroMemory(sharedSecret);
            if (keyEncryptionKey is not null) CryptographicOperations.ZeroMemory(keyEncryptionKey);
            if (wrappedKey is not null) CryptographicOperations.ZeroMemory(wrappedKey);
            if (contentKey is not null) CryptographicOperations.ZeroMemory(contentKey);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            program?.Dispose();
        }
    }

    internal async ValueTask<DaxqHeartbeatRefresh> RefreshAsync(
        LoadedDaxqPackage package,
        DaxqDeliveryContext context,
        DaxqDeviceIdentity identity,
        DaxqSignedEnvelope currentRunToken,
        DaxqLicenseTokenClaims currentClaims,
        CancellationToken cancellationToken)
    {
        await RefreshRevocationsAsync(
                package,
                context,
                currentClaims.AccountId,
                cancellationToken)
            .ConfigureAwait(false);

        byte[]? bindingBytes = null;
        byte[]? nonce = null;
        byte[]? proof = null;
        byte[]? signature = null;
        try
        {
            bindingBytes = Encoding.UTF8.GetBytes(
                currentRunToken.EncodedPayload + "." + currentRunToken.EncodedSignature);
            var binding = DaxqCryptography.Sha256Hex(bindingBytes);
            CryptographicOperations.ZeroMemory(bindingBytes);
            bindingBytes = null;
            var idempotencyKey = Guid.NewGuid().ToString("N");
            var challenge = await _transport.CreateChallengeAsync(
                    context,
                    new DaxqChallengeRequest(
                        identity.DeviceId,
                        context.LicenseId,
                        context.ReleaseId,
                        DaxqCryptography.HeartbeatOperation,
                        binding),
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            nonce = ValidateChallenge(challenge);
            CryptographicOperations.ZeroMemory(nonce);
            nonce = null;
            proof = DaxqCryptography.BuildDeviceProof(
                DaxqCryptography.HeartbeatOperation,
                challenge,
                context,
                identity.DeviceId,
                binding,
                idempotencyKey);
            signature = identity.Sign(proof);
            var response = await _transport.HeartbeatAsync(
                    context,
                    context.LicenseId,
                    new DaxqHeartbeatRequest(
                        context.ReleaseId,
                        identity.DeviceId,
                        challenge.ChallengeId,
                        currentRunToken,
                        DaxqCryptography.Base64Url(signature)),
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var claims = ValidateToken(
                response.RunToken,
                "run_token",
                context,
                identity.DeviceId,
                response.TtlSeconds);
            var offlineExpiry = ValidateOfflineLease(
                response.OfflineLease,
                context,
                identity.DeviceId,
                claims);
            return new DaxqHeartbeatRefresh(
                response.RunToken,
                claims,
                response.OfflineLease,
                offlineExpiry);
        }
        finally
        {
            if (bindingBytes is not null) CryptographicOperations.ZeroMemory(bindingBytes);
            if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
            if (proof is not null) CryptographicOperations.ZeroMemory(proof);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    internal void ApplyNativeLicenseEvidence(
        DaxqNativeVm vm,
        DaxqSignedEnvelope evidence)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Algorithm != DaxqCryptography.Es256)
            throw new CryptographicException("The native DAXQ license evidence does not use ES256.");

        byte[]? payload = null;
        byte[]? signature = null;
        byte[]? publicKey = null;
        try
        {
            payload = DaxqCryptography.DecodeBase64Url(evidence.EncodedPayload);
            signature = DaxqCryptography.DecodeBase64Url(evidence.EncodedSignature, 64);
            publicKey = _licensingTrust.ExportP256PublicKey(evidence.KeyId);
            var fault = vm.ApplyLicenseEvidence(payload, signature, publicKey);
            if (fault != DaxqFault.Ok)
            {
                throw new CryptographicException(
                    $"The protected native DAXQ license verifier rejected signed evidence: {fault}.");
            }
        }
        finally
        {
            if (payload is not null)
                CryptographicOperations.ZeroMemory(payload);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private async ValueTask RefreshRevocationsAsync(
        LoadedDaxqPackage package,
        DaxqDeliveryContext context,
        Guid? accountId,
        CancellationToken cancellationToken)
    {
        var requestedSequence = _revocationState.LastSequence;
        var envelope = await _transport.GetRevocationsAsync(
                context,
                requestedSequence,
                cancellationToken)
            .ConfigureAwait(false);
        var feed = DaxqCryptography.VerifyEnvelope<DaxqRevocationFeedClaims>(envelope, _licensingTrust);
        var now = _timeProvider.GetUtcNow();
        if (feed.SchemaVersion != 1 || feed.IssuedAt > now + TimeSpan.FromMinutes(2) ||
            now - feed.IssuedAt > _maximumFeedAge)
        {
            throw new CryptographicException("The signed DAXQ revocation feed is stale or malformed.");
        }
        _revocationState.Accept(feed, envelope, requestedSequence);
        foreach (var revocation in _revocationState.Entries)
        {
            var matches = revocation.TargetType switch
            {
                "account" => accountId is not null &&
                             string.Equals(revocation.TargetId, accountId.Value.ToString("D"),
                                 StringComparison.OrdinalIgnoreCase),
                "license" => string.Equals(revocation.TargetId, context.LicenseId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase),
                "release" => string.Equals(revocation.TargetId, context.ReleaseId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase),
                // Frozen DAXQ v1 does not carry the plaintext content-root identifier. Do not
                // reinterpret cipher_sha256 as content_root; release/license revocations still bind.
                "content_root" => false,
                "publisher_key" => string.Equals(
                    revocation.TargetId,
                    package.ReleaseSigningKeyId,
                    StringComparison.Ordinal),
                _ => false,
            };
            if (matches)
                throw new DaxqLicenseDeniedException($"The protected strategy was revoked: {revocation.Reason}");
        }
    }

    private DaxqLicenseTokenClaims ValidateToken(
        DaxqSignedEnvelope envelope,
        string expectedKind,
        DaxqDeliveryContext context,
        Guid deviceId,
        int ttlSeconds)
    {
        if (ttlSeconds <= 0 || ttlSeconds > 86_400)
            throw new InvalidDataException("The licensing token TTL is outside the supported bound.");
        var claims = DaxqCryptography.VerifyEnvelope<DaxqLicenseTokenClaims>(envelope, _licensingTrust);
        var now = _timeProvider.GetUtcNow();
        if (claims.SchemaVersion != 1 || !IsLowerHexTokenId(claims.TokenId) ||
            claims.AccountId == Guid.Empty || claims.RevocationSequence < 0 ||
            claims.TokenKind != expectedKind || claims.LicenseId != context.LicenseId ||
            claims.ReleaseId != context.ReleaseId || claims.DeviceId != deviceId ||
            claims.Issuer != _expectedIssuer || claims.Audience != _expectedAudience ||
            claims.IssuedAt > now + TimeSpan.FromMinutes(2) || claims.ExpiresAt <= now ||
            claims.ExpiresAt <= claims.IssuedAt ||
            claims.AccessValidUntil < claims.ExpiresAt || claims.AccessValidUntil <= now ||
            claims.RevocationSequence < _revocationState.LastSequence ||
            claims.ExpiresAt - claims.IssuedAt > TimeSpan.FromSeconds(ttlSeconds + 5))
        {
            throw new CryptographicException("The signed DAXQ licensing token has invalid binding or lifetime.");
        }
        if (context.AccountId is not null && claims.AccountId != context.AccountId)
            throw new CryptographicException("The signed DAXQ licensing token is bound to another account.");
        return claims;
    }

    private DateTimeOffset ValidateOfflineLease(
        DaxqSignedEnvelope? envelope,
        DaxqDeliveryContext context,
        Guid deviceId,
        DaxqLicenseTokenClaims runToken)
    {
        if (envelope is null || _offlineGraceLimit == TimeSpan.Zero)
            return runToken.ExpiresAt;
        var lease = DaxqCryptography.VerifyEnvelope<DaxqLicenseTokenClaims>(envelope, _licensingTrust);
        var now = _timeProvider.GetUtcNow();
        if (lease.SchemaVersion != 1 || !IsLowerHexTokenId(lease.TokenId) ||
            lease.TokenKind != "offline_lease" ||
            lease.LicenseId != context.LicenseId || lease.ReleaseId != context.ReleaseId ||
            lease.AccountId != runToken.AccountId || lease.DeviceId != deviceId ||
            lease.Issuer != _expectedIssuer || lease.Audience != _expectedAudience ||
            lease.IssuedAt > now + TimeSpan.FromMinutes(2) || lease.ExpiresAt <= now ||
            lease.ExpiresAt <= lease.IssuedAt || lease.ExpiresAt - lease.IssuedAt > _offlineGraceLimit ||
            lease.AccessValidUntil < lease.ExpiresAt || lease.AccessValidUntil != runToken.AccessValidUntil ||
            lease.RevocationSequence != runToken.RevocationSequence ||
            lease.RevocationSequence < _revocationState.LastSequence ||
            lease.ExpiresAt - lease.IssuedAt > MaximumOfflineGrace)
        {
            throw new CryptographicException("The signed DAXQ offline lease has invalid binding or lifetime.");
        }
        return lease.ExpiresAt > runToken.ExpiresAt ? lease.ExpiresAt : runToken.ExpiresAt;
    }

    private byte[] ValidateChallenge(DaxqChallengeResponse challenge)
    {
        var now = _timeProvider.GetUtcNow();
        if (challenge.ChallengeId == Guid.Empty || challenge.ExpiresAt <= now ||
            challenge.ExpiresAt > now + TimeSpan.FromMinutes(10))
            throw new InvalidDataException("The device-proof challenge is expired or malformed.");
        var nonce = DaxqCryptography.DecodeBase64Url(challenge.Nonce);
        if (nonce.Length is < 16 or > 64)
        {
            CryptographicOperations.ZeroMemory(nonce);
            throw new InvalidDataException("The device-proof challenge nonce has an invalid size.");
        }
        return nonce;
    }

    private static void ValidateContext(DaxqDeliveryContext context)
    {
        if (context.LicenseId == Guid.Empty || context.ReleaseId == Guid.Empty)
            throw new InvalidOperationException("The DAXQ delivery context omitted a license or release id.");
    }

    private static void ValidateContentKeyResponse(
        DaxqContentKeyResponse response,
        DaxqManifest manifest)
    {
        if (response.KeyWrapAlgorithm != DaxqCryptography.KeyWrapAlgorithm ||
            response.ContentAlgorithm != DaxqCryptography.ContentAlgorithm ||
            response.ContentKeyId != manifest.Protection.ContentKeyId ||
            response.TtlSeconds <= 0 || response.TtlSeconds > 3_600)
        {
            throw new CryptographicException("The content-key response is not bound to this DAXQ release.");
        }
    }

    private static byte[] Decrypt(
        DaxqManifest manifest,
        byte[] ciphertextAndTag,
        byte[] contentKey)
    {
        if (ciphertextAndTag.Length <= DaxqFormat.AuthenticationTagSizeBytes)
            throw new InvalidDataException("strategy.dqx is shorter than its authentication tag.");
        var nonce = DaxqCryptography.DecodeBase64Url(
            manifest.Protection.Nonce,
            DaxqFormat.NonceSizeBytes);
        var plaintext = new byte[ciphertextAndTag.Length - DaxqFormat.AuthenticationTagSizeBytes];
        try
        {
            using var aes = new AesGcm(contentKey, DaxqFormat.AuthenticationTagSizeBytes);
            aes.Decrypt(
                nonce,
                ciphertextAndTag.AsSpan(0, plaintext.Length),
                ciphertextAndTag.AsSpan(plaintext.Length),
                plaintext,
                ReadOnlySpan<byte>.Empty);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static bool IsLowerHexTokenId(string? value) =>
        value is { Length: 32 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record DaxqHeartbeatRefresh(
    DaxqSignedEnvelope RunToken,
    DaxqLicenseTokenClaims Claims,
    DaxqSignedEnvelope? OfflineLease,
    DateTimeOffset OfflineExpiry);

internal sealed class DaxqHeartbeatController : IDisposable
{
    private readonly object _nativeGate = new();
    private readonly DaxqLicensingRuntime _runtime;
    private readonly LoadedDaxqPackage _package;
    private readonly string _pluginName;
    private readonly DaxqDeliveryContext _context;
    private readonly DaxqDeviceIdentity _identity;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private DaxqSignedEnvelope _runToken;
    private DaxqLicenseTokenClaims _claims;
    private DaxqSignedEnvelope? _offlineLease;
    private DateTimeOffset _offlineExpiry;
    private DateTimeOffset _nextAttemptAt;
    private Task? _loop;
    private DaxqNativeVm? _nativeVm;
    private int _disposed;

    public DaxqHeartbeatController(
        DaxqLicensingRuntime runtime,
        LoadedDaxqPackage package,
        string pluginName,
        DaxqDeliveryContext context,
        DaxqDeviceIdentity identity,
        DaxqSignedEnvelope runToken,
        DaxqLicenseTokenClaims claims,
        DaxqSignedEnvelope? offlineLease,
        DateTimeOffset offlineExpiry,
        TimeSpan interval,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _runtime = runtime;
        _package = package;
        _pluginName = pluginName;
        _context = context;
        _identity = identity;
        _runToken = runToken;
        _claims = claims;
        _offlineLease = offlineLease;
        _offlineExpiry = offlineExpiry;
        _interval = interval;
        _timeProvider = timeProvider;
        _logger = logger;
        _nextAttemptAt = CalculateNextAttempt(_timeProvider.GetUtcNow(), claims);
    }

    public DaxqLicenseGate Gate { get; } = new();

    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    public void AttachNativeVm(DaxqNativeVm vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        lock (_nativeGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !Gate.IsAuthorized)
                throw new DaxqLicenseDeniedException(Gate.Reason);
            _runtime.ApplyNativeLicenseEvidence(vm, _runToken);
            if (_offlineLease is not null)
                _runtime.ApplyNativeLicenseEvidence(vm, _offlineLease);
            _nativeVm = vm;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        Revoke("The DAXQ strategy session ended.");
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _timeProvider.GetUtcNow();
                var remaining = _offlineExpiry - now;
                if (remaining <= TimeSpan.Zero)
                {
                    Revoke("The signed DAXQ offline authorization expired.");
                    return;
                }
                var untilAttempt = _nextAttemptAt - now;
                if (untilAttempt < TimeSpan.Zero)
                    untilAttempt = TimeSpan.Zero;
                var delay = remaining < untilAttempt ? remaining : untilAttempt;
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                if (_timeProvider.GetUtcNow() >= _offlineExpiry)
                {
                    Revoke("The signed DAXQ offline authorization expired.");
                    return;
                }

                try
                {
                    var refreshed = await RefreshWithinOfflineWindowAsync(cancellationToken)
                        .ConfigureAwait(false);
                    lock (_nativeGate)
                    {
                        if (_nativeVm is not null)
                        {
                            _runtime.ApplyNativeLicenseEvidence(_nativeVm, refreshed.RunToken);
                            if (refreshed.OfflineLease is not null)
                                _runtime.ApplyNativeLicenseEvidence(_nativeVm, refreshed.OfflineLease);
                        }
                        _offlineLease = refreshed.OfflineLease;
                    }
                    _runToken = refreshed.RunToken;
                    _claims = refreshed.Claims;
                    _offlineExpiry = refreshed.OfflineExpiry;
                    _nextAttemptAt = CalculateNextAttempt(_timeProvider.GetUtcNow(), _claims);
                }
                catch (DaxqLicenseDeniedException exception)
                {
                    Revoke(exception.Message);
                    return;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException or TimeoutException)
                {
                    if (_timeProvider.GetUtcNow() >= _offlineExpiry)
                    {
                        Revoke("The DAXQ licensing service stayed unavailable beyond the signed offline lease.");
                        return;
                    }
                    _logger.LogWarning(
                        "DAXQ licensing heartbeat unavailable for {PluginName}; signed offline lease remains in force",
                        _pluginName);
                    _nextAttemptAt = _timeProvider.GetUtcNow() +
                                     (_interval < TimeSpan.FromMinutes(1)
                                         ? _interval
                                         : TimeSpan.FromMinutes(1));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (_timeProvider.GetUtcNow() >= _offlineExpiry)
                    {
                        Revoke("The DAXQ licensing service stayed unavailable beyond the signed offline lease.");
                        return;
                    }
                    _logger.LogWarning(
                        "DAXQ licensing heartbeat timed out for {PluginName}; signed offline lease remains in force",
                        _pluginName);
                    _nextAttemptAt = _timeProvider.GetUtcNow() +
                                     (_interval < TimeSpan.FromMinutes(1)
                                         ? _interval
                                         : TimeSpan.FromMinutes(1));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (CryptographicException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "DAXQ licensing evidence failed verification for {PluginName}",
                        _pluginName);
                    Revoke("DAXQ licensing evidence failed verification.");
                    return;
                }
                catch (InvalidDataException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "DAXQ licensing response was invalid for {PluginName}",
                        _pluginName);
                    Revoke("The DAXQ licensing response was invalid.");
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "DAXQ licensing heartbeat failed closed for {PluginName}",
                        _pluginName);
                    Revoke("The DAXQ licensing heartbeat failed validation.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "DAXQ licensing supervision failed closed for {PluginName}",
                _pluginName);
            Revoke("The DAXQ licensing supervisor failed closed.");
        }
    }

    private async Task<DaxqHeartbeatRefresh> RefreshWithinOfflineWindowAsync(
        CancellationToken cancellationToken)
    {
        var remaining = _offlineExpiry - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("The signed DAXQ offline authorization expired.");

        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCancellation.CancelAfter(remaining);
        var pending = _runtime.RefreshAsync(
                _package,
                _context,
                _identity,
                _runToken,
                _claims,
                attemptCancellation.Token)
            .AsTask();
        try
        {
            return await pending.WaitAsync(remaining, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            attemptCancellation.Cancel();
            if (!pending.IsCompleted)
                ObserveFault(pending);
            throw;
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void Revoke(string reason)
    {
        lock (_nativeGate)
        {
            _ = _nativeVm?.RevokeLicense();
            Gate.Revoke(reason);
        }
    }

    private DateTimeOffset CalculateNextAttempt(
        DateTimeOffset now,
        DaxqLicenseTokenClaims claims)
    {
        var lifetimeTicks = Math.Max(1, (claims.ExpiresAt - claims.IssuedAt).Ticks);
        var lead = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks, lifetimeTicks / 10));
        var beforeExpiry = claims.ExpiresAt - lead;
        var byInterval = now + _interval;
        return beforeExpiry < byInterval ? beforeExpiry : byInterval;
    }
}

internal sealed class DaxqRevocationState
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("DaxAlgoTerminal.Daxq.Revocations.v1");
    private readonly object _gate = new();
    private readonly string? _statePath;
    private readonly DaxqEs256PublicKeyRing _trust;
    private readonly List<DaxqRevocationEntry> _entries = [];
    private DaxqSignedEnvelope? _lastEnvelope;
    private long _lastSequence;

    public DaxqRevocationState(string? statePath, DaxqEs256PublicKeyRing trust)
    {
        _statePath = string.IsNullOrWhiteSpace(statePath) ? null : Path.GetFullPath(statePath);
        _trust = trust;
        if (_statePath is not null && File.Exists(_statePath))
            Load();
    }

    public long LastSequence
    {
        get
        {
            lock (_gate) return _lastSequence;
        }
    }

    public IReadOnlyList<DaxqRevocationEntry> Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public void Accept(
        DaxqRevocationFeedClaims feed,
        DaxqSignedEnvelope envelope,
        long requestedSequence)
    {
        lock (_gate)
        {
            if (requestedSequence != _lastSequence || feed.FromSequence != requestedSequence ||
                feed.ThroughSequence < _lastSequence)
                throw new CryptographicException("The signed DAXQ revocation feed attempted a rollback.");
            var prior = _lastSequence;
            foreach (var entry in feed.Revocations)
            {
                if (entry.Sequence <= prior || entry.Sequence > feed.ThroughSequence ||
                    entry.IssuedAt > feed.IssuedAt + TimeSpan.FromMinutes(2))
                    throw new CryptographicException("The signed DAXQ revocation sequence is not monotonic.");
                prior = entry.Sequence;
            }
            if ((feed.Revocations.Count == 0 && feed.ThroughSequence != requestedSequence) ||
                (feed.Revocations.Count != 0 && prior != feed.ThroughSequence))
                throw new CryptographicException("The signed DAXQ revocation feed has an incomplete sequence.");
            _entries.AddRange(feed.Revocations);
            _lastSequence = feed.ThroughSequence;
            _lastEnvelope = envelope;
            Save();
        }
    }

    private void Load()
    {
        var ciphertext = File.ReadAllBytes(_statePath!);
        byte[]? plaintext = null;
        try
        {
            plaintext = DaxqPlatformDataProtection.Unprotect(
                ciphertext,
                Entropy,
                "revocations-v1");
            var stored = JsonSerializer.Deserialize<StoredRevocationState>(plaintext) ??
                         throw new InvalidDataException("The persisted DAXQ revocation state is empty.");
            if (stored.SchemaVersion != 1 || stored.LastSequence < 0 || stored.LastEnvelope is null)
                throw new InvalidDataException("The persisted DAXQ revocation state is malformed.");
            var checkpoint = DaxqCryptography.VerifyEnvelope<DaxqRevocationFeedClaims>(
                stored.LastEnvelope,
                _trust);
            var checkpointLast = checkpoint.Revocations.Count == 0
                ? checkpoint.FromSequence
                : checkpoint.Revocations[^1].Sequence;
            if (checkpoint.ThroughSequence != stored.LastSequence ||
                checkpoint.FromSequence > checkpoint.ThroughSequence ||
                checkpointLast != checkpoint.ThroughSequence)
                throw new CryptographicException("The persisted DAXQ revocation checkpoint does not match its cursor.");
            long prior = 0;
            foreach (var entry in stored.Entries)
            {
                if (entry.Sequence <= prior || entry.Sequence > stored.LastSequence)
                    throw new CryptographicException("The persisted DAXQ revocation entries are not monotonic.");
                prior = entry.Sequence;
            }
            _entries.AddRange(stored.Entries);
            _lastSequence = stored.LastSequence;
            _lastEnvelope = stored.LastEnvelope;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The persisted DAXQ revocation state is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void Save()
    {
        if (_statePath is null)
            return;
        var stored = new StoredRevocationState(
            1,
            _lastSequence,
            _entries.ToArray(),
            _lastEnvelope ?? throw new InvalidOperationException("A signed revocation checkpoint is required."));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(stored);
        byte[]? ciphertext = null;
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ciphertext = DaxqPlatformDataProtection.Protect(
                plaintext,
                Entropy,
                "revocations-v1");
            var directory = Path.GetDirectoryName(_statePath) ??
                            throw new InvalidOperationException("The revocation-state path has no directory.");
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    private sealed record StoredRevocationState(
        int SchemaVersion,
        long LastSequence,
        IReadOnlyList<DaxqRevocationEntry> Entries,
        DaxqSignedEnvelope LastEnvelope);
}
