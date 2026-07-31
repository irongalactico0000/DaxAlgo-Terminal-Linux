using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.AiAnalyst;

/// <summary>
/// Configuration for the AI Market Analyst feature. The provider key is protected by the current
/// macOS user's login Keychain and is never stored in plaintext or in appsettings.json.
/// </summary>
public sealed class AiAnalystOptions
{
    /// <summary>True when the user has wired up the Python sidecar.</summary>
    public bool Enabled { get; set; }

    /// <summary>Loopback URL of the FastAPI sidecar.</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:8765";

    /// <summary>LLM family routed through the sidecar.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Text/reasoning model id.</summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>Vision-capable model id used for chart analysis.</summary>
    public string VisionModel { get; set; } = "gpt-4o";

    /// <summary>Number of bars sent to the analyst on each run.</summary>
    public int BarCount { get; set; } = 50;

    /// <summary>Wall-clock ceiling for one analyst request.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Append an AI verdict to Signal and Trade notifications.</summary>
    public bool IncludeInEnricher { get; set; }

    /// <summary>
    /// Base64-encoded authenticated ciphertext. The encryption key remains in the macOS Keychain.
    /// </summary>
    public string? ApiKeyEncryptedBase64 { get; set; }

    /// <summary>Plaintext API key exposed only in memory.</summary>
    [JsonIgnore]
    public string? ApiKey
    {
        get => AiAnalystCredentialProtection.Decrypt(ApiKeyEncryptedBase64);
        set => ApiKeyEncryptedBase64 = AiAnalystCredentialProtection.Encrypt(value);
    }
}
