# macOS terminal migration
Status: implementation complete; real-macOS release validation pending

## Goal
Create an independent DaxAlgo Terminal under `C:\DaxAlgo\DaxAlgo-Terminal-Mac` with the current Windows Professional non-strategy feature set and visual language. Reusable implementation is physically copied and compiled here; no source or project reference may point back to the Windows checkout. Concrete strategy implementations are excluded and remain external plug-ins.

## Plan
1. Complete - inventory the extracted Avalonia baseline and current Pro/shared non-strategy closure.
2. Complete - establish the independent macOS solution and remove concrete strategy composition.
3. Complete - copy reusable projects and implement the shell, design system, runtime services, configuration, workers, brokers, security, and packaging paths.
4. Complete on the Windows validation host - run managed/native tests, both macOS RID builds and publishes, containment scans, and context synchronization.

## Blast radius
- Product changes are confined to this repository.
- `D:\Github\DaxAlgo-Terminal-Pro` and its `public/` submodule were read-only source inputs; their only intentional migration change is the companion task record.
- No commit, push, issue, PR, release, or other external action was performed.

## Findings and decisions
- The private Avalonia repository at commit `826857992d4d9fac0adb95e546d0b37613155ea7` was a useful baseline but materially predated the Windows product.
- `TradingTerminal.Mac.slnx` is now the canonical 55-project graph. The obsolete incomplete Linux solution was removed; inherited `src/linux`, `tests/linux`, and context paths remain directory names only.
- Production composition contains no concrete strategy project or built-in strategy implementation. Strategy SDK, authoring, catalog, trust, consent, quarantine, protected-runtime, and plug-in host contracts remain because they are terminal features.
- The full non-strategy shell is represented in Avalonia: login/account gates, catalog and presentation editing, charts/tools, recording, settings, Telegram archive, support, activity log, theme studio, Vibe Quant, coordinator/codegen, strategy composer, and backtest analysis/studio.
- Current portable Core, MarketData, Infrastructure, AI/coordinator, DAXQ, Footprint Transformer, SDK, bundle, worker, CLI, chart, tool, Settings, and UI sources compile locally in this tree.
- The backtest CLI and Studio discover only installed/authored plug-in strategies. Worker execution is staged into builds and publishes; macOS reports GPU requests as a transparent CPU fallback because the removed GPU bridge was strategy-specific.
- Secrets use native Security.framework Keychain calls on macOS without putting secret values in process arguments. Crash recovery, plug-in fault attribution/quarantine, and `--smoke-strategies` diagnostics are wired into the app.
- QuestDB auto-start is macOS Docker-managed with loopback-only ports, a pinned image, named volume, bounded startup, and safe external mode. NinjaTrader remains Windows-gated; IB uses the official TWS C# API and is a required release packaging dependency by default.
- DAXQ includes a copied native VM with CommonCrypto/Security.framework verification, hardened code-signature checks, arm64/x64 build support, and private release pin/sign staging. It fails closed when private production inputs are unavailable.
- The optional trained Footprint Transformer ONNX/metadata artifacts are absent from both the current Windows source and this tree; the provider therefore reports unavailable until private trained artifacts are supplied.

## Diff summary
- Solution/build/context: `TradingTerminal.Mac.slnx`, `Directory.Build.props`, `.claude/context/**`, `AGENTS.md`.
- Product/runtime: `src/linux/**`, including copied non-strategy closures and macOS-specific shell/platform adapters.
- Validation: `tests/linux/**`, including headless, DAXQ, worker, plug-in, entitlement, theme, packaging, and containment coverage.
- Native/release: `tools/daxq-vm/**`, `tools/macos/**`, app entitlements/plist/configuration, and worker staging targets.
- Delivery docs: `README.md`, `docs/strategy-bundles.md`, and this record.
- Removed concrete strategy projects/sources, strategy-specific native/CUDA implementations, and stale WPF-only shell duplicates from the deliverable graph.

## Verification
- Baseline before migration: solution build passed with 0 errors/48 warnings; 481/481 headless tests passed.
- Final managed headless suite: PASS, 599/599.
- DAXQ managed suites: PASS, Contracts 13/13, VM 41/41, Compiler 26/26, Host 23 passed with one Apple-team test skipped on Windows.
- The DAXQ allocation-path test hit its execution timeout once under the first cold-suite run, then passed in isolation and again in the complete Host suite; no source change was needed. Treat recurrence on an otherwise idle Mac runner as a timing-regression signal.
- Avalonia/theme/package invariant suite: PASS, 4/4.
- Final managed total executed: 706 passed, 1 macOS-only skipped, 0 failed.
- `dotnet build TradingTerminal.Mac.slnx -c Release`: PASS across the complete graph with 0 warnings/0 errors.
- App RID builds: PASS for `osx-arm64` and `osx-x64`, each with 0 warnings/0 errors.
- Self-contained publishes: PASS for both RIDs. Both apphosts have 64-bit Mach-O magic and the expected arm64/x86_64 CPU type; base configuration, official `CSharpAPI.dll`, and the staged backtest worker are present. `appsettings.local.json` and an unsigned/unpinned DAXQ dylib are absent as intended.
- Native DAXQ portability build on Windows: PASS; CMake build succeeded and CTest passed 1/1. Apple Security/CommonCrypto branches remain real-Mac-only.
- Shell syntax: PASS for `tools/macos/package.sh` and `tools/daxq-vm/build-macos.sh` under Git Bash.
- Containment audit: PASS. 137 XML and 20 JSON files parsed; all 176 project references remain inside this repository; no product source/project reference points to the Windows checkout.
- Strategy boundary audit: PASS. The Mac product graph has no concrete strategy projects/types and generic native/backtest catalogs are empty.
- Context was regenerated from `TradingTerminal.Mac.slnx`: 55 projects, 1,034 files, 143,423 LOC. Structural and independent byte-for-byte generator checks passed after the last source edit.

## Risks / deferred release evidence
- Run the arm64 and x64 app bundles on real supported Macs; verify Developer ID signing, notarization, stapling, Gatekeeper, launch, quit, crash recovery, and visual parity against Windows.
- Exercise Keychain create/read/update/delete, OAuth browser callbacks, Telegram 2FA/offload, plug-in install/consent/quarantine/recovery, CLI terminal launch, and `--smoke-strategies` with signed release plug-ins.
- Exercise official IB/TWS and other supported broker sessions, Docker Desktop/QuestDB lifecycle, worker execution, and sidecar resolution on macOS.
- Build/test/sign/load the DAXQ dylib on both Apple architectures with real license pins and verify protected package execution. The Windows host cannot execute Apple Security.framework branches.
- Supply and validate the private trained Footprint Transformer artifacts if model inference is part of a release.
- Replace the destination clone's inherited Linux-template Git remote with the intended private macOS repository before any push; no remote mutation was authorized here.
