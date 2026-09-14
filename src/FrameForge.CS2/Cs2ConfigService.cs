using System.Text;
using System.Text.RegularExpressions;
using FrameForge.Core.Abstractions;
using FrameForge.Core.IO;
using FrameForge.Core.Models;

namespace FrameForge.CS2;

/// <summary>
/// Reads and writes documented CS2 configuration files (cfg).
/// Always creates a backup before overwrite when a file already exists.
/// Uses atomic writes where practical. Preserves comments and unknown lines.
/// Never silently deletes user configuration.
/// </summary>
public sealed class Cs2ConfigService : ICs2ConfigService
{
    // Matches: key value  |  key "value"  |  key value // comment
    private static readonly Regex KeyValueRegex = new(
        @"^\s*(?<key>[A-Za-z0-9_./]+)\s+(?<value>""[^""]*""|[^\s/][^\s]*)?(?:\s*//\s*(?<comment>.*))?$",
        RegexOptions.Compiled);

    private readonly IAppLog? _log;

    public Cs2ConfigService(IAppLog? log = null)
    {
        _log = log;
    }

    public async Task<Cs2ConfigDocument> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return new Cs2ConfigDocument { FilePath = filePath };
        }

        try
        {
            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return Parse(content, filePath);
        }
        catch (DecoderFallbackException)
        {
            // Fall back to Latin1 for unusual encodings rather than failing hard
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var content = Encoding.Latin1.GetString(bytes);
            _log?.LogWarning($"Config file used non-UTF8 encoding; loaded with fallback: {filePath}");
            return Parse(content, filePath);
        }
        catch (IOException ex)
        {
            _log?.LogError($"Failed to read config '{filePath}'.", ex);
            // Graceful: return empty document tagged with path rather than throw for normal UI flows
            return new Cs2ConfigDocument { FilePath = filePath };
        }
    }

    public Cs2ConfigDocument Parse(string content, string? filePath = null)
    {
        var document = new Cs2ConfigDocument { FilePath = filePath ?? string.Empty };
        if (string.IsNullOrEmpty(content))
        {
            return document;
        }

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                document.Entries.Add(new Cs2ConfigEntry
                {
                    IsCommentOnly = true,
                    RawLine = line
                });
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                document.Entries.Add(new Cs2ConfigEntry
                {
                    IsCommentOnly = true,
                    Comment = trimmed[2..].TrimStart(),
                    RawLine = line
                });
                continue;
            }

            var match = KeyValueRegex.Match(line);
            if (!match.Success)
            {
                // Preserve unknown / corrupted lines verbatim — never drop them.
                document.Entries.Add(new Cs2ConfigEntry
                {
                    IsCommentOnly = true,
                    RawLine = line
                });
                continue;
            }

            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Success ? Unquote(match.Groups["value"].Value) : string.Empty;
            var comment = match.Groups["comment"].Success ? match.Groups["comment"].Value : null;

            document.Entries.Add(new Cs2ConfigEntry
            {
                Key = key,
                Value = value,
                Comment = comment,
                RawLine = line
            });
        }

        return document;
    }

    public string Serialize(Cs2ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sb = new StringBuilder();

        foreach (var entry in document.Entries)
        {
            if (entry.IsCommentOnly)
            {
                if (!string.IsNullOrEmpty(entry.RawLine))
                {
                    sb.AppendLine(entry.RawLine.TrimEnd('\r', '\n'));
                }
                else if (!string.IsNullOrEmpty(entry.Comment))
                {
                    sb.Append("// ").AppendLine(entry.Comment);
                }
                else
                {
                    sb.AppendLine();
                }

                continue;
            }

            var value = NeedsQuotes(entry.Value) ? $"\"{EscapeQuotes(entry.Value)}\"" : entry.Value;
            if (!string.IsNullOrEmpty(entry.Comment))
            {
                sb.Append(entry.Key).Append(' ').Append(value).Append(" // ").AppendLine(entry.Comment);
            }
            else
            {
                sb.Append(entry.Key).Append(' ').AppendLine(value);
            }
        }

        return sb.ToString();
    }

    public async Task WriteAsync(Cs2ConfigDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.FilePath);

        var directory = Path.GetDirectoryName(document.FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Side-car backup next to the file before overwrite — never overwrite without backup.
        if (File.Exists(document.FilePath))
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var backupPath = $"{document.FilePath}.frameforge.bak.{stamp}";
            AtomicFile.Copy(document.FilePath, backupPath, overwrite: false);
            _log?.LogInformation($"Created config side-car backup: {backupPath}");
        }

        var content = Serialize(document);
        await AtomicFile.WriteAllTextAsync(
                document.FilePath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _log?.LogInformation($"Wrote CS2 config: {document.FilePath}");
    }

    public async Task WriteValuesAsync(
        string filePath,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(values);

        var document = await ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
        var remaining = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < document.Entries.Count; i++)
        {
            var entry = document.Entries[i];
            if (entry.IsCommentOnly || string.IsNullOrEmpty(entry.Key))
            {
                continue;
            }

            if (remaining.TryGetValue(entry.Key, out var newValue))
            {
                document.Entries[i] = new Cs2ConfigEntry
                {
                    Key = entry.Key,
                    Value = newValue,
                    Comment = entry.Comment,
                    RawLine = entry.RawLine
                };
                remaining.Remove(entry.Key);
            }
        }

        foreach (var (key, value) in remaining)
        {
            document.Entries.Add(new Cs2ConfigEntry
            {
                Key = key,
                Value = value
            });
        }

        await WriteAsync(document, cancellationToken).ConfigureAwait(false);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string EscapeQuotes(string value) =>
        value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool NeedsQuotes(string value) =>
        string.IsNullOrEmpty(value) ||
        value.Any(char.IsWhiteSpace) ||
        value.Contains('"');
}
