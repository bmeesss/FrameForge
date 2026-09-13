using System.Text;

namespace FrameForge.CS2.Settings;

/// <summary>
/// Controlled autoexec.cfg integration for FrameForge.
/// Only content between FRAMEFORGE BEGIN/END markers is owned by FrameForge.
/// User content outside the markers is never removed or rewritten.
/// </summary>
public static class Cs2AutoexecIntegration
{
    public const string BeginMarker = "// FRAMEFORGE BEGIN";
    public const string EndMarker = "// FRAMEFORGE END";
    public const string ManagedFileName = "frameforge_settings.cfg";
    public const string AutoexecFileName = "autoexec.cfg";
    public const string ExecCommand = "exec frameforge_settings.cfg";

    /// <summary>
    /// Canonical managed section body (without outer blank lines).
    /// </summary>
    public static string BuildManagedSection() =>
        BeginMarker + Environment.NewLine +
        "// Managed by FrameForge — do not edit between these markers." + Environment.NewLine +
        "// Settings live in " + ManagedFileName + " so your other autoexec lines stay intact." + Environment.NewLine +
        ExecCommand + Environment.NewLine +
        EndMarker;

    public static bool HasFrameForgeSection(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        return IndexOfMarker(content, BeginMarker) >= 0 && IndexOfMarker(content, EndMarker) >= 0;
    }

    public static bool SectionExecutesManagedFile(string? content)
    {
        if (!TryGetSectionBounds(content, out var start, out var endExclusive))
        {
            // Also accept a bare exec outside markers (legacy / manual)
            return content is not null &&
                   content.Contains(ExecCommand, StringComparison.OrdinalIgnoreCase);
        }

        var section = content![start..endExclusive];
        return section.Contains(ExecCommand, StringComparison.OrdinalIgnoreCase) ||
               section.Contains("exec \"" + ManagedFileName + "\"", StringComparison.OrdinalIgnoreCase) ||
               section.Contains("exec " + ManagedFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ensures a single FRAMEFORGE section exists and execs the managed cfg.
    /// All user content outside markers is preserved byte-for-byte where possible
    /// (normalized only by line endings when rewriting the section).
    /// </summary>
    public static string EnsureFrameForgeSection(string? existingContent)
    {
        var content = existingContent ?? string.Empty;
        var section = BuildManagedSection();

        if (TryGetSectionBounds(content, out var start, out var endExclusive))
        {
            // Expand to full line starts so we replace whole marker lines cleanly
            var before = content[..start];
            var after = content[endExclusive..];
            // Drop a single trailing newline from before / leading from after to avoid triple blanks
            before = TrimTrailingNewlines(before, max: 2);
            after = TrimLeadingNewlines(after, max: 2);

            var sb = new StringBuilder();
            if (before.Length > 0)
            {
                sb.Append(before);
                if (!before.EndsWith('\n') && !before.EndsWith('\r'))
                {
                    sb.AppendLine();
                }
            }

            sb.AppendLine(section);
            if (after.Length > 0)
            {
                if (!sb.ToString().EndsWith(Environment.NewLine) && !sb.ToString().EndsWith("\n"))
                {
                    sb.AppendLine();
                }

                sb.Append(after.TrimStart('\r', '\n'));
                if (!after.EndsWith('\n') && !after.EndsWith('\r'))
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        // No section yet — append
        if (string.IsNullOrWhiteSpace(content))
        {
            return section + Environment.NewLine;
        }

        var result = content;
        if (!result.EndsWith('\n') && !result.EndsWith('\r'))
        {
            result += Environment.NewLine;
        }

        if (!result.EndsWith(Environment.NewLine + Environment.NewLine) &&
            !result.EndsWith("\n\n"))
        {
            result += Environment.NewLine;
        }

        return result + section + Environment.NewLine;
    }

    /// <summary>
    /// Returns content with the FrameForge section removed (user content preserved).
    /// </summary>
    public static string RemoveFrameForgeSection(string? existingContent)
    {
        if (string.IsNullOrEmpty(existingContent) ||
            !TryGetSectionBounds(existingContent, out var start, out var endExclusive))
        {
            return existingContent ?? string.Empty;
        }

        var before = TrimTrailingNewlines(existingContent[..start], max: 2);
        var after = TrimLeadingNewlines(existingContent[endExclusive..], max: 2);
        if (before.Length == 0)
        {
            return after;
        }

        if (after.Length == 0)
        {
            return before.EndsWith('\n') || before.EndsWith('\r') ? before : before + Environment.NewLine;
        }

        return before.TrimEnd('\r', '\n') + Environment.NewLine + Environment.NewLine + after.TrimStart('\r', '\n');
    }

    /// <summary>
    /// User-owned text outside FRAMEFORGE markers (for preservation checks).
    /// </summary>
    public static string GetUserOwnedContent(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        if (!TryGetSectionBounds(content, out var start, out var endExclusive))
        {
            return content;
        }

        return content[..start] + content[endExclusive..];
    }

    public static bool TryGetSectionBounds(string? content, out int start, out int endExclusive)
    {
        start = 0;
        endExclusive = 0;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var begin = IndexOfMarker(content, BeginMarker);
        if (begin < 0)
        {
            return false;
        }

        // Start at beginning of the line containing the marker
        start = begin;
        while (start > 0 && content[start - 1] is not ('\n' or '\r'))
        {
            start--;
        }

        var endMarker = IndexOfMarker(content, EndMarker, begin + BeginMarker.Length);
        if (endMarker < 0)
        {
            return false;
        }

        endExclusive = endMarker + EndMarker.Length;
        // Include trailing newline(s) after end marker
        if (endExclusive < content.Length && content[endExclusive] == '\r')
        {
            endExclusive++;
        }

        if (endExclusive < content.Length && content[endExclusive] == '\n')
        {
            endExclusive++;
        }

        return true;
    }

    private static int IndexOfMarker(string content, string marker, int startIndex = 0) =>
        content.IndexOf(marker, startIndex, StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingNewlines(string s, int max)
    {
        var i = s.Length;
        var removed = 0;
        while (i > 0 && removed < max && s[i - 1] is '\n' or '\r')
        {
            i--;
            removed++;
        }

        return s[..i];
    }

    private static string TrimLeadingNewlines(string s, int max)
    {
        var i = 0;
        var removed = 0;
        while (i < s.Length && removed < max && s[i] is '\n' or '\r')
        {
            i++;
            removed++;
        }

        return s[i..];
    }
}
