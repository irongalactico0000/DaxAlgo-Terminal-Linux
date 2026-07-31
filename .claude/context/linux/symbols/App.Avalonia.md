# TradingTerminal.App.Avalonia — public API surface (macOS/Avalonia)

Generated from source fingerprint `b2d2bcde9e83`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Shell/TradingTerminal.App.Avalonia/App.axaml.cs
```cs
   23: public partial class App : Application
   29: public IServiceProvider? Services { get; private set; }
   31: public override void Initialize() => AvaloniaXamlLoader.Load(this);
   33: public override async void OnFrameworkInitializationCompleted()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Archive/AvaloniaTelegramAuthPrompt.cs
```cs
   13: public sealed class AvaloniaTelegramAuthPrompt : ITelegramAuthPrompt
   15: public Task<string?> PromptAsync(string key, CancellationToken ct)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Archive/TelegramArchiveLogin.cs
```cs
   14: public sealed class TelegramArchiveLogin : ITelegramArchiveLogin
   21: public TelegramArchiveLogin(
   33: public bool IsConnected => _transport.IsReady;
   35: public TelegramArchiveCredentials Load()
   41: public async Task<TelegramArchiveLoginResult> ConnectAsync(
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Archive/TelegramArchiveOptionsPostConfigure.cs
```cs
   12: public void PostConfigure(string? name, TelegramArchiveOptions options)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Archive/TelegramPromptDialog.axaml.cs
```cs
    6: public partial class TelegramPromptDialog : Window
    8: public TelegramPromptDialog()
   13: public TelegramPromptDialog(string headerText, string helpText, bool isSecret)
   38: public TelegramPromptDialogContext(string header, string help)
   44: public string HeaderText { get; }
   45: public string HelpText { get; }
   46: public string InputValue { get; set; } = string.Empty;
```

## src/linux/Shell/TradingTerminal.App.Avalonia/AvaloniaUiDispatcher.cs
```cs
   14: public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();
   16: public void Post(Action action) => Dispatcher.UIThread.Post(action);
   18: public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Charts/LineChartControl.cs
```cs
   12: public sealed class LineChartControl : Control
   21: public static readonly DirectProperty<LineChartControl, IReadOnlyList<double>> SeriesProperty =
   26: public IReadOnlyList<double> Series
   32: public static readonly DirectProperty<LineChartControl, IReadOnlyList<double>> Series2Property =
   38: public IReadOnlyList<double> Series2
   44: public override void Render(DrawingContext ctx)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Composition/ObservableCollectionLogSink.cs
```cs
   12: public ObservableCollectionLogSink(InMemoryLogSink activityLog) =>
   15: public void Emit(LogEvent logEvent) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Composition/ServiceConfiguration.cs
```cs
   59: public static class ServiceConfiguration
   61: public static IHost BuildHost(IPluginConsentPrompt? pluginConsentPrompt = null)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Diagnostics/CrashGuard.cs
