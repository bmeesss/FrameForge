using FrameForge.Benchmark;
using FrameForge.Core.Abstractions;
using FrameForge.CS2;
using FrameForge.Hardware;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Dashboard;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Profiles;
using FrameForge.Infrastructure.Settings;
using FrameForge.Optimization;
using Microsoft.Extensions.DependencyInjection;

namespace FrameForge.Infrastructure.DependencyInjection;

/// <summary>
/// Composition root helpers for FrameForge services.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddFrameForge(this IServiceCollection services, string? dataRootOverride = null)
    {
        services.AddSingleton<IPathService>(_ => new PathService(dataRootOverride));
        services.AddSingleton<IAppLog>(sp => new FileAppLog(sp.GetRequiredService<IPathService>()));
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IHardwareInfoService, HardwareInfoService>();
        services.AddSingleton<ICs2DetectionService, Cs2DetectionService>();
        services.AddSingleton<ICs2ConfigService, Cs2ConfigService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IOptimizationCatalog, OptimizationCatalog>();
        services.AddSingleton<IOptimizationPipeline, OptimizationPipeline>();
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddSingleton<BasicSystemBenchmark>();
        return services;
    }

    public static ServiceProvider BuildFrameForgeProvider(string? dataRootOverride = null)
    {
        var services = new ServiceCollection();
        services.AddFrameForge(dataRootOverride);
        return services.BuildServiceProvider();
    }
}
