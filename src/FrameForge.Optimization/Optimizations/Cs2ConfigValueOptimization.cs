using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Models;

namespace FrameForge.Optimization.Optimizations;

/// <summary>
/// Applies a documented set of CS2 configuration key/value pairs to a cfg file.
/// Always reversible via stored previous values / backup restore.
/// </summary>
public sealed class Cs2ConfigValueOptimization : OptimizationBase
{
    private readonly ICs2ConfigService _configService;
    private readonly ICs2DetectionService _detectionService;
    private readonly string _relativeCfgPath;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly string _id;
    private readonly string _name;
    private readonly string _description;
    private readonly RiskLevel _risk;
    private readonly ExpectedImpact _impact;

    private Dictionary<string, string?>? _lastPreviousValues;
    private string? _lastTargetPath;

    public Cs2ConfigValueOptimization(
        string id,
        string name,
        string description,
        IReadOnlyDictionary<string, string> values,
        ICs2ConfigService configService,
        ICs2DetectionService detectionService,
        string relativeCfgPath = "autoexec.cfg",
        RiskLevel risk = RiskLevel.Low,
        ExpectedImpact impact = ExpectedImpact.Medium)
    {
        _id = id;
        _name = name;
        _description = description;
        _values = values;
        _configService = configService;
        _detectionService = detectionService;
        _relativeCfgPath = relativeCfgPath;
        _risk = risk;
        _impact = impact;
    }

    public override string Id => _id;
    public override string Name => _name;
    public override string Description => _description;
    public override OptimizationCategory Category => OptimizationCategory.Cs2Config;
    public override RiskLevel RiskLevel => _risk;
    public override ExpectedImpact ExpectedImpact => _impact;

    public override IReadOnlyList<BackupRequirement> BackupRequirements =>
        new[]
        {
            new BackupRequirement
            {
                Key = "cs2.cfg",
                Description = $"Backup of {_relativeCfgPath} before modification",
                Required = true
            }
        };

    public override async Task<bool> CanApplyAsync(CancellationToken cancellationToken = default)
    {
        var install = await _detectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
        return install.IsInstalled && !string.IsNullOrWhiteSpace(install.CfgDirectory);
    }

    public override async Task<OptimizationPreview> ExplainAsync(CancellationToken cancellationToken = default)
    {
        var canApply = await CanApplyAsync(cancellationToken).ConfigureAwait(false);
        var changes = _values.Select(kv => $"Set {kv.Key} to {kv.Value}").ToList();

        return CreatePreview(
            canApply,
            canApply
                ? $"Will update documented CS2 settings in {_relativeCfgPath}."
                : "CS2 installation or cfg directory was not found.",
            changes,
            canApply ? null : "CS2 is not detected.");
    }

    public override async Task<OptimizationResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var install = await _detectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
            if (!install.IsInstalled || string.IsNullOrWhiteSpace(install.CfgDirectory))
            {
                return OptimizationResult.Fail(Id, "CS2 cfg directory not available.");
            }

            Directory.CreateDirectory(install.CfgDirectory);
            var target = Path.Combine(install.CfgDirectory, _relativeCfgPath);
            _lastTargetPath = target;

            var document = await _configService.ReadAsync(target, cancellationToken).ConfigureAwait(false);
            var existing = document.ToDictionary();
            var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in _values.Keys)
            {
                previous[key] = existing.TryGetValue(key, out var current) ? current : null;
            }

            _lastPreviousValues = previous;

            // Serialize with every other writer/reader of this cfg scope (settings apply/restore/read).
            await using (var lease = await ManagedConfigGate
                .AcquireAsync(Path.GetDirectoryName(target), cancellationToken)
                .ConfigureAwait(false))
            {
                await _configService.WriteValuesAsync(target, _values, cancellationToken).ConfigureAwait(false);
            }

            return OptimizationResult.Ok(
                Id,
                $"Applied { _values.Count } setting(s) to {target}.",
                previous: previous,
                files: new[] { target });
        }
        catch (Exception ex)
        {
            return OptimizationResult.Fail(Id, ex.Message, ex);
        }
    }

    public override async Task<OptimizationResult> RevertAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_lastPreviousValues is null || string.IsNullOrWhiteSpace(_lastTargetPath))
            {
                return OptimizationResult.Fail(Id, "No previous values stored for revert. Restore from a backup instead.");
            }

            await using var lease = await ManagedConfigGate
                .AcquireAsync(Path.GetDirectoryName(_lastTargetPath), cancellationToken)
                .ConfigureAwait(false);

            // Prefer restoring from the newest side-car backup created by Cs2ConfigService.
            var restoredFromSidecar = TryRestoreNewestSidecar(_lastTargetPath);
            if (restoredFromSidecar)
            {
                return OptimizationResult.Ok(Id, "Reverted from side-car backup.", files: new[] { _lastTargetPath });
            }

            var toRestore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, previous) in _lastPreviousValues)
            {
                if (previous is null)
                {
                    // Key did not exist before. We do not delete user keys silently.
                    // Leave newly introduced keys in place; full file restore covers true rollback.
                    continue;
                }

                toRestore[key] = previous;
            }

            if (toRestore.Count > 0)
            {
                await _configService.WriteValuesAsync(_lastTargetPath, toRestore, cancellationToken)
                    .ConfigureAwait(false);
            }

            return OptimizationResult.Ok(Id, "Reverted known previous values.", files: new[] { _lastTargetPath });
        }
        catch (Exception ex)
        {
            return OptimizationResult.Fail(Id, ex.Message, ex);
        }
    }

    private static bool TryRestoreNewestSidecar(string targetPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            var fileName = Path.GetFileName(targetPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            var pattern = fileName + ".frameforge.bak.*";
            var sidecars = Directory.Exists(directory)
                ? Directory.GetFiles(directory, pattern)
                : Array.Empty<string>();

            if (sidecars.Length == 0)
            {
                return false;
            }

            var newest = sidecars.OrderByDescending(f => f, StringComparer.Ordinal).First();
            File.Copy(newest, targetPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
