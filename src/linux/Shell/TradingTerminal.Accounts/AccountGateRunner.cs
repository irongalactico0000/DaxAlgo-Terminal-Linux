using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

/// <summary>Creates the macOS account gate while retaining the Windows gate's service policy.</summary>
public static class AccountGateRunner
{
    private static int _forceFreshAuthentication;

    public static AccountGateWindow CreateWindow(AppEdition requiredEdition) =>
        CreateWindow(requiredEdition, GetEnvironmentName(), null, null, null, null);

    public static AccountGateWindow CreateWindow(
        AppEdition requiredEdition,
        GoogleAuthOptions googleAuthOptions)
    {
        ArgumentNullException.ThrowIfNull(googleAuthOptions);
        return CreateWindow(
            requiredEdition,
            GetEnvironmentName(),
            null,
            null,
            null,
            googleAuthOptions);
    }

    /// <summary>
    /// Clears the Keychain-protected local account session and forces the next gate invocation to
    /// authenticate interactively even if the session file cannot be removed.
    /// </summary>
    public static bool ClearStoredAccount()
    {
        Volatile.Write(ref _forceFreshAuthentication, 1);
        return DevelopmentAccountSessionStore.CreateDefault().Clear();
    }

    public static AccountGateWindow CreateWindow(
        AppEdition requiredEdition,
        string environmentName,
        IAccountAuthenticationService? authentication,
        IEntitlementService? entitlements,
        IAccountGateDiagnostics? diagnostics = null,
        GoogleAuthOptions? googleAuthOptions = null)
    {
        var edition = AccountGateEditionProfile.For(requiredEdition);
        var forceFreshAuthentication =
            Interlocked.Exchange(ref _forceFreshAuthentication, 0) != 0;
        var services = AccountGateServiceFactory.Create(
            environmentName,
            authentication,
            entitlements,
            googleAuthOptions: googleAuthOptions,
            forceFreshAuthentication: forceFreshAuthentication);
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            requiredEdition,
            TimeProvider.System,
            diagnostics);
        var viewModel = new AccountGateViewModel(
            coordinator,
            edition,
            services.Mode,
            services.HasGoogleAuthentication);
        return new AccountGateWindow(viewModel);
    }

    private static string GetEnvironmentName() =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";
}
