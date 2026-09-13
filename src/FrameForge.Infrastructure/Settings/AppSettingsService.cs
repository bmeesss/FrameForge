using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Settings;

public sealed class AppSettingsService : IAppSettingsService
{
    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettingsService(IPathService paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_paths.SettingsFilePath))
            {
                var defaults = AppSettings.CreateDefault();
                await FrameForgeJson.SerializeFileAsync(_paths.SettingsFilePath, defaults, cancellationToken)
                    .ConfigureAwait(false);
                return defaults;
            }

            var loaded = await FrameForgeJson.DeserializeFileAsync<AppSettings>(_paths.SettingsFilePath, cancellationToken)
                .ConfigureAwait(false);
            return loaded ?? AppSettings.CreateDefault();
        }
        catch (Exception ex)
        {
            _log.LogError("Failed to load settings; using defaults.", ex);
            return AppSettings.CreateDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameForgeJson.SerializeFileAsync(_paths.SettingsFilePath, settings, cancellationToken)
                .ConfigureAwait(false);
            _log.LogInformation("Settings saved.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var defaults = AppSettings.CreateDefault();
            await FrameForgeJson.SerializeFileAsync(_paths.SettingsFilePath, defaults, cancellationToken)
                .ConfigureAwait(false);
            _log.LogWarning("Application settings reset to defaults.");
        }
        finally
        {
            _gate.Release();
        }
    }
}
