using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.UI;

namespace TradingTerminal.App.Avalonia.Theming;

/// <summary>
/// Live palette editor with built-in base selection, complete token snapshots, custom-theme
/// persistence, and import/export through Avalonia's storage-provider seam.
/// </summary>
public sealed partial class ThemeStudioViewModel : ViewModelBase
{
    private static readonly string[] GroupOrder =
    {
        "Backgrounds",
        "Surfaces",
        "Borders",
        "Text",
        "Accent",
        "Semantic (P&L / status)",
        "Gradients",
        "AI (glass & gradients)",
        "Other",
    };

    private readonly IThemeManager _manager;
    private readonly IThemeFilePicker _filePicker;
    private bool _applyingBase;

    public ThemeStudioViewModel(IThemeManager manager, IThemeFilePicker filePicker)
    {
        _manager = manager;
        _filePicker = filePicker;

        BaseThemes = new ObservableCollection<ThemeDefinition>(
            manager.Themes.Where(theme => !theme.Id.StartsWith("custom.", StringComparison.Ordinal)));
        _selectedBaseTheme = BaseThemes.FirstOrDefault(theme =>
            string.Equals(theme.Id, manager.CurrentBaseThemeId, StringComparison.OrdinalIgnoreCase))
            ?? BaseThemes.FirstOrDefault();

        Groups = new ObservableCollection<ThemeTokenGroupViewModel>();
        RebuildTokens();
    }

    public ObservableCollection<ThemeDefinition> BaseThemes { get; }

    public ObservableCollection<ThemeTokenGroupViewModel> Groups { get; }

    [ObservableProperty]
    private ThemeDefinition? _selectedBaseTheme;

    [ObservableProperty]
    private string _newThemeName = "My Theme";

    [ObservableProperty]
    private string? _statusMessage;

    partial void OnSelectedBaseThemeChanged(ThemeDefinition? value)
    {
        if (_applyingBase || value is null)
            return;

        _manager.Apply(value.Id);
        RebuildTokens();
        StatusMessage = $"Started from '{value.Name}'.";
    }

    private void RebuildTokens()
    {
        Groups.Clear();
        var byGroup = _manager.EnumerateTokens()
            .GroupBy(token => token.Group)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var name in GroupOrder)
        {
            if (!byGroup.TryGetValue(name, out var tokens))
                continue;
            Groups.Add(BuildGroup(name, tokens));
        }

        foreach (var (name, tokens) in byGroup)
        {
            if (!GroupOrder.Contains(name))
                Groups.Add(BuildGroup(name, tokens));
        }
    }

    private ThemeTokenGroupViewModel BuildGroup(string name, IEnumerable<ThemeToken> tokens)
    {
        var group = new ThemeTokenGroupViewModel(name);
        foreach (var token in tokens)
            group.Tokens.Add(new ThemeTokenViewModel(_manager, token));
        return group;
    }

    [RelayCommand]
    private void ResetAll()
    {
        if (SelectedBaseTheme is null)
            return;

        _manager.Apply(SelectedBaseTheme.Id);
        RebuildTokens();
        StatusMessage = $"Reset to '{SelectedBaseTheme.Name}'.";
    }

    [RelayCommand]
    private void Save()
    {
        var file = BuildFile();
        var definition = _manager.RegisterCustomTheme(file);
        ApplyWithoutResetting(definition.Id);
        StatusMessage = $"Saved '{file.Name}'. It is now available in the Theme menu.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        ThemeFileHandle? target = null;
        try
        {
            target = await _filePicker.SaveThemeAsync(SanitizeFileName(NewThemeName) + ".json");
            if (target is null)
                return;

            await using (target)
            {
                _manager.ExportThemeFile(BuildFile(), target.Stream);
                await target.Stream.FlushAsync();
            }
            StatusMessage = $"Exported to {target.DisplayName}.";
        }
        catch (Exception exception)
        {
            StatusMessage = "Export failed: " + exception.Message;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        ThemeFileHandle? source = null;
        try
        {
            source = await _filePicker.OpenThemeAsync();
            if (source is null)
                return;

            CustomThemeFile file;
            await using (source)
                file = _manager.ImportThemeFile(source.Stream);

            var definition = _manager.RegisterCustomTheme(file);
            ApplyWithoutResetting(definition.Id);
            NewThemeName = file.Name;
            StatusMessage = $"Imported '{file.Name}' and applied it.";
        }
        catch (Exception exception)
        {
            StatusMessage = "Import failed: " + exception.Message;
        }
    }

    private void ApplyWithoutResetting(string themeId)
    {
        _manager.Apply(themeId);
        _applyingBase = true;
        SelectedBaseTheme = BaseThemes.FirstOrDefault(theme =>
            string.Equals(theme.Id, _manager.CurrentBaseThemeId, StringComparison.OrdinalIgnoreCase))
            ?? SelectedBaseTheme;
        _applyingBase = false;
        RebuildTokens();
    }

    private CustomThemeFile BuildFile()
    {
        var file = new CustomThemeFile
        {
            Name = string.IsNullOrWhiteSpace(NewThemeName) ? "Custom" : NewThemeName.Trim(),
            BaseThemeId = SelectedBaseTheme?.Id ?? "daxalgo-dark",
        };

        foreach (var group in Groups)
        {
            foreach (var token in group.Tokens)
            {
                if (token.IsGradient)
                {
                    file.Gradients[token.PrimaryKey] = token.Stops
                        .Select(stop => ThemeColorUtil.ToHex(stop.Color))
                        .ToList();
                }
                else
                {
                    file.Colors[token.PrimaryKey] = ThemeColorUtil.ToHex(token.Color);
                    if (token.LinkedColorKey is not null)
                        file.Colors[token.LinkedColorKey] = ThemeColorUtil.ToHex(token.Color);
                }
            }
        }

        return file;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "theme" : cleaned;
    }
}

/// <summary>A named, collapsible group of token editors.</summary>
public sealed class ThemeTokenGroupViewModel
{
    public ThemeTokenGroupViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ObservableCollection<ThemeTokenViewModel> Tokens { get; } = new();
}
