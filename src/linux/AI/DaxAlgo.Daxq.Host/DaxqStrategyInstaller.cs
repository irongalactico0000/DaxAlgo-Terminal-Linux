using DaxAlgo.Daxq.Contracts;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Plugins;

namespace DaxAlgo.Daxq.Host;

/// <summary>Result of importing a protected strategy into the running Pro terminal.</summary>
public sealed record DaxqStrategyInstallResult(
    bool Success,
    bool Active,
    bool Persisted,
    string Message,
    string? InstalledPath = null);

/// <summary>
/// Verifies a selected DAXQ package, activates its ordinary backtest/catalog registrations, and
/// persists it in the loader's folder convention for the next startup.
/// </summary>
public sealed class DaxqStrategyInstaller(
    IProtectedStrategyEngine engine,
    IBacktestStrategyRegistry backtestRegistry,
    IStrategyFactory strategyCatalog,
    PluginHostContext plugins,
    ILogger<DaxqStrategyInstaller> logger)
{
    public DaxqStrategyInstallResult Install(string sourcePath)
    {
        string? stagingPath = null;
        string? backupPath = null;
        string? targetPath = null;
        var targetReplaced = false;
        var completed = false;
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return Failed("The protected strategy package was not found.");
            if (!string.Equals(Path.GetExtension(sourcePath), DaxqFormat.PackageExtension,
                    StringComparison.OrdinalIgnoreCase))
                return Failed("Pick a .daxq protected strategy package.");

            var source = Path.GetFullPath(sourcePath);
            Directory.CreateDirectory(plugins.PluginsRoot);
            stagingPath = Path.Combine(
                plugins.PluginsRoot, $".daxq-{Guid.NewGuid():N}.installing.daxq");
            File.Copy(source, stagingPath, overwrite: false);

            // Everything below is derived from this frozen copy. A source file swapped while the
            // dialog is open can no longer change the bytes that are verified and activated.
            var verified = RequireRegistrations(engine.LoadStrategies(stagingPath));
            var folderName = RequireSinglePackageIdentity(verified);
            var sourceHash = PluginIntegrity.Sha256(stagingPath);
            if (PluginRevocationList.Load(plugins.PluginsRoot)
                .IsRevoked(sourceHash, folderName, out var revokedReason))
            {
                return Failed($"Protected strategy install rejected: {revokedReason}.");
            }

            var targetDirectory = Path.Combine(plugins.PluginsRoot, folderName);
            targetPath = Path.Combine(targetDirectory, folderName + DaxqFormat.PackageExtension);
            var existingPin = PluginTrustedHashes.Load(plugins.PluginsRoot)
                .VerifyArtifact(folderName, targetPath, out var pinDetail);
            if (existingPin == PluginPinResult.Tampered ||
                (existingPin == PluginPinResult.Match &&
                 !string.Equals(sourceHash, PluginIntegrity.Sha256(targetPath),
                     StringComparison.OrdinalIgnoreCase)))
            {
                return Failed(
                    $"Protected strategy install rejected: {pinDetail ?? "it would replace a build-pinned artifact"}.");
            }

            Directory.CreateDirectory(targetDirectory);
            if (File.Exists(targetPath))
            {
                backupPath = Path.Combine(
                    targetDirectory, $".{folderName}.{Guid.NewGuid():N}.backup");
                File.Move(targetPath, backupPath, overwrite: false);
            }
            File.Move(stagingPath, targetPath, overwrite: false);
            stagingPath = null;
            targetReplaced = true;

            if (!string.Equals(sourceHash, PluginIntegrity.Sha256(targetPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed DAXQ bytes changed during import.");

            // Reload from the stable installed path so fault attribution uses the exact state key.
            var registrations = RequireRegistrations(engine.LoadStrategies(targetPath));
            if (!string.Equals(
                    RequireSinglePackageIdentity(registrations), folderName,
                    StringComparison.Ordinal))
                throw new InvalidDataException("The installed DAXQ identity changed during import.");
            if (PluginRevocationList.Load(plugins.PluginsRoot)
                .IsRevoked(sourceHash, folderName, out revokedReason))
                throw new InvalidDataException($"The installed DAXQ package is revoked: {revokedReason}.");

            if (plugins.State?.IsDisabled(folderName) == true)
            {
                UpdateState(folderName, targetPath);
                completed = true;
                return new DaxqStrategyInstallResult(
                    true, false, true,
                    $"'{registrations[0].Strategy.DisplayName}' is installed but remains disabled.",
                    targetPath);
            }

            foreach (var registration in registrations)
            {
                backtestRegistry.Register(registration.BacktestStrategy);
                strategyCatalog.Register(registration.Strategy, registration.StrategyFactory);
            }

            UpdateState(folderName, targetPath);

            plugins.AddRuntimeInstalled(new LoadedPlugin(
                registrations[0].Strategy.DisplayName,
                $"DAXQ VM ABI {DaxqFormat.VmAbiVersion}",
                targetPath,
                StrategyImplementationTypes: registrations
                    .Select(r => r.Strategy.GetType().FullName ?? r.Strategy.GetType().Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));

            var displayName = registrations[0].Strategy.DisplayName;
            completed = true;
            logger.LogInformation(
                "Installed protected strategy {StrategyId}: active={Active} persisted={Persisted}",
                folderName, true, true);
            return new DaxqStrategyInstallResult(
                true, true, true,
                $"'{displayName}' is in the Strategies catalog and backtester. " +
                "It will load through the protected path after restart.",
                targetPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Protected strategy installation failed for {Path}", sourcePath);
            return Failed($"Protected strategy install failed: {ex.Message}");
        }
        finally
        {
            TryDelete(stagingPath);
            if (!completed && targetReplaced && targetPath is not null)
                TryDelete(targetPath);
            if (backupPath is not null && File.Exists(backupPath))
            {
                if (completed)
                    TryDelete(backupPath);
                else if (targetPath is not null)
                {
                    try { File.Move(backupPath, targetPath, overwrite: false); }
                    catch (Exception ex) { logger.LogError(ex, "Could not restore the previous DAXQ package"); }
                }
            }
        }
    }

    private void UpdateState(string folderName, string installedPath)
    {
        if (plugins.State is null) return;
        try
        {
            plugins.State.ClearQuarantine(folderName);
            plugins.State.ClearPendingUninstall(folderName);
            plugins.State.SetInstalledHash(folderName, PluginIntegrity.Sha256(installedPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update lifecycle state for DAXQ strategy {StrategyId}", folderName);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch { /* best-effort cleanup; the verified target/backup remains explicit */ }
    }

    private static IReadOnlyList<ProtectedStrategyRegistration> RequireRegistrations(
        IReadOnlyList<ProtectedStrategyRegistration>? registrations) =>
        registrations is { Count: > 0 }
            ? registrations
            : throw new InvalidDataException("The protected strategy engine returned no strategies.");

    private static string RequireSinglePackageIdentity(
        IReadOnlyList<ProtectedStrategyRegistration> registrations)
    {
        var ids = registrations.Select(r => r.Strategy.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0]) ||
            ids[0].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ids[0] is "." or "..")
        {
            throw new InvalidDataException("A DAXQ v1 package must expose one filesystem-safe strategy id.");
        }
        return ids[0];
    }

    private static DaxqStrategyInstallResult Failed(string message) =>
        new(false, false, false, message);
}
