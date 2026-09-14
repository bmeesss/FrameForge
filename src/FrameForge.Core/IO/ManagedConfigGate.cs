using System.Collections.Concurrent;

namespace FrameForge.Core.IO;

/// <summary>
/// Lease returned by <see cref="ManagedConfigGate.AcquireAsync"/>.
/// Dispose (or <c>await using</c>) releases the gate exactly once.
/// </summary>
public sealed class ManagedConfigLease : IAsyncDisposable
{
    private readonly SemaphoreSlim? _gate;
    private int _released;

    public ManagedConfigLease(SemaphoreSlim? gate)
    {
        _gate = gate;
    }

    /// <summary>False when no scope was resolved (e.g. CS2 cfg directory unavailable).</summary>
    public bool IsActive => _gate is not null;

    public ValueTask DisposeAsync()
    {
        if (_gate is not null && Interlocked.Exchange(ref _released, 1) == 0)
        {
            _gate.Release();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One shared asynchronous gate per managed configuration scope (a CS2 cfg directory).
///
/// Every operation that reads or mutates the files of that scope takes the same gate:
///   - reads never observe a half-written managed cfg / autoexec
///   - writes never interleave
///
/// The gate is per-directory, not application-wide: two unrelated CS2 installs never
/// block each other. Waiting is cancellation aware and the gate is always released in a
/// finally-equivalent path (<c>await using</c> / DisposeAsync).
///
/// A gate is never reentrant: code holding a lease must call the *CoreAsync variants of
/// operations instead of the public entry points, otherwise it would deadlock on itself.
/// </summary>
public static class ManagedConfigGate
{
    private static readonly StringComparer ScopeComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(ScopeComparer);

    /// <summary>Number of scopes currently tracked (diagnostics/tests).</summary>
    public static int ScopeCount => Gates.Count;

    /// <summary>
    /// Acquires the gate for <paramref name="scopeDirectory"/>.
    /// Returns an inactive lease when no directory is supplied (nothing to protect).
    /// </summary>
    public static async Task<ManagedConfigLease> AcquireAsync(
        string? scopeDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeDirectory))
        {
            return new ManagedConfigLease(null);
        }

        var key = Normalize(scopeDirectory);
        var gate = Gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ManagedConfigLease(gate);
    }

    private static string Normalize(string directory)
    {
        try
        {
            return Path.GetFullPath(directory);
        }
        catch
        {
            return directory;
        }
    }
}
