using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Optimization;

/// <summary>
/// Executes: Detect → Analyze → Preview → Backup → Apply → Verify → Rollback on failure.
/// A failed apply reverts previously applied optimizations in reverse order and restores the backup.
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

            // 4. Backup (capture current files + collect previous values after each apply into backup metadata)
            _log.LogInformation("Pipeline: Backup");
            var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var preApplyPrevious = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (settings.AutomaticBackup)
            {
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
                    try
                    {
                        files.AddRange(Directory.EnumerateFiles(install.CfgDirectory, "frameforge_*.cfg"));
                        files.AddRange(Directory.EnumerateFiles(install.CfgDirectory, "*.cfg")
                            .Where(f => Path.GetFileName(f).StartsWith("frameforge_", StringComparison.OrdinalIgnoreCase)));
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"Could not enumerate cfg directory for backup: {ex.Message}");
                    }
                }

                try
                {
                    var entry = await _backupService.CreateBackupAsync(
                        description: $"Pre-apply backup for {string.Join(", ", ids)}",
                        optimizationIds: ids,
                        affectedFiles: files.Distinct(StringComparer.OrdinalIgnoreCase),
                        previousValues: preApplyPrevious,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    backupId = entry.Id;
                    steps.Add(new PipelineStepResult
                    {
                        StepName = "Backup",
                        Success = true,
                        Message = $"Backup created: {backupId}"
                    });
                }
                catch (Exception ex)
                {
                    steps.Add(new PipelineStepResult
                    {
                        StepName = "Backup",
                        Success = false,
                        Message = $"Backup failed: {ex.Message}"
                    });
                    return Fail(steps, applied, $"Backup failed; apply aborted: {ex.Message}", null, rolledBack: false);
                }
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

                    var rollbackOk = await PerformRollbackAsync(
                        appliedOptimizations,
                        backupId,
                        steps,
                        cancellationToken).ConfigureAwait(false);

                    return Fail(steps, applied, result.Message, backupId, rolledBack: rollbackOk);
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

            // 6. Verify
            _log.LogInformation("Pipeline: Verify");
            var allOk = applied.Count > 0 && applied.All(a => a.Success);
            if (!allOk)
            {
                steps.Add(new PipelineStepResult
                {
                    StepName = "Verify",
                    Success = false,
                    Message = "Verification reported failures."
                });
                var rollbackOk = await PerformRollbackAsync(appliedOptimizations, backupId, steps, cancellationToken)
                    .ConfigureAwait(false);
                return Fail(steps, applied, "Verification failed.", backupId, rolledBack: rollbackOk);
            }

            // Light file presence check for affected paths
            foreach (var result in applied)
            {
                foreach (var file in result.AffectedFiles)
                {
                    if (!string.IsNullOrWhiteSpace(file) && !File.Exists(file))
                    {
                        steps.Add(new PipelineStepResult
                        {
                            StepName = "Verify",
                            Success = false,
                            Message = $"Expected file missing after apply: {file}"
                        });
                        var rollbackOk = await PerformRollbackAsync(appliedOptimizations, backupId, steps, cancellationToken)
                            .ConfigureAwait(false);
                        return Fail(steps, applied, $"Verify failed for {file}", backupId, rolledBack: rollbackOk);
                    }
                }
            }

            steps.Add(new PipelineStepResult
            {
                StepName = "Verify",
                Success = true,
                Message = "All applied optimizations reported success."
            });

            return new OptimizationPipelineResult
            {
                Success = true,
                BackupId = backupId,
                Steps = steps,
                Applied = applied
            };
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Pipeline cancelled.");
            await PerformRollbackAsync(appliedOptimizations, backupId, steps, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError("Pipeline crashed.", ex);
            await PerformRollbackAsync(appliedOptimizations, backupId, steps, CancellationToken.None)
                .ConfigureAwait(false);
            steps.Add(new PipelineStepResult
            {
                StepName = "Error",
                Success = false,
                Message = ex.Message
            });
            return Fail(steps, applied, ex.Message, backupId, rolledBack: true);
        }
    }

    private async Task<bool> PerformRollbackAsync(
        List<IOptimization> applied,
        string? backupId,
        List<PipelineStepResult> steps,
        CancellationToken cancellationToken)
    {
        _log.LogError("Pipeline failure — rolling back.");
        var ok = true;

        // Independently reverse each applied optimization
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            try
            {
                var result = await applied[i].RevertAsync(cancellationToken).ConfigureAwait(false);
                _log.LogInformation($"Reverted {applied[i].Id}: {result.Message}");
                if (!result.Success)
                {
                    ok = false;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                _log.LogError($"Failed to revert {applied[i].Id}", ex);
            }
        }

        // Restore file-level backup as safety net
        if (backupId is not null)
        {
            try
            {
                var restore = await _backupService.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
                if (!restore.Success)
                {
                    ok = false;
                    _log.LogError($"Backup restore during rollback reported failure: {restore.Message}");
                }
            }
            catch (Exception restoreEx)
            {
                ok = false;
                _log.LogError("Backup restore during rollback failed.", restoreEx);
            }
        }

        steps.Add(new PipelineStepResult
        {
            StepName = "Rollback",
            Success = ok,
            Message = ok
                ? "Rollback completed after failure."
                : "Rollback completed with one or more errors — check logs and backups."
        });

        return ok;
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
