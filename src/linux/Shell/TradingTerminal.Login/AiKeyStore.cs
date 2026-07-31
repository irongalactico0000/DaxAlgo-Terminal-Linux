using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.App.Login;

/// <summary>
/// Persists AI-provider API keys, one per provider id, in the current user's macOS Keychain.
/// The JSON file stores opaque Keychain references only (AES-GCM ciphertext on non-macOS test hosts).
/// Kept separate from the broker <see cref="CredentialStore"/> so AI setup and trading credentials
/// don't share a file. Read by the codegen key resolver; written by the AI-providers settings section.
/// </summary>
public sealed class AiKeyStore : IAiKeyStore
{
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgoTerminal");
    private static readonly string FilePath = Path.Combine(Directory, "ai-keys.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<AiKeyStore> _logger;
    private readonly object _gate = new();
    private Dictionary<string, string> _encrypted; // providerId -> protected reference

    public AiKeyStore(ILogger<AiKeyStore> logger)
    {
        _logger = logger;
        _encrypted = Load();
    }

    /// <summary>Provider ids that currently have a stored key.</summary>
    public IReadOnlyCollection<string> ConfiguredProviders
    {
        get { lock (_gate) return _encrypted.Keys.ToArray(); }
    }

    public bool HasKey(string providerId)
    {
        lock (_gate) return _encrypted.ContainsKey(providerId);
    }

    /// <summary>The decrypted key for <paramref name="providerId"/>, or null when none is stored (or it
    /// can't be decrypted — a machine/user change).</summary>
    public string? Get(string providerId)
    {
        lock (_gate)
            return _encrypted.TryGetValue(providerId, out var enc) ? PlatformSecretStore.Unprotect(enc) : null;
    }

    public void Set(string providerId, string apiKey)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) { Remove(providerId); return; }
            _encrypted.TryGetValue(providerId, out var existing);
            _encrypted[providerId] = PlatformSecretStore.Protect(
                $"ai.{providerId}", apiKey, existing)
                ?? throw new InvalidOperationException("The AI key could not be protected.");
            Save();
        }
    }

    public void Remove(string providerId)
    {
        lock (_gate)
        {
            if (_encrypted.Remove(providerId, out var reference))
            {
                PlatformSecretStore.Delete(reference);
                Save();
            }
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath), JsonOptions)
                is { } d ? new(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read AI key store; starting fresh");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_encrypted, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist AI keys");
        }
    }

}
