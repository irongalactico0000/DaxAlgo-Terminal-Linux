using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace TradingTerminal.App.Avalonia.Theming;

/// <summary>A selected theme file and its open stream.</summary>
public sealed class ThemeFileHandle : IAsyncDisposable
{
    public ThemeFileHandle(string displayName, Stream stream)
    {
        DisplayName = displayName;
        Stream = stream;
    }

    public string DisplayName { get; }

    public Stream Stream { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>Testable file-selection seam used by Theme Studio.</summary>
public interface IThemeFilePicker
{
    Task<ThemeFileHandle?> OpenThemeAsync();

    Task<ThemeFileHandle?> SaveThemeAsync(string suggestedFileName);
}

/// <summary>
/// Avalonia storage-provider implementation. It works with macOS security-scoped storage handles
/// and streams directly instead of assuming that a picker result exposes a local filesystem path.
/// </summary>
public sealed class AvaloniaThemeFilePicker : IThemeFilePicker
{
    private static readonly FilePickerFileType ThemeJson = new("DaxAlgo theme JSON")
    {
        Patterns = new[] { "*.json" },
        MimeTypes = new[] { "application/json" },
        AppleUniformTypeIdentifiers = new[] { "public.json" },
    };

    private readonly Func<TopLevel?> _topLevel;

    public AvaloniaThemeFilePicker(Func<TopLevel?> topLevel)
    {
        _topLevel = topLevel;
    }

    public async Task<ThemeFileHandle?> OpenThemeAsync()
    {
        var storage = _topLevel()?.StorageProvider;
        if (storage is null)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import DaxAlgo theme",
            AllowMultiple = false,
            FileTypeFilter = new[] { ThemeJson },
        });
        if (files.Count == 0)
            return null;

        var file = files[0];
        return new ThemeFileHandle(file.Name, await file.OpenReadAsync());
    }

    public async Task<ThemeFileHandle?> SaveThemeAsync(string suggestedFileName)
    {
        var storage = _topLevel()?.StorageProvider;
        if (storage is null)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DaxAlgo theme",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { ThemeJson },
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return null;

        var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.Position = 0;
            stream.SetLength(0);
        }

        return new ThemeFileHandle(file.Name, stream);
    }
}
