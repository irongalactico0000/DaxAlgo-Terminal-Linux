using System.Text.Json.Serialization;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login;

/// <summary>
/// On-disk shape for persisted connection settings. Secret properties serialize only opaque
/// references to the current user's macOS Keychain (or AES-GCM ciphertext on non-macOS test hosts).
/// </summary>
public sealed class StoredCredentials
{
    /// <summary>Which broker the user last signed in with. Drives the form shown on next launch.</summary>
    public BrokerKind SelectedBroker { get; set; } = BrokerKind.InteractiveBrokers;

    /// <summary>When true, the login window fires every available broker's Connect on startup
    /// (each form using its own persisted credentials) instead of waiting for manual clicks.</summary>
    public bool AutoConnect { get; set; }

    public string? Username { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;
    public int ClientId { get; set; } = 1;
    public string AccountType { get; set; } = "Paper";
    public int MarketDataType { get; set; } = 1;
    public bool RememberPassword { get; set; }

    // ---- NinjaTrader-specific fields ----
    public string NinjaAccountName { get; set; } = "Sim101";
    public string NinjaDllPath { get; set; } = string.Empty;
    public string NinjaFuturesContractMonth { get; set; } = string.Empty;

    // ---- cTrader-specific fields ----
    public string CTraderClientId { get; set; } = string.Empty;
    public long CTraderAccountId { get; set; }
    public bool CTraderIsLive { get; set; }

    /// <summary>Opaque protected reference for the OAuth client secret.</summary>
    public string? CTraderClientSecretEncryptedBase64 { get; set; }
    /// <summary>Opaque protected reference for the OAuth access token.</summary>
    public string? CTraderAccessTokenEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? CTraderClientSecret
    {
        get => PlatformSecretStore.Unprotect(CTraderClientSecretEncryptedBase64);
        set => CTraderClientSecretEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.ctrader.client-secret", value, CTraderClientSecretEncryptedBase64);
    }

    [JsonIgnore]
    public string? CTraderAccessToken
    {
        get => PlatformSecretStore.Unprotect(CTraderAccessTokenEncryptedBase64);
        set => CTraderAccessTokenEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.ctrader.access-token", value, CTraderAccessTokenEncryptedBase64);
    }

    // ---- IronBeam-specific fields ----
    public string? IronBeamUsername { get; set; }
    public bool IronBeamIsLive { get; set; }

    /// <summary>Opaque protected reference for the IronBeam API key.</summary>
    public string? IronBeamApiKeyEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? IronBeamApiKey
    {
        get => PlatformSecretStore.Unprotect(IronBeamApiKeyEncryptedBase64);
        set => IronBeamApiKeyEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.ironbeam.api-key", value, IronBeamApiKeyEncryptedBase64);
    }

    // ---- London Strategic Edge-specific fields ----

    /// <summary>Opaque protected reference for the London Strategic Edge API key.</summary>
    public string? LondonStrategicEdgeApiKeyEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? LondonStrategicEdgeApiKey
    {
        get => PlatformSecretStore.Unprotect(LondonStrategicEdgeApiKeyEncryptedBase64);
        set => LondonStrategicEdgeApiKeyEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.lse.api-key", value, LondonStrategicEdgeApiKeyEncryptedBase64);
    }

    // ---- Alpaca-specific fields ----
    public string AlpacaApiKey { get; set; } = string.Empty;
    public bool AlpacaIsLive { get; set; }
    public string AlpacaStockDataFeed { get; set; } = "iex";

    /// <summary>Opaque protected reference for the Alpaca API secret.</summary>
    public string? AlpacaApiSecretEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? AlpacaApiSecret
    {
        get => PlatformSecretStore.Unprotect(AlpacaApiSecretEncryptedBase64);
        set => AlpacaApiSecretEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.alpaca.api-secret", value, AlpacaApiSecretEncryptedBase64);
    }

    // ---- Upstox-specific fields ----
    public string UpstoxApiKey { get; set; } = string.Empty;
    public string UpstoxRedirectUri { get; set; } = string.Empty;

    /// <summary>Opaque protected reference for the Upstox OAuth client secret.</summary>
    public string? UpstoxApiSecretEncryptedBase64 { get; set; }

    /// <summary>Opaque protected reference for the Upstox access token (expires daily ~03:30 IST).</summary>
    public string? UpstoxAccessTokenEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? UpstoxApiSecret
    {
        get => PlatformSecretStore.Unprotect(UpstoxApiSecretEncryptedBase64);
        set => UpstoxApiSecretEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.upstox.api-secret", value, UpstoxApiSecretEncryptedBase64);
    }

    [JsonIgnore]
    public string? UpstoxAccessToken
    {
        get => PlatformSecretStore.Unprotect(UpstoxAccessTokenEncryptedBase64);
        set => UpstoxAccessTokenEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.upstox.access-token", value, UpstoxAccessTokenEncryptedBase64);
    }

    /// <summary>Opaque protected reference. Null when password is not remembered.</summary>
    public string? PasswordEncryptedBase64 { get; set; }

    [JsonIgnore]
    public string? Password
    {
        get => PlatformSecretStore.Unprotect(PasswordEncryptedBase64);
        set => PasswordEncryptedBase64 = PlatformSecretStore.Protect(
            "broker.ib.password", value, PasswordEncryptedBase64);
    }

    internal void ClearProtectedSecrets()
    {
        Password = null;
        CTraderClientSecret = null;
        CTraderAccessToken = null;
        IronBeamApiKey = null;
        LondonStrategicEdgeApiKey = null;
        AlpacaApiSecret = null;
        UpstoxApiSecret = null;
        UpstoxAccessToken = null;
    }
}
