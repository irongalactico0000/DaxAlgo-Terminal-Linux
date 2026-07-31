using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace TradingTerminal.App.Avalonia.Theming;

/// <summary>A selectable application theme and its compiled Avalonia palette resource.</summary>
public sealed record ThemeDefinition(string Id, string Name, string PaletteUri);

/// <summary>
/// Swaps the active DaxAlgo palette, applies live token overrides, and persists built-in or custom
/// theme selections. The contract mirrors the current Windows theme manager while using Avalonia
/// resources and theme variants.
/// </summary>
public interface IThemeManager
{
    IReadOnlyList<ThemeDefinition> Themes { get; }

    string CurrentThemeId { get; }

    string CurrentBaseThemeId { get; }

    event EventHandler? ThemesChanged;

    void Apply(string themeId);

    void ApplySaved();

    IReadOnlyList<ThemeToken> EnumerateTokens();

    Color? ReadColor(string key);

    LinearGradientBrush? ReadGradient(string key);

    void SetColorOverride(string key, Color value);

    void SetGradientOverride(string key, IReadOnlyList<Color> stops);

    ThemeDefinition RegisterCustomTheme(CustomThemeFile file);

    void ExportThemeFile(CustomThemeFile file, string path);

    void ExportThemeFile(CustomThemeFile file, Stream destination);

    CustomThemeFile ImportThemeFile(string path);

    CustomThemeFile ImportThemeFile(Stream source);

    bool TryGetCustomTheme(string id, out CustomThemeFile file);
}

/// <inheritdoc cref="IThemeManager" />
public sealed class ThemeManager : IThemeManager
{
    private const string ResourceRoot = "avares://TradingTerminal.App.Avalonia/Themes/";
    private const string PaletteSentinel = "Background.Primary.Color";

    private static readonly ThemeDefinition[] Builtins =
    {
        new("daxalgo-dark", "DaxAlgo Dark", ResourceRoot + "Palette.axaml"),
        new("daxalgo-light", "DaxAlgo Light", ResourceRoot + "Palette.Light.axaml"),
    };

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal");
    private static readonly string PrefFile = Path.Combine(AppDataDir, "theme.txt");
    private static readonly string ThemesDir = Path.Combine(AppDataDir, "themes");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, CustomThemeFile> _customs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _overrideKeys = new(StringComparer.Ordinal);
    private List<ThemeDefinition> _all = new(Builtins);
    private IResourceProvider? _activePaletteProvider;
    private ResourceDictionary? _activePalette;

    public event EventHandler? ThemesChanged;

    public IReadOnlyList<ThemeDefinition> Themes => _all;

    public string CurrentThemeId { get; private set; } = Builtins[0].Id;

    public string CurrentBaseThemeId =>
        _customs.TryGetValue(CurrentThemeId, out var custom)
            ? ResolveBuiltin(custom.BaseThemeId).Id
            : CurrentThemeId;

    public void ApplySaved()
    {
        LoadCustomThemes();
        Apply(LoadSavedId());
    }

    public void Apply(string themeId)
    {
        if (Application.Current is null)
            return;

        if (_customs.TryGetValue(themeId, out var custom))
        {
            SwapPalette(custom.BaseThemeId);
            foreach (var (key, hex) in custom.Colors)
                SetColorOverride(key, ParseColor(hex));
            foreach (var (key, stops) in custom.Gradients)
                SetGradientOverride(key, stops.Select(ParseColor).ToList());

            CurrentThemeId = themeId;
            SaveId(themeId);
            return;
        }

        var definition = ResolveBuiltin(themeId);
        SwapPalette(definition.Id);
        CurrentThemeId = definition.Id;
        SaveId(definition.Id);
    }

    private void SwapPalette(string builtinId)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var definition = ResolveBuiltin(builtinId);
        ClearOverrides();

        var palette = AvaloniaXamlLoader.Load(new Uri(definition.PaletteUri)) as ResourceDictionary
            ?? throw new InvalidDataException($"Theme palette '{definition.PaletteUri}' is not a resource dictionary.");

        var dictionaries = app.Resources.MergedDictionaries;
        var index = _activePaletteProvider is null
            ? FindPaletteIndex(dictionaries)
            : dictionaries.IndexOf(_activePaletteProvider);

        if (index >= 0)
            dictionaries[index] = palette;
        else
        {
            dictionaries.Insert(0, palette);
            index = 0;
        }

