# macOS index / Sdk

Generated from source fingerprint `b2d2bcde9e83`. macOS/Avalonia source only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/Sdk/DaxAlgo.Sdk/AuthoredPlugin.cs` | 164 | linux | DaxAlgo.Sdk | product | Y | The author wrote a complete hand-written window: metadata, a view-model, a view. |
| `src/linux/Sdk/DaxAlgo.Sdk/IPluginRegistrar.cs` | 33 | linux | DaxAlgo.Sdk | product | Y | The host service collection the plugin registers its strategy / view / |
| `src/linux/Sdk/DaxAlgo.Sdk/IStrategyEngineFactory.cs` | 26 | linux | DaxAlgo.Sdk | product | Y | Declarative tunables used by live editors, backtests, and optimizers. |
| `src/linux/Sdk/DaxAlgo.Sdk/IStrategyPlugin.cs` | 28 | linux | DaxAlgo.Sdk | product | Y | Human-readable plugin name (logging + the future marketplace UI). |
| `src/linux/Sdk/DaxAlgo.Sdk/SdkInfo.cs` | 18 | linux | DaxAlgo.Sdk | product | Y | Semantic version of this SDK build. Bump on any breaking change to |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/CanonicalJson.cs` | 59 | linux | DaxAlgo.Strategy.Bundle | product | Y | Minimal JSON string/number encoding with a frozen escape algorithm. It deliberately avoids |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/DaxStrategyBundle.cs` | 354 | linux | DaxAlgo.Strategy.Bundle | product | Y | Creates and verifies passive .daxstrategy archives without loading any payload assembly. |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleArchive.cs` | 403 | linux | DaxAlgo.Strategy.Bundle | product | Y | DSSE PAE domain-separates the payload type and both byte lengths from the |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleEnginePolicy.cs` | 256 | linux | DaxAlgo.Strategy.Bundle | product | Y | Validates the manifest-named factory from metadata without loading strategy code. |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleExternalAssemblyPolicy.cs` | 249 | linux | DaxAlgo.Strategy.Bundle | product | Y | Frozen v1 list of assemblies supplied by the .NET 9 Windows shared |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleLimitOptions.cs` | 60 | linux | DaxAlgo.Strategy.Bundle | product | Y |  |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleManifestCodec.cs` | 625 | linux | DaxAlgo.Strategy.Bundle | product | Y |  |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleModels.cs` | 230 | linux | DaxAlgo.Strategy.Bundle | product | Y | A repeatable source for one payload. The bundle packer owns and disposes |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePath.cs` | 93 | linux | DaxAlgo.Strategy.Bundle | product | Y |  |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundlePayloadPolicy.cs` | 485 | linux | DaxAlgo.Strategy.Bundle | product | Y | Validates bundle payload shape as metadata only. This is a format and |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleRuntimePolicy.cs` | 80 | linux | DaxAlgo.Strategy.Bundle | product | Y | The frozen v1 framework/host assembly allowlist used by graph and runtime resolution. |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleSemanticVersion.cs` | 85 | linux | DaxAlgo.Strategy.Bundle | product | Y |  |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleStore.cs` | 708 | linux | DaxAlgo.Strategy.Bundle | product | Y | Atomically makes one already-installed evidence selection active. |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleStoreJson.cs` | 263 | linux | DaxAlgo.Strategy.Bundle | product | Y |  |
| `src/linux/Sdk/DaxAlgo.Strategy.Bundle/StrategyBundleStoreModels.cs` | 105 | linux | DaxAlgo.Strategy.Bundle | product | Y | Controls whether an installed strategy may be unsigned. |