```cs
   19: public static string ReportDirectory { get; } = Path.Combine(
   24: public static void Install(string appName, Action<string, string, string>? log = null)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Diagnostics/PluginFaultWatchdog.cs
```cs
   14: public static IDisposable Attach(
   99: public void Dispose() => Interlocked.Exchange(ref _detach, null)?.Invoke();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Diagnostics/StrategyWindowSmoke.cs
```cs
   12: public static async Task<int> RunAsync(
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchViewModel.cs
```cs
   12: public sealed partial class ArimaGarchViewModel : ObservableObject
   17: public ArimaGarchViewModel()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/ArimaGarchWindow.axaml.cs
```cs
    5: public partial class ArimaGarchWindow : Window
    7: public ArimaGarchWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanViewModel.cs
```cs
   13: public sealed partial class KalmanViewModel : ObservableObject
   17: public KalmanViewModel()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/KalmanWindow.axaml.cs
```cs
    5: public partial class KalmanWindow : Window
    7: public KalmanWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityViewModel.cs
```cs
   13: public sealed partial class StationarityViewModel : ObservableObject
   17: public StationarityViewModel()
   29: public SeriesTransform[] Transforms { get; }
```

## src/linux/Shell/TradingTerminal.App.Avalonia/MachineLearning/StationarityWindow.axaml.cs
```cs
    5: public partial class StationarityWindow : Window
    7: public StationarityWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Plugins/PluginConsentDialog.axaml.cs
```cs
   15: public partial class PluginConsentDialog : Window
   17: public PluginConsentDialog()
   41: public string Headline { get; private set; }
   42: public string PublisherText { get; private set; }
   43: public string PathText { get; private set; }
   44: public string HashText { get; private set; }
   45: public IReadOnlyList<string> Capabilities { get; private set; }
   47: public static Task<bool> AskAsync(PluginConsentRequest request, Window owner)
   83: public sealed class PluginConsentPrompt : IPluginConsentPrompt
   87: public PluginConsentPrompt(TimeSpan? timeout = null)
   92: public bool RequestConsent(PluginConsentRequest request)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Plugins/PluginManagerView.axaml.cs
```cs
    5: public partial class PluginManagerView : UserControl
    7: public PluginManagerView()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Plugins/PluginManagerViewModel.cs
```cs
   21: public sealed record PluginRow(
   45: public sealed partial class PluginManagerViewModel : ViewModelBase
   54: public PluginManagerViewModel(
   89: public string PluginsRoot { get; }
   90: public string TrustPolicySummary { get; }
   91: public ObservableCollection<PluginRow> Rows { get; } = new();
  102: public ObservableCollection<PluginCatalogItem> CatalogItems { get; } = new();
  105: public bool FeedConfigured => _feed.IsConfigured;
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Program.cs
```cs
    9: public static void Main(string[] args) =>
   12: public static AppBuilder BuildAvaloniaApp() =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/AiProvidersSettingsWindow.axaml.cs
```cs
    5: public partial class AiProvidersSettingsWindow : Window
    7: public AiProvidersSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveActivityWindow.axaml.cs
```cs
    5: public partial class ArchiveActivityWindow : Window
    7: public ArchiveActivityWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ArchiveSettingsWindow.axaml.cs
```cs
    5: public partial class ArchiveSettingsWindow : Window
    7: public ArchiveSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/NotificationsSettingsWindow.axaml.cs
```cs
    5: public partial class NotificationsSettingsWindow : Window
    7: public NotificationsSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/ResearchSettingsWindow.axaml.cs
```cs
    5: public partial class ResearchSettingsWindow : Window
    7: public ResearchSettingsWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/StrategyAuthoringWindow.axaml.cs
```cs
   13: public partial class StrategyAuthoringWindow : Window
   17: public StrategyAuthoringWindow()
   24: public bool ShowSimulatedDataBanner
   94: public sealed class ParameterKindMatchConverter : IValueConverter
   96: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
  105: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  110: public sealed class PositiveCountConverter : IValueConverter
  112: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  115: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Settings/SupportWindow.axaml.cs
```cs
    6: public partial class SupportWindow : Window
    8: public SupportWindow()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/BrokerApiChipViewModel.cs
```cs
   12: public sealed partial class BrokerApiChipViewModel : ViewModelBase
   14: public BrokerApiChipViewModel(BrokerKind broker)
   20: public BrokerKind Broker { get; }
   22: public string Label { get; }
   28: public int AvailableCallsPerMinute => SoftLimitPerMinute > 0
   32: public BrokerApiChipStatus Status => SoftLimitPerMinute <= 0
   41: public string UsageDisplay => SoftLimitPerMinute > 0
   45: public string TooltipText => SoftLimitPerMinute > 0
   78: public enum BrokerApiChipStatus
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/BrokerApiMeterViewModel.cs
```cs
   15: public sealed partial class BrokerApiMeterViewModel : ViewModelBase, IDisposable
   21: public BrokerApiMeterViewModel(IBrokerApiMeter meter)
   31: public ObservableCollection<BrokerApiChipViewModel> Chips { get; }
   71: public void Dispose() => _timer.Stop();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindow.axaml.cs
```cs
   12: public partial class MainWindow : Window
   16: public MainWindow()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/MainWindowViewModel.cs
```cs
   29: public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
   38: public MainWindowViewModel(
   83: public MainWindowViewModel()
  101: public ObservableCollection<ITradingStrategy> Strategies { get; }
  103: public ObservableCollection<StrategyCatalogItemViewModel> CatalogItems { get; }
  107: public ITradingStrategy? SelectedStrategy => SelectedCatalogItem?.Strategy;
  108: public bool HasNoStrategies => CatalogItems.Count == 0;
  109: public bool HasStrategies => CatalogItems.Count > 0;
  111: public IReadOnlyList<CliLaunchChoice> CliLaunchChoices { get; }
  113: public void LaunchCli(CliLaunchChoice? choice)
  132: public string SelectedDetails => SelectedStrategy?.Description ?? "Select a strategy to see its description.";
  166: public BrokerApiMeterViewModel? ApiMeter { get; }
  167: public TickRecordingService? Recorder { get; }
  169: public int PluginProblemCount { get; }
  170: public bool HasPluginProblems => PluginProblemCount > 0;
  174: public InMemoryLogSink ActivityLog { get; }
  177: public ObservableCollection<LogEntry> VisibleLog { get; }
  186: public void BeginBusy(string title, string message)
  193: public void EndBusy() => IsBusy = false;
  237: public bool IsDisconnected => ConnectionState is not ConnectionState.Connected;
  238: public bool HasFeedDrops => FeedDropCount > 0;
  239: public string DisconnectBannerText => "Disconnected — connect a broker to resume";
  240: public int ConnectedBrokerCount => _brokerSelector?.Connected.Count ?? 0;
  241: public bool IsAuthenticated => _session?.IsAuthenticated == true;
  242: public string SessionUserDisplay => !IsAuthenticated
  249: public string RuntimeInfo =>
  289: public async Task ReconnectAllAsync()
  316: public void Dispose()
  328: public sealed class CliLaunchChoice(AgentCliAdapter adapter, bool isAvailable)
  330: public AgentCliAdapter Adapter { get; } = adapter;
  331: public bool IsAvailable { get; } = isAvailable;
  332: public string DisplayName => Adapter.DisplayName;
  333: public string MenuHeader => IsAvailable ? Adapter.DisplayName : $"{Adapter.DisplayName} - not installed";
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/ShellConverters.cs
```cs
   13: public static readonly IBrush Bullish = Brush("#00C853");
   14: public static readonly IBrush Warning = Brush("#FFD600");
   15: public static readonly IBrush Danger = Brush("#FF1744");
   16: public static readonly IBrush Muted = Brush("#8A8A8A");
   17: public static readonly IBrush Accent = Brush("#FF8C00");
   18: public static readonly IBrush BullishSoft = Brush("#2600C853");
   19: public static readonly IBrush WarningSoft = Brush("#26FFD600");
   20: public static readonly IBrush BearishSoft = Brush("#26FF1744");
   21: public static readonly IBrush Neutral = Brush("#1AFFFFFF");
   22: public static readonly IBrush NeutralStrong = Brush("#22FFFFFF");
   23: public static readonly IBrush BorderStrong = Brush("#3A3A3A");
   25: public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
   29: public sealed class ConnectionStateToBrushConverter : IValueConverter
   31: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   41: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   46: public sealed class ApiStatusToBrushConverter : IValueConverter
   48: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   59: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   65: public sealed class SessionFlagToBrushConverter : IValueConverter
   67: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   78: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   84: public sealed class BoolToTextConverter : IValueConverter
   86: public string TrueText { get; set; } = string.Empty;
   87: public string FalseText { get; set; } = string.Empty;
   89: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   92: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   98: public sealed class LogLevelToBrushConverter : IValueConverter
  100: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
  113: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Shell/StrategyPillConverters.cs
```cs
   13: public sealed record StrategyPill(string Text, IBrush Background, IBrush Foreground);
   20: public sealed class StrategyDataRequirementConverter : IValueConverter
   32: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   51: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   62: public sealed class StrategyClassificationConverter : IValueConverter
   74: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   98: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  135: public sealed class StringNotEmptyConverter : IValueConverter
  137: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  140: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Strategies/StrategyImageTile.axaml.cs
```cs
   13: public partial class StrategyImageTile : UserControl
   15: public static readonly StyledProperty<string?> ImagePathProperty =
   21: public StrategyImageTile()
   29: public string? ImagePath
   35: protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
   41: protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
   48: protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Strategies/StrategyPresentationEditorViewModel.cs
```cs
   11: public sealed partial class StrategyPresentationEditorViewModel : ViewModelBase
   15: public StrategyPresentationEditorViewModel(StrategyCatalogItemViewModel item)
   30: public string StrategyId => _item.Id;
   31: public string DefaultName { get; }
   32: public string DefaultDescription { get; }
   33: public string DefaultLinkUrl { get; }
   42: public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);
   68: public StrategyPresentation Build()
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Strategies/StrategyPresentationEditorWindow.axaml.cs
```cs
    6: public partial class StrategyPresentationEditorWindow : Window
    8: public StrategyPresentationEditorWindow() => InitializeComponent();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Support/ISupportPrompt.cs
```cs
    9: public interface ISupportPrompt
   15:     void MaybeShowOnLaunch(Window owner);
   18:     void Show(Window owner);
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Support/SupportPrompt.cs
```cs
   29: public SupportPrompt(IServiceProvider services, ILogger<SupportPrompt> logger)
   35: public void MaybeShowOnLaunch(Window owner)
   77: public void Show(Window owner)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Theming/AvaloniaThemeFilePicker.cs
```cs
    7: public sealed class ThemeFileHandle : IAsyncDisposable
    9: public ThemeFileHandle(string displayName, Stream stream)
   15: public string DisplayName { get; }
   17: public Stream Stream { get; }
   19: public ValueTask DisposeAsync() => Stream.DisposeAsync();
   23: public interface IThemeFilePicker
   25:     Task<ThemeFileHandle?> OpenThemeAsync();
   27:     Task<ThemeFileHandle?> SaveThemeAsync(string suggestedFileName);
   34: public sealed class AvaloniaThemeFilePicker : IThemeFilePicker
   45: public AvaloniaThemeFilePicker(Func<TopLevel?> topLevel)
   50: public async Task<ThemeFileHandle?> OpenThemeAsync()
   69: public async Task<ThemeFileHandle?> SaveThemeAsync(string suggestedFileName)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Theming/ThemeManager.cs
```cs
   11: public sealed record ThemeDefinition(string Id, string Name, string PaletteUri);
   18: public interface IThemeManager
   20:     IReadOnlyList<ThemeDefinition> Themes { get; }
   22:     string CurrentThemeId { get; }
   24:     string CurrentBaseThemeId { get; }
   26:     event EventHandler? ThemesChanged;
   28:     void Apply(string themeId);
   30:     void ApplySaved();
   32:     IReadOnlyList<ThemeToken> EnumerateTokens();
   34:     Color? ReadColor(string key);
   36:     LinearGradientBrush? ReadGradient(string key);
   38:     void SetColorOverride(string key, Color value);
   40:     void SetGradientOverride(string key, IReadOnlyList<Color> stops);
   42:     ThemeDefinition RegisterCustomTheme(CustomThemeFile file);
   44:     void ExportThemeFile(CustomThemeFile file, string path);
   46:     void ExportThemeFile(CustomThemeFile file, Stream destination);
   48:     CustomThemeFile ImportThemeFile(string path);
   50:     CustomThemeFile ImportThemeFile(Stream source);
   52:     bool TryGetCustomTheme(string id, out CustomThemeFile file);
   56: public sealed class ThemeManager : IThemeManager
   81: public event EventHandler? ThemesChanged;
   83: public IReadOnlyList<ThemeDefinition> Themes => _all;
   85: public string CurrentThemeId { get; private set; } = Builtins[0].Id;
   87: public string CurrentBaseThemeId =>
   92: public void ApplySaved()
   98: public void Apply(string themeId)
  169: public Color? ReadColor(string key)
  182: public LinearGradientBrush? ReadGradient(string key)
  189: public void SetColorOverride(string key, Color value)
  201: public void SetGradientOverride(string key, IReadOnlyList<Color> stops)
  238: public IReadOnlyList<ThemeToken> EnumerateTokens()
  352: public ThemeDefinition RegisterCustomTheme(CustomThemeFile file)
  375: public void ExportThemeFile(CustomThemeFile file, string path)
  381: public void ExportThemeFile(CustomThemeFile file, Stream destination)
  388: public CustomThemeFile ImportThemeFile(string path)
  394: public CustomThemeFile ImportThemeFile(Stream source)
  403: public bool TryGetCustomTheme(string id, out CustomThemeFile file) =>
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Theming/ThemeStudioView.axaml.cs
```cs
    5: public partial class ThemeStudioView : UserControl
    7: public ThemeStudioView()
   12: public ThemeStudioView(IThemeManager manager)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Theming/ThemeStudioViewModel.cs
```cs
   12: public sealed partial class ThemeStudioViewModel : ViewModelBase
   31: public ThemeStudioViewModel(IThemeManager manager, IThemeFilePicker filePicker)
   46: public ObservableCollection<ThemeDefinition> BaseThemes { get; }
   48: public ObservableCollection<ThemeTokenGroupViewModel> Groups { get; }
  218: public sealed class ThemeTokenGroupViewModel
  220: public ThemeTokenGroupViewModel(string name)
  225: public string Name { get; }
  227: public ObservableCollection<ThemeTokenViewModel> Tokens { get; } = new();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Theming/ThemeToken.cs
```cs
    6: public enum ThemeTokenKind
   13: public sealed record ThemeToken(
   26: public sealed class CustomThemeFile
   28: public string Name { get; set; } = "Custom";
   30: public string BaseThemeId { get; set; } = "daxalgo-dark";
   32: public Dictionary<string, string> Colors { get; set; } = new();
   34: public Dictionary<string, List<string>> Gradients { get; set; } = new();
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Theming/ThemeTokenViewModel.cs
```cs
   12: public sealed class ThemeTokenViewModel : ObservableObject
   18: public ThemeTokenViewModel(IThemeManager manager, ThemeToken token)
   39: public string DisplayName { get; }
   41: public string PrimaryKey { get; }
   43: public string? LinkedColorKey { get; }
   45: public bool IsGradient { get; }
   47: public bool IsSolid => !IsGradient;
   49: public ObservableCollection<GradientStopViewModel> Stops { get; }
   51: public Color Color => _color;
   53: public IBrush Swatch => new SolidColorBrush(_color);
   55: public string Hex
   66: public byte A
   72: public byte R
   78: public byte G
   84: public byte B
   90: public IBrush GradientPreview
  145: public sealed class GradientStopViewModel : ObservableObject
  151: public GradientStopViewModel(int index, Color color, Action onChanged)
  158: public int Index { get; }
  160: public string Label => $"Stop {Index + 1}";
  162: public Color Color => _color;
  164: public IBrush Swatch => new SolidColorBrush(_color);
  166: public string Hex
  177: public byte A
  183: public byte R
  189: public byte G
  195: public byte B
  228: public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
  230: public static bool TryParse(string? value, out Color color)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationHeatmapControl.cs
```cs
   13: public sealed class CorrelationHeatmapControl : Control
   27: public static readonly DirectProperty<CorrelationHeatmapControl, CorrelationMatrix?> MatrixProperty =
   32: public CorrelationMatrix? Matrix
   38: protected override Size MeasureOverride(Size a)
   44: public override void Render(DrawingContext ctx)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationViewModel.cs
```cs
   13: public sealed partial class CorrelationViewModel : ObservableObject
   17: public CorrelationViewModel() => Run();
   51: public static double NextGaussian(this Random r)
```

## src/linux/Shell/TradingTerminal.App.Avalonia/Tools/CorrelationWindow.axaml.cs
```cs
    5: public partial class CorrelationWindow : Window
    7: public CorrelationWindow() => InitializeComponent();
```