        _activePaletteProvider = dictionaries[index];
        _activePalette = palette;
        app.RequestedThemeVariant = definition.Id == "daxalgo-light"
            ? ThemeVariant.Light
            : ThemeVariant.Dark;
    }

    private static int FindPaletteIndex(IList<IResourceProvider> dictionaries)
    {
        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].TryGetResource(PaletteSentinel, ThemeVariant.Default, out _))
                return index;
        }

        return -1;
    }

    private static ThemeDefinition ResolveBuiltin(string id) =>
        Builtins.FirstOrDefault(theme =>
            string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Builtins[0];

    public Color? ReadColor(string key)
    {
        if (Application.Current?.TryFindResource(key, out var resource) != true)
            return null;

        return resource switch
        {
            SolidColorBrush brush => brush.Color,
            Color color => color,
            _ => null,
        };
    }

    public LinearGradientBrush? ReadGradient(string key)
    {
        return Application.Current?.TryFindResource(key, out var resource) == true
            ? resource as LinearGradientBrush
            : null;
    }

    public void SetColorOverride(string key, Color value)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.Resources[key] = app.TryFindResource(key, out var existing) && existing is Color
            ? value
            : new SolidColorBrush(value);
        _overrideKeys.Add(key);
    }

    public void SetGradientOverride(string key, IReadOnlyList<Color> stops)
    {
        var app = Application.Current;
        if (app is null || stops.Count == 0)
            return;

        var existing = ReadGradient(key);
        var brush = new LinearGradientBrush
        {
            StartPoint = existing?.StartPoint ?? RelativePoint.TopLeft,
            EndPoint = existing?.EndPoint ?? new RelativePoint(0, 1, RelativeUnit.Relative),
        };

        for (var index = 0; index < stops.Count; index++)
        {
            var offset = existing is not null && index < existing.GradientStops.Count
                ? existing.GradientStops[index].Offset
                : stops.Count <= 1 ? 0d : (double)index / (stops.Count - 1);
            brush.GradientStops.Add(new GradientStop(stops[index], offset));
        }

        app.Resources[key] = brush;
        _overrideKeys.Add(key);
    }

    private void ClearOverrides()
    {
        var app = Application.Current;
        if (app is not null)
        {
            foreach (var key in _overrideKeys)
                app.Resources.Remove(key);
        }

        _overrideKeys.Clear();
    }

    public IReadOnlyList<ThemeToken> EnumerateTokens()
    {
        var tokens = new List<ThemeToken>();
        if (_activePalette is null)
            return tokens;

        var keys = _activePalette.Keys.OfType<string>().ToList();
        var keySet = new HashSet<string>(keys, StringComparer.Ordinal);
        var linkedColorKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (_activePalette[key] is SolidColorBrush && LinkedColorKeyFor(key, keySet) is { } colorKey)
                linkedColorKeys.Add(colorKey);
        }

        foreach (var key in keys)
        {
            switch (_activePalette[key])
            {
                case SolidColorBrush brush:
                {
                    var colorKey = LinkedColorKeyFor(key, keySet);
                    tokens.Add(new ThemeToken(
                        Humanize(key),
                        GroupOf(key),
                        ThemeTokenKind.Solid,
                        key,
                        colorKey,
                        ReadColor(key) ?? brush.Color,
                        Array.Empty<Color>()));
                    break;
                }
                case Color color when !linkedColorKeys.Contains(key):
                    tokens.Add(new ThemeToken(
                        Humanize(key),
                        GroupOf(key),
                        ThemeTokenKind.Solid,
                        key,
                        null,
                        ReadColor(key) ?? color,
                        Array.Empty<Color>()));
                    break;
                case LinearGradientBrush gradient:
                {
                    var live = ReadGradient(key) ?? gradient;
                    tokens.Add(new ThemeToken(
                        Humanize(key),
                        GroupOf(key),
                        ThemeTokenKind.Gradient,
                        key,
                        null,
                        default,
                        live.GradientStops.Select(stop => stop.Color).ToList()));
                    break;
                }
            }
        }

        return tokens;
    }

    private static string? LinkedColorKeyFor(string brushKey, HashSet<string> keys)
    {
        var direct = brushKey + ".Color";
        if (keys.Contains(direct))
            return direct;

        if (brushKey.EndsWith(".Brush", StringComparison.Ordinal))
        {
            var stripped = brushKey[..^".Brush".Length] + ".Color";
            if (keys.Contains(stripped))
                return stripped;
        }

        return null;
    }

    private static string GroupOf(string key)
    {
        if (key.StartsWith("Ai.", StringComparison.Ordinal)
            || key.StartsWith("Gradient.Ai", StringComparison.Ordinal))
            return "AI (glass & gradients)";
        if (key.StartsWith("Gradient", StringComparison.Ordinal))
            return "Gradients";
        if (key.StartsWith("Background", StringComparison.Ordinal))
            return "Backgrounds";
        if (key.StartsWith("Border", StringComparison.Ordinal))
            return "Borders";
        if (key.StartsWith("Text", StringComparison.Ordinal))
            return "Text";
        if (key.StartsWith("Surface", StringComparison.Ordinal))
            return "Surfaces";
        if (key.StartsWith("Accent", StringComparison.Ordinal))
            return "Accent";
        if (key.StartsWith("Bullish", StringComparison.Ordinal)
            || key.StartsWith("Bearish", StringComparison.Ordinal)
            || key.StartsWith("Danger", StringComparison.Ordinal)
            || key.StartsWith("Warning", StringComparison.Ordinal)
            || key.StartsWith("Highlight", StringComparison.Ordinal))
            return "Semantic (P&L / status)";
        return "Other";
    }

    private static string Humanize(string key)
    {
        var trimmed = key;
        if (trimmed.EndsWith(".Brush", StringComparison.Ordinal))
            trimmed = trimmed[..^".Brush".Length];
        else if (trimmed.EndsWith(".Color", StringComparison.Ordinal))
            trimmed = trimmed[..^".Color".Length];
        return trimmed.Replace('.', ' ');
    }

    public ThemeDefinition RegisterCustomTheme(CustomThemeFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Normalize(file);

        var slug = Slug(file.Name);
        var id = "custom." + slug;
        try
        {
            Directory.CreateDirectory(ThemesDir);
            ExportThemeFile(file, Path.Combine(ThemesDir, slug + ".json"));
        }
        catch
        {
            // Persistence is best-effort; the theme remains available for this session.
        }

        _customs[id] = file;
        RebuildThemeList();
        ThemesChanged?.Invoke(this, EventArgs.Empty);
        return new ThemeDefinition(id, file.Name + " (custom)", ResolveBuiltin(file.BaseThemeId).PaletteUri);
    }

    public void ExportThemeFile(CustomThemeFile file, string path)
    {
        using var stream = File.Create(path);
        ExportThemeFile(file, stream);
    }

    public void ExportThemeFile(CustomThemeFile file, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        JsonSerializer.Serialize(destination, file, JsonOptions);
    }

    public CustomThemeFile ImportThemeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return ImportThemeFile(stream);
    }

    public CustomThemeFile ImportThemeFile(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var file = JsonSerializer.Deserialize<CustomThemeFile>(source)
            ?? throw new InvalidDataException("Theme file is empty or malformed.");
        Normalize(file);
        return file;
    }

    public bool TryGetCustomTheme(string id, out CustomThemeFile file) =>
        _customs.TryGetValue(id, out file!);

    private void LoadCustomThemes()
    {
        _customs.Clear();
        try
        {
            if (Directory.Exists(ThemesDir))
            {
                foreach (var path in Directory.EnumerateFiles(ThemesDir, "*.json"))
                {
                    try
                    {
                        var file = ImportThemeFile(path);
                        _customs["custom." + Path.GetFileNameWithoutExtension(path)] = file;
                    }
                    catch
                    {
                        // Skip one malformed theme without hiding the remaining installed themes.
                    }
                }
            }
        }
        catch
        {
            // An unreadable themes directory leaves the built-in themes available.
        }

        RebuildThemeList();
    }

    private void RebuildThemeList()
    {
        var themes = new List<ThemeDefinition>(Builtins);
        foreach (var (id, file) in _customs)
        {
            themes.Add(new ThemeDefinition(
                id,
                file.Name + " (custom)",
                ResolveBuiltin(file.BaseThemeId).PaletteUri));
        }

        _all = themes;
    }

    private static Color ParseColor(string hex) =>
        Color.TryParse(hex, out var color) ? color : Colors.Magenta;

    private static void Normalize(CustomThemeFile file)
    {
        file.Name = string.IsNullOrWhiteSpace(file.Name) ? "Custom" : file.Name.Trim();
        file.BaseThemeId = ResolveBuiltin(file.BaseThemeId).Id;
        file.Colors ??= new Dictionary<string, string>();
        file.Gradients ??= new Dictionary<string, List<string>>();
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrEmpty(slug) ? "theme" : slug;
    }

    private string LoadSavedId()
    {
        try
        {
            if (File.Exists(PrefFile))
            {
                var id = File.ReadAllText(PrefFile).Trim();
                if (_all.Any(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)))
                    return id;
            }
        }
        catch
        {
            // Fall back to the default palette.
        }

        return Builtins[0].Id;
    }

    private static void SaveId(string id)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(PrefFile, id);
        }
        catch
        {
            // Theme preference persistence is best-effort.
        }
    }
}
