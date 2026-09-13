using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Optimization;

/// <summary>
/// Executes: Detect → Analyze → Preview → Backup → Apply → Verify → Rollback on failure.
/// </summary>
public sealed class OptimizationPipeline : IOptimizationPipeline
{
    private readonly IOptimizationCatalog _catalog;
    private readonly IBackupService _backupService;
    private readonly ICs2DetectionService _detectionService;
    private readonly IAppSettingsService _settingsService;
    private readonly IAppLog _log;

    public OptimizationPipeline(
        IOptimizationCatalog catalog,
        IBackupService backupService,
        ICs2DetectionService detectionService,
        IAppSettingsService settingsService,
        IAppLog log)
    {
        _catalog = catalog;
        _backupService = backupService;
        _detectionService = detectionService;
        _settingsService = settingsService;
        _log = log;
    }

    public async Task<OptimizationPipelineResult> ExecuteAsync(
        IEnumerable<string> optimizationIds,
        CancellationToken cancellationToken = default)
    {
        var steps = new List<PipelineStepResult>();
        var applied = new List<OptimizationResult>();
        string? backupId = null;
        var appliedOptimizations = new List<IOptimization>();

        try
        {
            // 1. Detect
            cancellationToken.ThrowIfCancellationRequested();
            _log.LogInformation("Pipeline: Detect");
            var install = await _detectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
            steps.Add(new PipelineStepResult
            {
                StepName = "Detect",
                Success = true,
                Message = install.IsInstalled
                    ? $"CS2 found at {install.InstallPath}"
                    : "CS2 not installed (config optimizations may be skipped)."
            });

            // 2. Analyze
            _log.LogInformation("Pipeline: Analyze");
            var selected = new List<IOptimization>();
            foreach (var id in optimizationIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var opt = _catalog.GetById(id);
                if (opt is null)
                {
                    steps.Add(new PipelineStepResult
                    {
                        StepName = "Analyze",
                        Success = false,
                        Message = $"Unknown optimization id: {id}"
                    });
                    return Fail(steps, applied, "Unknown optimization id.", backupId, rolledBack: false);
                }

                selected.Add(opt);
            }

            steps.Add(new PipelineStepResult
            {
                StepName = "Analyze",
                Success = true,
                Message = $"Resolved {selected.Count} optimization(s)."
            });

            // 3. Preview / CanApply filter
            _log.LogInformation("Pipeline: Preview");
            var runnable = new List<IOptimization>();
            foreach (var opt in selected)
            {
                var preview = await opt.ExplainAsync(cancellationToken).ConfigureAwait(false);
                var canApply = await opt.CanApplyAsync(cancellationToken).ConfigureAwait(false);
                if (!canApply || !preview.CanApply)
                {
                    _log.LogWarning($"Skipping {opt.Id}: {preview.BlockReason ?? "cannot apply"}");
                    steps.Add(new PipelineStepResult
                    {
                        StepName = "Preview",
                        Success = true,
                        Message = $"Skipped {opt.Id}: {preview.BlockReason ?? "cannot apply"}"
                    });
                    continue;
                }

                runnable.Add(opt);
            }

            steps.Add(new PipelineStepResult
            {
                StepName = "Preview",
                Success = true,
                Message = $"{runnable.Count} optimization(s) ready to apply."
            });

            if (runnable.Count == 0)
            {
                return new OptimizationPipelineResult
                {
                    Success = true,
                    Steps = steps,
                    Applied = applied,
                    ErrorMessage = "No applicable optimizations to run."
                };
            }

            // 4. Backup
            _log.LogInformation("Pipeline: Backup");
            var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings.AutomaticBackup)
            {
                var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var files = new List<string>();
                var ids = runnable.Select(r => r.Id).ToList();

                foreach (var opt in runnable)
                {
                    foreach (var req in opt.BackupRequirements)
                    {
                        if (!string.IsNullOrWhiteSpace(req.FilePath) && File.Exists(req.FilePath))
                        {
                            files.Add(req.FilePath);
                        }
                    }
                }

                if (install.IsInstalled && !string.IsNullOrWhiteSpace(install.CfgDirectory) &&
                    Directory.Exists(install.CfgDirectory))
                {
                    files.AddRange(Directory.EnumerateFiles(install.CfgDirectory, "frameforge_*.cfg"));
                }

                var entry = await _backupService.CreateBackupAsync(
                    description: $"Pre-apply backup for {string.Join(", ", ids)}",
                    optimizationIds: ids,
                    affectedFiles: files.Distinct(StringComparer.OrdinalIgnoreCase),
                    previousValues: previous,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                backupId = entry.Id;
                steps.Add(new PipelineStepResult
                {
                    StepName = "Backup",
                    Success = true,
                    Message = $"Backup created: {backupId}"
                });
            }
            else
            {
                steps.Add(new PipelineStepResult
                {
                    StepName = "Backup",
                    Success = true,
                    Message = "Automatic backup disabled by settings."
                });
            }

            // 5. Apply
            _log.LogInformation("Pipeline: Apply");
            foreach (var opt in runnable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await opt.ApplyAsync(cancellationToken).ConfigureAwait(false);
                applied.Add(result);
                if (!result.Success)
                {
                    steps.Add(new PipelineStepResult
                    {
                        StepName = "Apply",
                        Success = false,
                        Message = $"Failed {opt.Id}: {result.Message}"
                    });

                    // 7. Rollback on failure
                    _log.LogError($"Pipeline failure on {opt.Id}, rolling back.");
                    await RollbackAsync(appliedOptimizations, cancellationToken).ConfigureAwait(false);
                    if (backupId is not null)
                    {
                        try
                        {
                            await _backupService.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception restoreEx)
                        {
                            _log.LogError("Backup restore during rollback failed.", restoreEx);
                        }
                    }

                    steps.Add(new PipelineStepResult
                    {
                        StepName = "Rollback",
                        Success = true,
                        Message = "Rollback completed after failure."
                    });

                    return Fail(steps, applied, result.Message, backupId, rolledBack: true);
                }

                appliedOptimizations.Add(opt);
                _log.LogInformation($"Applied optimization {opt.Id}");
            }

            steps.Add(new PipelineStepResult
            {
                StepName = "Apply",
                Success = true,
                Message = $"Applied {appliedOptimizations.Count} optimization(s)."
            });

            // 6. Verify (lightweight — ensure no exception and results marked success)
            _log.LogInformation("Pipeline: Verify");
            var allOk = applied.All(a => a.Success);
            steps.Add(new PipelineStepResult
            {
                StepName = "Verify",
                Success = allOk,
                Message = allOk ? "All applied optimizations reported success." : "Verification reported failures."
            });

            return new OptimizationPipelineResult
            {
                Success = allOk,
                BackupId = backupId,
                Steps = steps,
                Applied = applied
            };
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Pipeline cancelled.");
            await RollbackAsync(appliedOptimizations, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError("Pipeline crashed.", ex);
            await RollbackAsync(appliedOptimizations, CancellationToken.None).ConfigureAwait(false);
            steps.Add(new PipelineStepResult
            {
                StepName = "Error",
                Success = false,
                Message = ex.Message
            });
            return Fail(steps, applied, ex.Message, backupId, rolledBack: true);
        }
    }

    private async Task RollbackAsync(List<IOptimization> applied, CancellationToken cancellationToken)
    {
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            try
            {
                var result = await applied[i].RevertAsync(cancellationToken).ConfigureAwait(false);
                _log.LogInformation($"Reverted {applied[i].Id}: {result.Message}");
            }
            catch (Exception ex)
            {
                _log.LogError($"Failed to revert {applied[i].Id}", ex);
            }
        }
    }

    private static OptimizationPipelineResult Fail(
        List<PipelineStepResult> steps,
        List<OptimizationResult> applied,
        string error,
        string? backupId,
        bool rolledBack) =>
        new()
        {
            Success = false,
            Steps = steps,
            Applied = applied,
            ErrorMessage = error,
            BackupId = backupId,
            RolledBack = rolledBack
        };
}
