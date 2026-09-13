using System.Collections.Concurrent;
using System.Text;
using FrameForge.Core.Abstractions;
using FrameForge.Core.Models;

namespace FrameForge.Infrastructure.Logging;

/// <summary>
/// Structured local file logger. Never logs secrets, tokens, or memory dumps.
/// </summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    private readonly IPathService _paths;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly object _writeLock = new();
    private LogLevelSetting _minimumLevel;
    private bool _disposed;

    public FileAppLog(IPathService paths, LogLevelSetting minimumLevel = LogLevelSetting.Information)
    {
        _paths = paths;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_paths.LogsDirectory);
    }

    public void SetMinimumLevel(LogLevelSetting level) => _minimumLevel = level;

    public void LogTrace(string message) => Write(LogLevelSetting.Trace, message);
    public void LogDebug(string message) => Write(LogLevelSetting.Debug, message);
    public void LogInformation(string message) => Write(LogLevelSetting.Information, message);
    public void LogWarning(string message) => Write(LogLevelSetting.Warning, message);

    public void LogError(string message, Exception? exception = null)
    {
        var full = exception is null ? message : $"{message} | {exception.GetType().Name}: {Sanitize(exception.Message)}";
        Write(LogLevelSetting.Error, full);
    }

    private void Write(LogLevelSetting level, string message)
    {
        if (level < _minimumLevel || _disposed)
        {
            return;
        }

        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {Sanitize(message)}";
        _queue.Enqueue(line);
        Flush();
    }

    private void Flush()
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        lock (_writeLock)
        {
            var file = Path.Combine(_paths.LogsDirectory, $"frameforge-{DateTime.UtcNow:yyyyMMdd}.log");
            var sb = new StringBuilder();
            while (_queue.TryDequeue(out var line))
            {
                sb.AppendLine(line);
            }

            if (sb.Length > 0)
            {
                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
        }
    }

    /// <summary>
    /// Redacts common sensitive patterns from log lines.
    /// </summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        // Strip obvious credential-like assignments
        var sanitized = message;
        string[] keys = ["password", "token", "secret", "apikey", "api_key", "authorization"];
        foreach (var key in keys)
        {
            // crude redaction: key=value / key: value
            sanitized = System.Text.RegularExpressions.Regex.Replace(
                sanitized,
                $@"(?i)({key}\s*[=:]\s*)([^\s,;]+)",
                "$1***");
        }

        return sanitized;
    }

    public void Dispose()
    {
        _disposed = true;
        Flush();
    }
}
