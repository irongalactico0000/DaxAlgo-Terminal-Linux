using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

namespace TradingTerminal.App.Archive;

/// <summary>
/// App-layer implementation of the <see cref="ITelegramArchiveLogin"/> seam used by the login
/// window. Reuses the archive settings persistence and the existing Telegram transport, with the
/// Avalonia verification-code / 2FA prompt supplying interactive values.
/// </summary>
public sealed class TelegramArchiveLogin : ITelegramArchiveLogin
{
    private readonly IOptionsMonitor<ArchiveOptions> _archiveOpts;
    private readonly IOptionsMonitor<TelegramArchiveOptions> _telegramOpts;
    private readonly TelegramArchiveTransport _transport;
    private readonly ILogger<TelegramArchiveLogin> _logger;

    public TelegramArchiveLogin(
        IOptionsMonitor<ArchiveOptions> archiveOpts,
        IOptionsMonitor<TelegramArchiveOptions> telegramOpts,
        TelegramArchiveTransport transport,
        ILogger<TelegramArchiveLogin> logger)
    {
        _archiveOpts = archiveOpts;
        _telegramOpts = telegramOpts;
        _transport = transport;
        _logger = logger;
    }

    public bool IsConnected => _transport.IsReady;

    public TelegramArchiveCredentials Load()
    {
        var t = _telegramOpts.CurrentValue;
        return new TelegramArchiveCredentials(t.ApiId, t.ApiHash, t.PhoneNumber);
    }

    public async Task<TelegramArchiveLoginResult> ConnectAsync(
        TelegramArchiveCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (credentials.ApiId <= 0)
            return new TelegramArchiveLoginResult(false, "Enter your Telegram api_id (a number from my.telegram.org/apps).");
        if (string.IsNullOrWhiteSpace(credentials.ApiHash))
            return new TelegramArchiveLoginResult(false, "Enter your Telegram api_hash (from my.telegram.org/apps).");
        if (string.IsNullOrWhiteSpace(credentials.PhoneNumber))
            return new TelegramArchiveLoginResult(false, "Enter your phone number in international format (e.g. +91XXXXXXXXXX).");

        var snap = new TelegramArchiveOptions
        {
            ApiId = credentials.ApiId,
            ApiHash = credentials.ApiHash.Trim(),
            PhoneNumber = credentials.PhoneNumber.Trim(),
            SessionFilePath = _telegramOpts.CurrentValue.SessionFilePath,
        };

        ArchiveUserFile.Save(_archiveOpts.CurrentValue, snap);

        try
        {
            await Task.Run(
                    () => _transport.EnsureConnectedAsync(snap, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return _transport.IsReady
                ? new TelegramArchiveLoginResult(true, "Connected.")
                : new TelegramArchiveLoginResult(false, "Login did not complete.");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Telegram archive login canceled: {Reason}", ex.Message);
            return new TelegramArchiveLoginResult(false, $"Login canceled: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram archive login failed");
            return new TelegramArchiveLoginResult(false, $"Login failed: {ex.Message}");
        }
    }
}
