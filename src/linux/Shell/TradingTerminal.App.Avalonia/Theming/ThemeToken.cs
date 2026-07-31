using Avalonia.Media;

namespace TradingTerminal.App.Avalonia.Theming;

/// <summary>Whether a palette token is a flat colour or a multi-stop gradient.</summary>
public enum ThemeTokenKind
{
    Solid,
    Gradient,
}

/// <summary>One editable palette entry surfaced by Theme Studio.</summary>
public sealed record ThemeToken(
    string DisplayName,
    string Group,
    ThemeTokenKind Kind,
    string PrimaryKey,
    string? LinkedColorKey,
    Color SolidValue,
    IReadOnlyList<Color> GradientStops);

/// <summary>
/// Shareable on-disk representation of a custom theme. Colours use #AARRGGBB and every editable
/// token is captured, so a file is independent of the edits that produced it.
/// </summary>
public sealed class CustomThemeFile
{
    public string Name { get; set; } = "Custom";

    public string BaseThemeId { get; set; } = "daxalgo-dark";

    public Dictionary<string, string> Colors { get; set; } = new();

    public Dictionary<string, List<string>> Gradients { get; set; } = new();
}
