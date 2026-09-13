using System.Security.Cryptography;
using System.Text;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Json;
using FrameForge.Core.Models;
using FrameForge.CS2.Settings;

namespace FrameForge.Infrastructure.Performance;

/// <summary>
/// Centralized watcher for FrameForge-managed cfg files.
/// Uses FileSystemWatcher when available; falls back to hash compare on demand.
/// Does not keep the process alive when stopped/disposed.
/// </summary>
public sealed class ManagedConfigWatcher : IManagedConfigWatcher
{
    public const string HashStoreFileName = "managed-file-hashes.json";

    private readonly ICs2DetectionService _detection;
    private readonly ICs2SettingsService _settings;
    private readonly IPathService _paths;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;
    private bool _watching;
    private string? _cfgDir;
    private string? _managedPath;
    private string? _autoexecPath;
    private DateTimeOffset _lastEvent = DateTimeOffset.MinValue;

    public ManagedConfigWatcher(
        ICs2DetectionService detection,
        ICs2SettingsService settings,
        IPathService paths,
        IAppLog log)
    {
        _detection = detection;
        _settings = settings;
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.PerformanceHistoryDirectory);
    }

    public bool IsWatching
    {
        get { lock (_gate) return _watching; }
    }

    public event EventHandler<ExternalConfigChangeEventArgs>? ExternalChangeDetected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var install = await _detection.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
        {
            _log.LogDebug("ManagedConfigWatcher: CS2 cfg not available — not starting.");
            return;
        }

        lock (_gate)
        {
            if (_watching)
            {
                return;
            }

            _cfgDir = install.CfgDirectory;
            _managedPath = Path.Combine(_cfgDir, Cs2SettingsService.ManagedFileName);
            _autoexecPath = Path.Combine(_cfgDir, Cs2AutoexecIntegration.AutoexecFileName);

            try
            {
                _watcher = new FileSystemWatcher(_cfgDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    Filter = "*.cfg",
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFsEvent;
                _watcher.Created += OnFsEvent;
                _watcher.Renamed += OnFsRenamed;
                _watcher.Deleted += OnFsEvent;
                _watching = true;
                _log.LogInformation($"ManagedConfigWatcher started on {_cfgDir}");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"FileSystemWatcher unavailable ({ex.Message}); hash-only mode.");
                _watching = true; // still "active" for hash checks
            }
        }

        // Ensure baseline exists
        if (!File.Exists(HashStorePath))
        {
            await CaptureBaselineAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            DisposeWatcher_NoLock();
            _watching = false;
        }

        _log.LogInformation("ManagedConfigWatcher stopped.");
    }

    public async Task CaptureBaselineAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsurePathsAsync(cancellationToken).ConfigureAwait(false);
        var doc = new ManagedFileHashStoreDocument();
        if (!string.IsNullOrWhiteSpace(_managedPath))
        {
            doc.Files.Add(await HashFileAsync(_managedPath, sectionOnly: false, cancellationToken).ConfigureAwait(false));
        }

        if (!string.IsNullOrWhiteSpace(_autoexecPath))
        {
            doc.Files.Add(await HashFileAsync(_autoexecPath, sectionOnly: true, cancellationToken).ConfigureAwait(false));
        }

        await FrameForgeJson.SerializeFileAsync(HashStorePath, doc, cancellationToken).ConfigureAwait(false);
        _log.LogDebug("Captured managed file hash baseline.");
    }

    public async Task<bool> HasExternalChangesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsurePathsAsync(cancellationToken).ConfigureAwait(false);
        var baseline = await LoadHashesAsync(cancellationToken).ConfigureAwait(false);
        if (baseline.Files.Count == 0)
        {
            return false;
        }

        foreach (var rec in baseline.Files)
        {
            if (string.IsNullOrWhiteSpace(rec.FilePath))
            {
                continue;
            }

            var sectionOnly = rec.ManagedSectionHashSha256 is not null && rec.ContentHashSha256 is null
                || (rec.FilePath.EndsWith("autoexec.cfg", StringComparison.OrdinalIgnoreCase));
            var current = await HashFileAsync(rec.FilePath, sectionOnly, cancellationToken).ConfigureAwait(false);
            if (sectionOnly)
            {
                if (!string.Equals(rec.ManagedSectionHashSha256, current.ManagedSectionHashSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (!string.Equals(rec.ContentHashSha256, current.ContentHashSha256, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Task InvalidateRestoreCacheAsync()
    {
        // Assessments are re-read from disk each call; this signals listeners.
        ExternalChangeDetected?.Invoke(this, new ExternalConfigChangeEventArgs
        {
            FilePath = _managedPath ?? string.Empty,
            ChangeKind = "Invalidate",
            Message = "Targeted restore assessments invalidated due to external or baseline change.",
            DetectedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e) =>
        HandlePath(e.FullPath ?? e.OldFullPath, "Renamed");

    private void OnFsEvent(object sender, FileSystemEventArgs e) =>
        HandlePath(e.FullPath, e.ChangeType.ToString());

    private void HandlePath(string? path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var name = Path.GetFileName(path);
        var isManaged = name.Equals(Cs2SettingsService.ManagedFileName, StringComparison.OrdinalIgnoreCase);
        var isAutoexec = name.Equals(Cs2AutoexecIntegration.AutoexecFileName, StringComparison.OrdinalIgnoreCase);
        if (!isManaged && !isAutoexec)
        {
            return;
        }

        // Debounce
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastEvent).TotalMilliseconds < 250)
        {
            return;
        }

        _lastEvent = now;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false); // let writers finish
                var baseline = await LoadHashesAsync(CancellationToken.None).ConfigureAwait(false);
                var rec = baseline.Files.FirstOrDefault(f =>
                    f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));

                if (isAutoexec)
                {
                    var current = await HashFileAsync(path, sectionOnly: true, CancellationToken.None).ConfigureAwait(false);
                    var sectionChanged = rec is null ||
                        !string.Equals(rec.ManagedSectionHashSha256, current.ManagedSectionHashSha256, StringComparison.OrdinalIgnoreCase);
                    if (!sectionChanged)
                    {
                        // User content outside markers only — do not invalidate setting restores
                        ExternalChangeDetected?.Invoke(this, new ExternalConfigChangeEventArgs
                        {
                            FilePath = path,
                            ChangeKind = kind,
                            ManagedSectionChanged = false,
                            UserSectionOnly = true,
                            Message = "autoexec.cfg changed outside FRAMEFORGE markers only — setting restore safety unchanged."
                        });
                        return;
                    }

                    await InvalidateRestoreCacheAsync().ConfigureAwait(false);
                    ExternalChangeDetected?.Invoke(this, new ExternalConfigChangeEventArgs
                    {
                        FilePath = path,
                        ChangeKind = kind,
                        ManagedSectionChanged = true,
                        Message = "FrameForge-managed autoexec section changed externally. Targeted restore assessments invalidated."
                    });
                    return;
                }

                // managed settings file
                var cur = await HashFileAsync(path, sectionOnly: false, CancellationToken.None).ConfigureAwait(false);
                if (rec is not null &&
                    string.Equals(rec.ContentHashSha256, cur.ContentHashSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return; // our own write matching baseline
                }

                await InvalidateRestoreCacheAsync().ConfigureAwait(false);
                ExternalChangeDetected?.Invoke(this, new ExternalConfigChangeEventArgs
                {
                    FilePath = path,
                    ChangeKind = kind,
                    ManagedSectionChanged = true,
                    Message =
                        "frameforge_settings.cfg changed. If this was outside FrameForge, targeted restore may become UnsafeToTargetRestore."
                });
            }
            catch (Exception ex)
            {
                _log.LogDebug($"Watcher handle failed: {ex.Message}");
            }
        });
    }

    private async Task EnsurePathsAsync(CancellationToken cancellationToken)
    {
        if (_managedPath is not null)
        {
            return;
        }

        var snap = await _settings.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        _cfgDir = snap.CfgDirectory;
        _managedPath = snap.ManagedConfigPath;
        _autoexecPath = snap.AutoexecPath;
    }

    private string HashStorePath =>
        Path.Combine(_paths.PerformanceHistoryDirectory, HashStoreFileName);

    private async Task<ManagedFileHashStoreDocument> LoadHashesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HashStorePath))
        {
            return new ManagedFileHashStoreDocument();
        }

        try
        {
            return await FrameForgeJson.DeserializeFileAsync<ManagedFileHashStoreDocument>(HashStorePath, cancellationToken)
                .ConfigureAwait(false) ?? new ManagedFileHashStoreDocument();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Corrupt managed hash store; quarantining: {ex.Message}");
            try
            {
                File.Move(HashStorePath, HashStorePath + $".corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}");
            }
            catch { /* ignore */ }

            return new ManagedFileHashStoreDocument();
        }
    }

    private static async Task<ManagedFileHashRecord> HashFileAsync(
        string path,
        bool sectionOnly,
        CancellationToken cancellationToken)
    {
        var rec = new ManagedFileHashRecord
        {
            FilePath = path,
            CapturedAt = DateTimeOffset.UtcNow
        };

        if (!File.Exists(path))
        {
            rec.ContentHashSha256 = null;
            rec.ManagedSectionHashSha256 = null;
            rec.FileLength = null;
            return rec;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        rec.FileLength = new FileInfo(path).Length;

        if (sectionOnly)
        {
            var section = ExtractManagedSection(text);
            rec.ManagedSectionHashSha256 = Sha256Hex(section);
            rec.ContentHashSha256 = null;
        }
        else
        {
            rec.ContentHashSha256 = Sha256Hex(text);
        }

        return rec;
    }

    /// <summary>Extract FRAMEFORGE BEGIN…END block; empty if missing.</summary>
    public static string ExtractManagedSection(string autoexecContent)
    {
        if (string.IsNullOrEmpty(autoexecContent))
        {
            return string.Empty;
        }

        const string begin = "FRAMEFORGE BEGIN";
        const string end = "FRAMEFORGE END";
        var b = autoexecContent.IndexOf(begin, StringComparison.OrdinalIgnoreCase);
        var e = autoexecContent.IndexOf(end, StringComparison.OrdinalIgnoreCase);
        if (b < 0 || e < 0 || e <= b)
        {
            return string.Empty;
        }

        return autoexecContent.Substring(b, e + end.Length - b);
    }

    public static string Sha256Hex(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void DisposeWatcher_NoLock()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFsEvent;
            _watcher.Created -= OnFsEvent;
            _watcher.Renamed -= OnFsRenamed;
            _watcher.Deleted -= OnFsEvent;
            _watcher.Dispose();
        }
        catch { /* ignore */ }

        _watcher = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ManagedConfigWatcher));
        }
    }
}
