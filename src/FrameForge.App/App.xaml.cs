using System.Windows;
using FrameForge.App.ViewModels;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;
using FrameForge.Infrastructure.DependencyInjection;
using FrameForge.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace FrameForge.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var collection = new ServiceCollection();
        collection.AddFrameForge();
        collection.AddTransient<MainViewModel>();
        collection.AddTransient<MainWindow>();
        _services = collection.BuildServiceProvider();

        var log = _services.GetRequiredService<IAppLog>();
        log.LogInformation("FrameForge starting.");

        var settings = _services.GetRequiredService<IAppSettingsService>().LoadAsync().GetAwaiter().GetResult();
        if (log is FileAppLog fileLog)
        {
            fileLog.SetMinimumLevel(settings.LoggingLevel);
        }

        if (settings.LaunchAtStartup)
        {
            // TODO: Register HKCU\Software\Microsoft\Windows\CurrentVersion\Run on Windows.
            log.LogDebug("Launch at startup is enabled in settings (registration TODO).");
        }

        var viewModel = _services.GetRequiredService<MainViewModel>();
        viewModel.SetEnumCollections(Enum.GetValues<LogLevelSetting>(), Enum.GetValues<ThemeSetting>());

        MainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<IAppLog>()?.LogInformation("FrameForge shutting down.");
        _services?.Dispose();
        base.OnExit(e);
    }
}
