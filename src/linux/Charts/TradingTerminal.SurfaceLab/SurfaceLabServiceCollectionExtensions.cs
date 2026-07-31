using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.SurfaceLab;

/// <summary>DI registration for the native macOS 3D Surface Lab. Transient registration gives
/// every window an independent live pipeline, renderer, and disposable view model.</summary>
public static class SurfaceLabServiceCollectionExtensions
{
    public static IServiceCollection AddSurfaceLabSurface(this IServiceCollection services)
    {
        services.AddTransient<SurfaceLabViewModel>();
        services.AddTransient<SurfaceLabWindow>();
        return services;
    }
}
