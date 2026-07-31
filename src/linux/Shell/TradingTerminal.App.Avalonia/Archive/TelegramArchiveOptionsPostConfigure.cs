using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Rehydrates the platform-protected Telegram values after configuration binding. Legacy plaintext
/// values remain readable and are upgraded to protected ciphertext on the next save.
/// </summary>
internal sealed class TelegramArchiveOptionsPostConfigure : IPostConfigureOptions<TelegramArchiveOptions>
{
    public void PostConfigure(string? name, TelegramArchiveOptions options)
    {
        if (!string.IsNullOrEmpty(options.ApiHashEncryptedBase64))
        {
            var decrypted = TelegramArchiveCredentialProtection.Decrypt(options.ApiHashEncryptedBase64);
            if (!string.IsNullOrEmpty(decrypted)) options.ApiHash = decrypted;
        }

        if (!string.IsNullOrEmpty(options.PhoneNumberEncryptedBase64))
        {
            var decrypted = TelegramArchiveCredentialProtection.Decrypt(options.PhoneNumberEncryptedBase64);
            if (!string.IsNullOrEmpty(decrypted)) options.PhoneNumber = decrypted;
        }
    }
}
