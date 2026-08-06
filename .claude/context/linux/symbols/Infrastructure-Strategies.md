# TradingTerminal.Infrastructure / Strategies — public API surface (macOS/Avalonia)

Generated from source fingerprint `3b8482429c18`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/AuthoredStrategyInstaller.cs
```cs
   21: public sealed record AuthoredStrategyInstall(
   45: public sealed class AuthoredStrategyInstaller(
   53: public AuthoredStrategyInstall Install(StrategyScript script, StrategyCompileResult compiled)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/RoslynStrategyCompiler.cs
```cs
   27: public sealed class RoslynStrategyCompiler : IStrategyCompiler
   59: public StrategyCompileResult Compile(StrategyScript script)
  156: public sealed class DaxAlgoAuthoredPlugin : DaxAlgo.Sdk.IStrategyPlugin
  158: public string Name => {{Literal(script.DisplayName)}};
  159: public string TargetSdkVersion => {{Literal(SdkInfo.Version)}};
  161: public void Register(DaxAlgo.Sdk.IPluginRegistrar registrar) =>
```
