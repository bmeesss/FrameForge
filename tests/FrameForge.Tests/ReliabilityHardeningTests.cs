using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Models;
using FrameForge.CS2;
using FrameForge.CS2.Settings;
using FrameForge.Infrastructure.Backup;
using FrameForge.Infrastructure.Logging;
using FrameForge.Infrastructure.Paths;
using FrameForge.Infrastructure.Performance;
using FrameForge.Infrastructure.Settings;
using Xunit;

namespace FrameForge.Tests;

/// <summary>
/// Reliability / transactional hardening phase:
///   - apply always has an internal recovery path (independent of AutomaticBackup)
///   - configuration operations are serialized per managed configuration scope
///   - autoexec.cfg is validated structurally (managed section only) instead of heuristically
///   - AtomicFile reports AtomicReplace vs FallbackReplace honestly and never leaves the
///     destination deleted
///
/// Concurrency tests are deterministic: they synchronise on TaskCompletionSource signals,
/// never on sleeps. The only timer in this file is a watchdog that fails a test instead of
/// letting a broken lock hang the suite forever.
/// </summary>
public sealed class ReliabilityHardeningTests
{
    // -------------------------------------------------------------------------------------
    // 1. Recovery snapshot (independent of the optional user backup)
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_WithAutomaticBackupDisabled_Succeeds_AndCleansUpRecoverySnapshot()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: false,
            autoexec: "// user settings" + Nl + "sensitivity 2.2" + Nl,
            managed: "fps_max 400" + Nl);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" },
            reason: "no-backup-apply");

        Assert.True(result.Success, result.Message);
        Assert.Null(result.BackupId);
        Assert.True(result.Recovery.SnapshotCreated);
        Assert.NotNull(result.Recovery.SnapshotId);
        Assert.False(result.Recovery.RestoreAttempted);
        Assert.True(result.Recovery.CleanedUp);
        Assert.Equal(0, result.Recovery.FailedFiles.Count);

        // The temporary recovery snapshot is removed after a verified success.
        var snapshot = AssertSingle(h.Recovery.Created);
        Assert.False(Directory.Exists(snapshot.SnapshotDirectory));
        Assert.True(snapshot.IsCleanedUp);

        // AutomaticBackup only controls the user-visible backup, never apply behaviour.
        Assert.Empty(await h.Backups.ListBackupsAsync());
        Assert.Equal("0", (await h.ReadManagedAsync())["fps_max"]);
    }

    [Fact]
    public async Task Apply_VerificationFailure_WithBackupDisabled_RestoresEveryFileFromRecoverySnapshot()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: false,
            autoexec: "// user top" + Nl + "bind \"p\" \"say hello\"" + Nl,
            managed: "fps_max 400" + Nl);

        var managedBefore = await File.ReadAllTextAsync(h.ManagedPath);
        var autoexecBefore = await File.ReadAllTextAsync(h.AutoexecPath);

        // Corrupt the managed file silently after the write so verification must fail.
        h.Config.AfterWriteValues = path => File.WriteAllText(path, "fps_max 999" + Nl);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" },
            reason: "verify-failure");

        Assert.False(result.Success);
        Assert.Contains("Verification failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Recovery.SnapshotCreated);
        Assert.True(result.Recovery.RestoreAttempted);
        Assert.True(result.Recovery.RestoreSucceeded);
        Assert.False(result.Recovery.RestoreFailed);

        // Every affected file is byte-exact again — even though no backup existed.
        Assert.Equal(managedBefore, await File.ReadAllTextAsync(h.ManagedPath));
        Assert.Equal(autoexecBefore, await File.ReadAllTextAsync(h.AutoexecPath));
        Assert.Equal(0, result.Recovery.FailedFiles.Count);
    }

    [Fact]
    public async Task Apply_ExceptionDuringWrite_WithBackupDisabled_RecoversOriginalState()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: false,
            autoexec: "// keep me" + Nl + "volume 0.7" + Nl,
            managed: null);

        var autoexecBefore = await File.ReadAllTextAsync(h.AutoexecPath);

        h.Config.BeforeWriteValues = (path, values, ct) =>
            throw new InvalidOperationException("simulated write failure (disk full)");

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" },
            reason: "write-failure");

        Assert.False(result.Success);
        Assert.Contains("simulated write failure", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.BackupId);
        Assert.True(result.Recovery.RestoreAttempted);
        Assert.True(result.Recovery.RestoreSucceeded);

        // The managed file did not exist before the apply → recovery must delete it again.
        Assert.False(File.Exists(h.ManagedPath));
        Assert.Equal(autoexecBefore, await File.ReadAllTextAsync(h.AutoexecPath));
    }

    [Fact]
    public async Task Apply_RecoverySnapshotLost_ReportsHardFailure()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: false,
            autoexec: "// user" + Nl,
            managed: "fps_max 400" + Nl);

        // Simulate a lost/corrupted recovery snapshot: restore cannot succeed any more.
        h.Recovery.OnCreated = snapshot =>
        {
            if (Directory.Exists(snapshot.SnapshotDirectory))
            {
                Directory.Delete(snapshot.SnapshotDirectory, recursive: true);
            }
        };

        h.Config.BeforeWriteValues = (path, values, ct) =>
            throw new InvalidOperationException("simulated apply failure");

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" },
            reason: "recovery-failure");

        Assert.False(result.Success);
        Assert.True(result.Recovery.RestoreAttempted);
        Assert.False(result.Recovery.RestoreSucceeded);
        Assert.True(result.Recovery.RestoreFailed);
        Assert.NotEmpty(result.Recovery.FailedFiles);
        Assert.Contains("RECOVERY FAILED", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual intervention", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoverySnapshot_RestoresExactBytes_AndDeletesFilesCreatedAfterSnapshot()
    {
        var root = NewRoot();
        try
        {
            var existing = Path.Combine(root, "autoexec.cfg");
            var createdLater = Path.Combine(root, "frameforge_settings.cfg");
            await File.WriteAllTextAsync(existing, "bind w +forward" + Nl + "// user line" + Nl);

            using var snapshot = RecoverySnapshot.Create(new[] { existing, createdLater });
            Assert.True(snapshot.IsComplete);
            Assert.Equal(2, snapshot.Entries.Count);

            var original = await File.ReadAllBytesAsync(existing);
            await File.WriteAllTextAsync(existing, "// clobbered" + Nl);
            await File.WriteAllTextAsync(createdLater, "fps_max 0" + Nl);

            var report = snapshot.Restore();
            Assert.True(report.Success, report.Message);
            Assert.Equal(2, report.RestoredFiles.Count);

            // Read first: byte[].SequenceEqual resolves to the ReadOnlySpan<byte> overload and a
            // span cannot be preserved across the await boundary.
            var restored = await File.ReadAllBytesAsync(existing);
            Assert.True(original.SequenceEqual(restored));
            Assert.False(File.Exists(createdLater));

            Assert.True(snapshot.TryCleanup(out var cleanupError), cleanupError);
            Assert.False(Directory.Exists(snapshot.SnapshotDirectory));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task RecoverySnapshot_MissingData_FailsSafely()
    {
        var root = NewRoot();
        try
        {
            var file = Path.Combine(root, "autoexec.cfg");
            await File.WriteAllTextAsync(file, "// user" + Nl);

            using var snapshot = RecoverySnapshot.Create(new[] { file });
            Directory.Delete(snapshot.SnapshotDirectory, recursive: true);
            await File.WriteAllTextAsync(file, "// clobbered" + Nl);

            var report = snapshot.Restore();
            Assert.False(report.Success);
            Assert.Contains(report.FailedFiles, f => string.Equals(f, file, StringComparison.Ordinal));
            Assert.NotEmpty(report.Details);
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // 2. Concurrency — one gate per managed configuration scope
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentApplies_SerializeAndNeverInterleaveWrites()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        var entered = NewSignal();
        var release = NewSignal();
        h.Config.BeforeWriteValues = async (path, values, ct) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var first = h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" }, "concurrent-1");
        await WaitAsync(entered.Task, "first apply to enter its write");

        var second = h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "144" }, "concurrent-2");

        // The second apply must wait for the scope gate instead of writing concurrently.
        Assert.False(second.IsCompleted);

        release.TrySetResult();

        var firstResult = await WaitAsync(first, "first apply");
        var secondResult = await WaitAsync(second, "second apply");

        Assert.True(firstResult.Success, firstResult.Message);
        Assert.True(secondResult.Success, secondResult.Message);

        // Serialized: the write section was never entered by two operations at once.
        Assert.Equal(1, h.Config.MaxConcurrentOperations);

        // No interleaved/duplicated output.
        var text = await File.ReadAllTextAsync(h.ManagedPath);
        Assert.Equal(1, CountSettingLines(text, "fps_max"));
        Assert.True(text.Contains("fps_max 0", StringComparison.Ordinal) ||
                    text.Contains("fps_max 144", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentApplyAndRestore_Serialize()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        var seeded = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" }, "seed");
        Assert.True(seeded.Success, seeded.Message);
        Assert.NotNull(seeded.BackupId);

        var entered = NewSignal();
        var release = NewSignal();
        h.Config.BeforeWriteValues = async (path, values, ct) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var apply = h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "240" }, "with-restore");
        await WaitAsync(entered.Task, "apply to enter its write");

        var restore = h.Service.RestoreLastFrameForgeChangesAsync();
        Assert.False(restore.IsCompleted);

        release.TrySetResult();

        var applyResult = await WaitAsync(apply, "apply");
        var restoreResult = await WaitAsync(restore, "restore");

        Assert.True(applyResult.Success, applyResult.Message);
        Assert.True(restoreResult.Success, restoreResult.Message);

        // The restore ran after the apply had finished, so it reverts *that* apply: the value
        // the apply overwrote (fps_max 0, written by the seed) is back. Had the restore won the
        // gate first it would have reverted the older backup instead and left 240 behind.
        var map = await h.ReadManagedAsync();
        Assert.Equal("0", map["fps_max"]);
        Assert.Equal(1, h.Config.MaxConcurrentOperations);
    }

    [Fact]
    public async Task ConcurrentApplyAndRead_Serialize()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        var entered = NewSignal();
        var release = NewSignal();
        h.Config.BeforeWriteValues = async (path, values, ct) =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var apply = h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "60" }, "with-read");
        await WaitAsync(entered.Task, "apply to enter its write");

        var read = h.Service.ReadSettingsAsync();
        Assert.False(read.IsCompleted);

        release.TrySetResult();

        var applyResult = await WaitAsync(apply, "apply");
        var snapshot = await WaitAsync(read, "read");

        Assert.True(applyResult.Success, applyResult.Message);
        Assert.True(snapshot.Cs2Available);

        // A read never observes a file while another operation is mutating it.
        Assert.Equal(1, h.Config.MaxConcurrentOperations);

        var fps = snapshot.Settings.First(s => s.Definition.ConfigKey == "fps_max");
        Assert.NotNull(fps.CurrentValue);
    }

    [Fact]
    public async Task ConcurrentRestoreAndRead_Serialize()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        var seeded = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" }, "seed");
        Assert.True(seeded.Success, seeded.Message);

        var entered = NewSignal();
        var release = NewSignal();
        h.Backups.BeforeRestore = async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var restore = h.Service.RestoreLastFrameForgeChangesAsync();
        await WaitAsync(entered.Task, "restore to enter BackupService.RestoreAsync");

        var read = h.Service.ReadSettingsAsync();
        Assert.False(read.IsCompleted);

        release.TrySetResult();

        var restoreResult = await WaitAsync(restore, "restore");
        var snapshot = await WaitAsync(read, "read");

        Assert.True(restoreResult.Success, restoreResult.Message);
        Assert.True(snapshot.Cs2Available);
        Assert.Equal("400", (await h.ReadManagedAsync())["fps_max"]);
    }

    [Fact]
    public async Task CancelledOperation_ReleasesTheScopeGate()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            h.Service.ApplySettingsAsync(
                new Dictionary<string, string> { ["fps_max"] = "0" },
                "cancelled",
                cancellationToken: cts.Token));

        // The gate was released, so the next operation is not blocked.
        var followUp = await WaitAsync(
            h.Service.ApplySettingsAsync(new Dictionary<string, string> { ["fps_max"] = "0" }, "after-cancel"),
            "follow-up apply after cancellation");
        Assert.True(followUp.Success, followUp.Message);
    }

    // -------------------------------------------------------------------------------------
    // 3. Managed-section structural validation
    // -------------------------------------------------------------------------------------

    [Fact]
    public void MarkerInspection_DetectsEveryAmbiguousState()
    {
        var begin = Cs2AutoexecIntegration.BeginMarker;
        var end = Cs2AutoexecIntegration.EndMarker;

        AssertState("// just user" + Nl + "sensitivity 1" + Nl, AutoexecMarkerState.None, ambiguous: false);
        AssertState(begin + Nl + ExecLine + Nl + end + Nl, AutoexecMarkerState.Complete, ambiguous: false);
        AssertState(begin + Nl + ExecLine + Nl, AutoexecMarkerState.MissingEnd, ambiguous: true);
        AssertState("// user" + Nl + end + Nl, AutoexecMarkerState.MissingBegin, ambiguous: true);
        AssertState(end + Nl + begin + Nl, AutoexecMarkerState.OutOfOrder, ambiguous: true);

        // Duplicate markers
        AssertState(begin + Nl + ExecLine + Nl + end + Nl + begin + Nl + ExecLine + Nl + end + Nl,
            AutoexecMarkerState.DuplicateBegin, ambiguous: true);
        AssertState(begin + Nl + ExecLine + Nl + end + Nl + end + Nl,
            AutoexecMarkerState.DuplicateEnd, ambiguous: true);

        // Nested markers
        AssertState(begin + Nl + ExecLine + Nl + begin + Nl + ExecLine + Nl + end + Nl,
            AutoexecMarkerState.Nested, ambiguous: true);

        // Ambiguous files are never repaired silently.
        var inspection = Cs2AutoexecIntegration.InspectMarkers(begin + Nl + ExecLine + Nl);
        Assert.True(inspection.IsAmbiguous);
        Assert.NotNull(inspection.UserAction);
        Assert.False(inspection.CanEditManagedSection);
    }

    [Fact]
    public async Task Apply_RefusesAmbiguousMarkers_BeforeWritingAnything()
    {
        var begin = Cs2AutoexecIntegration.BeginMarker;
        var end = Cs2AutoexecIntegration.EndMarker;

        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: "// user" + Nl + begin + Nl + ExecLine + Nl + begin + Nl + ExecLine + Nl + end + Nl,
            managed: null);

        var autoexecBefore = await File.ReadAllTextAsync(h.AutoexecPath);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" }, "ambiguous");

        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FixHint(), result.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing was written and no recovery snapshot was needed.
        Assert.False(result.Recovery.SnapshotCreated);
        Assert.Empty(h.Recovery.Created);
        Assert.False(File.Exists(h.ManagedPath));
        Assert.Equal(autoexecBefore, await File.ReadAllTextAsync(h.AutoexecPath));
    }

    [Fact]
    public void StructuralValidation_AcceptsOnlyManagedSectionChanges()
    {
        var begin = Cs2AutoexecIntegration.BeginMarker;
        var end = Cs2AutoexecIntegration.EndMarker;

        var before = "// user top" + Nl + "bind \"f\" \"slot1\"" + Nl +
                     begin + Nl + "exec some_old.cfg" + Nl + end + Nl +
                     "// user bottom" + Nl;

        var after = Cs2AutoexecIntegration.EnsureFrameForgeSection(before);
        var check = Cs2AutoexecIntegration.ValidateUserContentPreserved(before, after);
        Assert.True(check.IsValid, check.Reason);

        // Only the managed section changed.
        Assert.Contains("// user top", after);
        Assert.Contains("bind \"f\" \"slot1\"", after);
        Assert.Contains("// user bottom", after);
        Assert.DoesNotContain("some_old.cfg", after);
        Assert.Contains(Cs2AutoexecIntegration.ExecCommand, after);

        // Re-applying is stable (no phantom changes reported).
        var again = Cs2AutoexecIntegration.EnsureFrameForgeSection(after);
        Assert.True(Cs2AutoexecIntegration.ValidateUserContentPreserved(after, again).IsValid);

        // Appending a section to a file without markers is valid too.
        var fresh = Cs2AutoexecIntegration.EnsureFrameForgeSection("// only user" + Nl);
        Assert.True(Cs2AutoexecIntegration.ValidateUserContentPreserved("// only user" + Nl, fresh).IsValid);
    }

    [Fact]
    public void StructuralValidation_RejectsReorderedDuplicatedOrRelocatedUserContent()
    {
        var begin = Cs2AutoexecIntegration.BeginMarker;
        var end = Cs2AutoexecIntegration.EndMarker;

        var before = "// alpha" + Nl + "bind \"f\" \"slot1\"" + Nl +
                     begin + Nl + ExecLine + Nl + end + Nl +
                     "// omega" + Nl;

        var layout = Cs2AutoexecIntegration.TryGetManagedLayout(before, out var parsed);
        Assert.True(layout);

        // Reordered user lines — the old "do the lines still appear somewhere" check passed this.
        var reordered = "bind \"f\" \"slot1\"" + Nl + "// alpha" + Nl + parsed.Managed + "// omega" + Nl;
        Assert.False(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, reordered).IsValid);

        // Duplicated user line.
        var duplicated = parsed.Prefix + "// alpha" + Nl + parsed.Managed + parsed.Suffix;
        Assert.False(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, duplicated).IsValid);

        // User line relocated from the prefix to the suffix.
        var relocated = "bind \"f\" \"slot1\"" + Nl + parsed.Managed + parsed.Suffix + "// alpha" + Nl;
        Assert.False(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, relocated).IsValid);

        // User line dropped entirely.
        var dropped = "// alpha" + Nl + parsed.Managed + parsed.Suffix;
        Assert.False(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, dropped).IsValid);
    }

    [Fact]
    public void StructuralValidation_RejectsContentAddedBeforeOrAfterMarkers()
    {
        var layout = MarkedFile(out var before);
        Assert.True(layout);

        Cs2AutoexecIntegration.TryGetManagedLayout(before, out var parsed);

        var injectedBefore = "// injected" + Nl + parsed.Prefix + parsed.Managed + parsed.Suffix;
        var injectedAfter = parsed.Prefix + parsed.Managed + parsed.Suffix + "// injected" + Nl;

        Assert.False(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, injectedBefore).IsValid);
        Assert.False(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, injectedAfter).IsValid);
    }

    [Fact]
    public async Task Apply_KeepsUserContentBeforeAndAfterMarkers()
    {
        var before = "// user prefix line" + Nl + "volume 0.4" + Nl +
                     Cs2AutoexecIntegration.BeginMarker + Nl + "exec old.cfg" + Nl +
                     Cs2AutoexecIntegration.EndMarker + Nl +
                     "// user suffix line" + Nl + "bind \"g\" \"drop\"" + Nl;

        await using var h = await Harness.CreateAsync(
            automaticBackup: true, autoexec: before, managed: null);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" }, "preserve-structure");

        Assert.True(result.Success, result.Message);
        Assert.True(result.Recovery.SnapshotCreated);
        Assert.True(result.Recovery.CleanedUp);
        Assert.False(result.Recovery.RestoreAttempted);

        var after = await File.ReadAllTextAsync(h.AutoexecPath);
        Assert.True(Cs2AutoexecIntegration.ValidateUserContentPreserved(before, after).IsValid);
        Assert.Contains("// user prefix line", after);
        Assert.Contains("volume 0.4", after);
        Assert.Contains("// user suffix line", after);
        Assert.Contains("bind \"g\" \"drop\"", after);
        Assert.Contains(Cs2AutoexecIntegration.ExecCommand, after);
        Assert.DoesNotContain("old.cfg", after);
    }

    // -------------------------------------------------------------------------------------
    // 4. AtomicFile behaviour
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task AtomicFile_ReportsAtomicReplaceStrategy()
    {
        var root = NewRoot();
        try
        {
            var destination = Path.Combine(root, "data.txt");
            await File.WriteAllTextAsync(destination, "original" + Nl);

            var source = Path.Combine(root, "data.txt.new");
            await File.WriteAllTextAsync(source, "replacement" + Nl);

            var result = AtomicFile.Replace(source, destination);

            // Either the platform gave us a real atomic replace, or it honestly reported the
            // guarded fallback — but never a false "atomic" claim.
            Assert.True(result.Verified);
            Assert.True(result.UsedAtomicReplace || result.UsedFallbackReplace);
            Assert.Equal("replacement" + Nl, await File.ReadAllTextAsync(destination));
            Assert.False(File.Exists(source));

            // No temporary or recovery leftovers.
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            Assert.Empty(Directory.GetFiles(root, "*.ffrecover-*"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task AtomicFile_ForcedFallback_UsesRecoveryCopy_AndCleansUp()
    {
        var root = NewRoot();
        try
        {
            var destination = Path.Combine(root, "autoexec.cfg");
            await File.WriteAllTextAsync(destination, "original user content" + Nl);

            var source = Path.Combine(root, "autoexec.cfg.tmp-source");
            await File.WriteAllTextAsync(source, "new content" + Nl);

            var result = AtomicFile.Replace(source, destination, allowAtomicReplace: false);

            Assert.Equal(FileReplaceStrategy.FallbackReplace, result.Strategy);
            Assert.True(result.UsedFallbackReplace);
            Assert.False(result.UsedAtomicReplace);
            Assert.True(result.UsedRecoveryCopy);
            Assert.True(result.Verified);
            Assert.False(result.RestoredOriginalAfterFailure);
            Assert.Equal("new content" + Nl, await File.ReadAllTextAsync(destination));

            // The recovery copy was removed after a verified success.
            Assert.Empty(Directory.GetFiles(root, "*.ffrecover-*"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task AtomicFile_FallbackFailure_LeavesDestinationIntact()
    {
        var root = NewRoot();
        try
        {
            // A directory at the destination path makes every replacement primitive fail.
            var destination = Path.Combine(root, "autoexec.cfg");
            Directory.CreateDirectory(destination);

            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "new content" + Nl);

            var threw = false;
            try
            {
                AtomicFile.Replace(source, destination, allowAtomicReplace: false);
            }
            catch (IOException)
            {
                threw = true;
            }

            Assert.True(threw);
            Assert.True(Directory.Exists(destination), "destination must never be left deleted");
            Assert.Empty(Directory.GetFiles(root, "*.ffrecover-*"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Fact]
    public async Task AtomicFile_UnwritableDirectory_FailureKeepsOriginalFile()
    {
        if (OperatingSystem.IsWindows() || IsRunningAsRoot())
        {
            // Permission-based failure injection is not portable to Windows and is
            // ineffective for root.
            return;
        }

        var root = NewRoot();
        var directory = Path.Combine(root, "cfg");
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "autoexec.cfg");
            await File.WriteAllTextAsync(destination, "original user content" + Nl);
            var original = await File.ReadAllBytesAsync(destination);

            var source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "new content" + Nl);

            // Make the scope unwritable so no replacement primitive can succeed.
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var threw = false;
            try
            {
                AtomicFile.Replace(source, destination, allowAtomicReplace: false);
            }
            catch (IOException)
            {
                threw = true;
            }

            Assert.True(threw, "replacement into an unwritable directory must fail loudly");
            Assert.True(File.Exists(destination), "destination must never be left deleted");
            // Read first: byte[].SequenceEqual resolves to the ReadOnlySpan<byte> overload and a
            // span cannot be preserved across the await boundary.
            var bytesAfterFailure = await File.ReadAllBytesAsync(destination);
            Assert.True(original.SequenceEqual(bytesAfterFailure));

            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Empty(Directory.GetFiles(directory, "*.ffrecover-*"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch
            {
                // best effort
            }

            DeleteQuietly(root);
        }
    }

    // -------------------------------------------------------------------------------------
    // 5. Per-key change snapshots (Phase 10) — persisted on every *successful* apply
    //
    // Regression coverage for the per-key snapshot list that is built before the backup and
    // appended after a verified success: it must exist independently of the optional user
    // backup (the no-backup path is the one that reaches the append site), carry the previous
    // and new value plus the managed file, and never be written for an apply that failed and
    // was rolled back.
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_PersistsPerKeySnapshots_WhenNoBackupIsCreated()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: false,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" },
            reason: "snapshot-without-backup");

        Assert.True(result.Success, result.Message);
        Assert.Null(result.BackupId);
        Assert.True(result.Recovery.SnapshotCreated);
        Assert.True(result.Recovery.CleanedUp);

        // Per-key snapshots are written whether or not a user backup was requested.
        var snap = AssertSingle(await h.Snapshots.ListAsync());
        Assert.Equal("fps_max", snap.ConfigKey);
        Assert.Equal("400", snap.PreviousValue);
        Assert.Equal("0", snap.NewValue);
        Assert.Equal(h.ManagedPath, snap.File);
        Assert.Equal("snapshot-without-backup", snap.Reason);
        Assert.False(string.IsNullOrWhiteSpace(snap.Id));

        // Nothing links the snapshot to a backup, because no backup was created.
        Assert.Null(snap.BackupId);
        Assert.Empty(await h.Backups.ListBackupsAsync());
    }

    [Fact]
    public async Task Apply_PersistsPerKeySnapshots_AndStampsBackupId_WhenBackupIsCreated()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "144" },
            reason: "snapshot-with-backup");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.BackupId);

        var snap = AssertSingle(await h.Snapshots.ListAsync());
        Assert.Equal("fps_max", snap.ConfigKey);
        Assert.Equal("400", snap.PreviousValue);
        Assert.Equal("144", snap.NewValue);
        Assert.Equal(result.BackupId, snap.BackupId);

        // The same snapshots were handed to the backup that was created for this apply.
        var backups = await h.Backups.ListBackupsAsync();
        var backup = AssertSingle(backups);
        Assert.Equal(result.BackupId, backup.Id);
        Assert.Equal("fps_max", AssertSingle(backup.SettingChangeSnapshots).ConfigKey);
    }

    [Fact]
    public async Task Apply_PersistsOneSnapshotPerChangedSetting()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: false,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl + "m_rawinput 1" + Nl);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0", ["m_rawinput"] = "0" },
            reason: "multi-key");

        Assert.True(result.Success, result.Message);

        var snaps = await h.Snapshots.ListAsync();
        Assert.Equal(2, snaps.Count);
        Assert.Contains(snaps, s => s.ConfigKey == "fps_max" && s.PreviousValue == "400" && s.NewValue == "0");
        Assert.Contains(snaps, s => s.ConfigKey == "m_rawinput" && s.PreviousValue == "1" && s.NewValue == "0");
        Assert.All(snaps, s => Assert.Equal(h.ManagedPath, s.File));
        Assert.All(snaps, s => Assert.Equal("multi-key", s.Reason));
    }

    [Fact]
    public async Task Apply_FailedVerification_PersistsNoSnapshots()
    {
        await using var h = await Harness.CreateAsync(
            automaticBackup: true,
            autoexec: Cs2AutoexecIntegration.EnsureFrameForgeSection("// user" + Nl),
            managed: "fps_max 400" + Nl);

        // Corrupt the managed file silently after the write so verification must fail.
        h.Config.AfterWriteValues = path => File.WriteAllText(path, "fps_max 999" + Nl);

        var result = await h.Service.ApplySettingsAsync(
            new Dictionary<string, string> { ["fps_max"] = "0" },
            reason: "verify-failure-snapshot");

        Assert.False(result.Success);
        Assert.Contains("Verification failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Recovery.RestoreAttempted);
        Assert.True(result.Recovery.RestoreSucceeded);

        // A rolled-back apply must not leave per-key snapshots behind.
        Assert.Empty(await h.Snapshots.ListAsync());
    }

    // -------------------------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------------------------

    private const string ExecLine = "exec frameforge_settings.cfg";
    private static string Nl => "\n";

    private static string FixHint() => "will not modify autoexec.cfg";

    private static void AssertState(string content, AutoexecMarkerState expected, bool ambiguous)
    {
        var inspection = Cs2AutoexecIntegration.InspectMarkers(content);
        Assert.Equal(expected, inspection.State);
        Assert.Equal(ambiguous, inspection.IsAmbiguous);
        Assert.NotEmpty(inspection.Message);
    }

    private static bool MarkedFile(out string content)
    {
        content = "// user prefix" + Nl +
                  Cs2AutoexecIntegration.BeginMarker + Nl + ExecLine + Nl +
                  Cs2AutoexecIntegration.EndMarker + Nl +
                  "// user suffix" + Nl;
        return Cs2AutoexecIntegration.TryGetManagedLayout(content, out _);
    }

    private static int CountSettingLines(string text, string key) =>
        text.Split('\n')
            .Count(line => line.Trim().StartsWith(key + " ", StringComparison.OrdinalIgnoreCase));

    private static bool IsRunningAsRoot() =>
        string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Watchdog wrapper: the tests synchronise on signals, not on time. The timer only turns a
    /// deadlock into a failing test instead of a hung suite.
    /// </summary>
    private static async Task WaitAsync(Task task, string what)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(20));
        var finished = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        Assert.True(ReferenceEquals(finished, task), $"Timed out waiting for {what} — operations are not serializing correctly.");
        await task.ConfigureAwait(false);
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, string what)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(20));
        var finished = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        Assert.True(ReferenceEquals(finished, task), $"Timed out waiting for {what} — operations are not serializing correctly.");
        return await task.ConfigureAwait(false);
    }

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.Equal(1, items.Count);
        return items[0];
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff_hard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // best effort
        }
    }

    // -------------------------------------------------------------------------------------
    // test doubles
    // -------------------------------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string root, string cfgDir)
        {
            Root = root;
            CfgDir = cfgDir;
            ManagedPath = Path.Combine(cfgDir, Cs2SettingsService.ManagedFileName);
            AutoexecPath = Path.Combine(cfgDir, Cs2AutoexecIntegration.AutoexecFileName);

            var paths = new PathService(root);
            var log = new FileAppLog(paths, LogLevelSetting.Warning);
            AppSettings = new AppSettingsService(paths, log);
            Backups = new HookedBackupService(new BackupService(paths, log));
            Config = new HookedConfigService();
            Recovery = new CapturingRecoverySnapshots();
            Detection = new FakeDetection(cfgDir);
            Snapshots = new SettingChangeSnapshotStore(paths, log);

            Service = new Cs2SettingsService(
                new Cs2SettingCatalog(),
                Config,
                Detection,
                Backups,
                AppSettings,
                log,
                changeSnapshots: Snapshots,
                baselineSink: null,
                recoveryFactory: Recovery);
        }

        public string Root { get; }

        public string CfgDir { get; }

        public string ManagedPath { get; }

        public string AutoexecPath { get; }

        public FakeDetection Detection { get; }

        public HookedConfigService Config { get; }

        public CapturingRecoverySnapshots Recovery { get; }

        public HookedBackupService Backups { get; }

        public SettingChangeSnapshotStore Snapshots { get; }

        public AppSettingsService AppSettings { get; }

        public Cs2SettingsService Service { get; }

        public static async Task<Harness> CreateAsync(
            bool automaticBackup,
            string? autoexec,
            string? managed)
        {
            var root = NewRoot();
            var cfgDir = Path.Combine(root, "game", "csgo", "cfg");
            Directory.CreateDirectory(cfgDir);

            var harness = new Harness(root, cfgDir);
            if (autoexec is not null)
            {
                await File.WriteAllTextAsync(harness.AutoexecPath, autoexec).ConfigureAwait(false);
            }

            if (managed is not null)
            {
                await File.WriteAllTextAsync(harness.ManagedPath, managed).ConfigureAwait(false);
            }

            var settings = await harness.AppSettings.LoadAsync().ConfigureAwait(false);
            settings.AutomaticBackup = automaticBackup;
            await harness.AppSettings.SaveAsync(settings).ConfigureAwait(false);
            return harness;
        }

        public async Task<IDictionary<string, string>> ReadManagedAsync()
        {
            var doc = await Config.ReadAsync(ManagedPath).ConfigureAwait(false);
            return doc.ToDictionary();
        }

        public ValueTask DisposeAsync()
        {
            DeleteQuietly(Root);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDetection : ICs2DetectionService
    {
        public FakeDetection(string cfgDir)
        {
            Install = new Cs2InstallInfo
            {
                IsInstalled = true,
                InstallPath = Path.GetDirectoryName(cfgDir) ?? cfgDir,
                CfgDirectory = cfgDir,
                DetectionMessage = "hardening-test-install"
            };
        }

        public Cs2InstallInfo Install { get; set; }

        public Task<Cs2InstallInfo> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Install);
    }

    /// <summary>
    /// Wraps the real config service with deterministic hooks (block / throw / corrupt) and
    /// measures whether two operations ever touch the config files at the same time.
    /// </summary>
    private sealed class HookedConfigService : ICs2ConfigService
    {
        private readonly Cs2ConfigService _inner = new();
        private readonly object _sync = new();
        private int _current;
        private int _max;

        public Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task>? BeforeWriteValues { get; set; }

        public Action<string>? AfterWriteValues { get; set; }

        public Func<string, CancellationToken, Task>? BeforeRead { get; set; }

        public int MaxConcurrentOperations
        {
            get
            {
                lock (_sync)
                {
                    return _max;
                }
            }
        }

        public async Task<Cs2ConfigDocument> ReadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                if (BeforeRead is not null)
                {
                    await BeforeRead(filePath, cancellationToken).ConfigureAwait(false);
                }

                return await _inner.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Exit();
            }
        }

        public async Task WriteAsync(Cs2ConfigDocument document, CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                await _inner.WriteAsync(document, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Exit();
            }
        }

        public async Task WriteValuesAsync(
            string filePath,
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                if (BeforeWriteValues is not null)
                {
                    await BeforeWriteValues(filePath, values, cancellationToken).ConfigureAwait(false);
                }

                await _inner.WriteValuesAsync(filePath, values, cancellationToken).ConfigureAwait(false);

                AfterWriteValues?.Invoke(filePath);
            }
            finally
            {
                Exit();
            }
        }

        public Cs2ConfigDocument Parse(string content, string? filePath = null) => _inner.Parse(content, filePath);

        public string Serialize(Cs2ConfigDocument document) => _inner.Serialize(document);

        private void Enter()
        {
            lock (_sync)
            {
                _current++;
                if (_current > _max)
                {
                    _max = _current;
                }
            }
        }

        private void Exit()
        {
            lock (_sync)
            {
                _current--;
            }
        }
    }

    private sealed class CapturingRecoverySnapshots : IRecoverySnapshotFactory
    {
        public List<RecoverySnapshot> Created { get; } = new();

        public Action<RecoverySnapshot>? OnCreated { get; set; }

        public RecoverySnapshot Create(IReadOnlyList<string> paths)
        {
            var snapshot = RecoverySnapshot.Create(paths);
            Created.Add(snapshot);
            OnCreated?.Invoke(snapshot);
            return snapshot;
        }
    }

    private sealed class HookedBackupService : IBackupService
    {
        private readonly BackupService _inner;

        public HookedBackupService(BackupService inner)
        {
            _inner = inner;
        }

        public Func<Task>? BeforeRestore { get; set; }

        public Task<BackupEntry> CreateBackupAsync(
            string description,
            IEnumerable<string> optimizationIds,
            IEnumerable<string> affectedFiles,
            IReadOnlyDictionary<string, string?> previousValues,
            string? profileId = null,
            IEnumerable<SettingChangeSnapshot>? settingChangeSnapshots = null,
            CancellationToken cancellationToken = default) =>
            _inner.CreateBackupAsync(
                description,
                optimizationIds,
                affectedFiles,
                previousValues,
                profileId,
                settingChangeSnapshots,
                cancellationToken);

        public Task<IReadOnlyList<BackupEntry>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
            _inner.ListBackupsAsync(cancellationToken);

        public Task<BackupEntry?> GetBackupAsync(string backupId, CancellationToken cancellationToken = default) =>
            _inner.GetBackupAsync(backupId, cancellationToken);

        public async Task<BackupRestoreResult> RestoreAsync(string backupId, CancellationToken cancellationToken = default)
        {
            if (BeforeRestore is not null)
            {
                await BeforeRestore().ConfigureAwait(false);
            }

            return await _inner.RestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
        }

        public Task DeleteAsync(string backupId, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(backupId, cancellationToken);
    }
}
