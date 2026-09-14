using System.Text;

namespace FrameForge.CS2.Settings;

/// <summary>
/// Structural state of the FRAMEFORGE markers inside autoexec.cfg.
/// Only <see cref="None"/> and <see cref="Complete"/> can be modified safely.
/// Every other state is ambiguous and requires user intervention — FrameForge
/// never silently repairs or rewrites an ambiguous file.
/// </summary>
public enum AutoexecMarkerState
{
    /// <summary>No markers at all: the whole file is user content.</summary>
    None,

    /// <summary>Exactly one BEGIN marker followed by exactly one END marker.</summary>
    Complete,

    /// <summary>END marker(s) without a BEGIN marker.</summary>
    MissingBegin,

    /// <summary>BEGIN marker without an END marker.</summary>
    MissingEnd,

    /// <summary>END marker appears before the BEGIN marker.</summary>
    OutOfOrder,

    /// <summary>More than one BEGIN marker (not nested in an END).</summary>
    DuplicateBegin,

    /// <summary>More than one END marker (with a single BEGIN).</summary>
    DuplicateEnd,

    /// <summary>A BEGIN marker appears before the END of an already opened section.</summary>
    Nested
}

/// <summary>Result of inspecting the FRAMEFORGE markers of autoexec.cfg.</summary>
public sealed class AutoexecMarkerInspection
{
    public AutoexecMarkerState State { get; init; }

    public int BeginCount { get; init; }

    public int EndCount { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>What the user has to do when the state is ambiguous.</summary>
    public string? UserAction { get; init; }

    /// <summary>True for every state that must not be modified automatically.</summary>
    public bool IsAmbiguous =>
        State is not AutoexecMarkerState.None and not AutoexecMarkerState.Complete;

    /// <summary>True when the markers form a single, ordered managed section.</summary>
    public bool CanEditManagedSection => State == AutoexecMarkerState.Complete;
}

/// <summary>
/// Structural layout of an autoexec.cfg that contains a managed section:
/// user prefix, managed section, user suffix.
/// </summary>
public sealed class ManagedSectionLayout
{
    /// <summary>Everything before the BEGIN marker line — user owned, must not change.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>BEGIN marker line through END marker line (incl. its line break) — FrameForge owned.</summary>
    public string Managed { get; init; } = string.Empty;

    /// <summary>Everything after the END marker line — user owned, must not change.</summary>
    public string Suffix { get; init; } = string.Empty;
}

/// <summary>Outcome of a structural check on autoexec.cfg content.</summary>
public sealed class AutoexecStructuralCheck
{
    public bool IsValid { get; init; }

    public bool RequiresUserIntervention { get; init; }

    public string Reason { get; init; } = string.Empty;

    public static AutoexecStructuralCheck Valid() => new() { IsValid = true };

    public static AutoexecStructuralCheck Invalid(string reason, bool requiresUserIntervention = false) => new()
    {
        IsValid = false,
        Reason = reason,
        RequiresUserIntervention = requiresUserIntervention
    };
}

/// <summary>
/// Controlled autoexec.cfg integration for FrameForge.
/// Only content between FRAMEFORGE BEGIN/END markers is owned by FrameForge.
/// User content outside the markers is never removed or rewritten.
///
/// Marker handling is strict: a file with ambiguous markers (missing half, duplicates,
/// nesting, reversed order) is reported as such and left untouched instead of "repaired".
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
    public static string BuildManagedSection() => BuildManagedSection(Environment.NewLine);

    /// <summary>Managed section body using an explicit newline style.</summary>
    public static string BuildManagedSection(string newline) =>
        BeginMarker + newline +
        "// Managed by FrameForge — do not edit between these markers." + newline +
        "// Settings live in " + ManagedFileName + " so your other autoexec lines stay intact." + newline +
        ExecCommand + newline +
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
    /// Inspects the FRAMEFORGE markers of autoexec.cfg without modifying anything.
    /// </summary>
    public static AutoexecMarkerInspection InspectMarkers(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Build(AutoexecMarkerState.None, 0, 0);
        }

        var beginCount = CountOccurrences(content, BeginMarker);
        var endCount = CountOccurrences(content, EndMarker);

        if (beginCount == 0 && endCount == 0)
        {
            return Build(AutoexecMarkerState.None, beginCount, endCount);
        }

        var firstBegin = IndexOfMarker(content, BeginMarker);
        var firstEnd = IndexOfMarker(content, EndMarker);

        if (beginCount == 1 && endCount == 1)
        {
            return firstEnd > firstBegin
                ? Build(AutoexecMarkerState.Complete, beginCount, endCount)
                : Build(AutoexecMarkerState.OutOfOrder, beginCount, endCount);
        }

        if (beginCount == 1 && endCount == 0)
        {
            return Build(AutoexecMarkerState.MissingEnd, beginCount, endCount);
        }

        if (beginCount == 0)
        {
            return Build(AutoexecMarkerState.MissingBegin, beginCount, endCount);
        }

        // More than one marker: distinguish nested sections from plain duplicates.
        var secondBegin = IndexOfMarker(content, BeginMarker, firstBegin + BeginMarker.Length);
        if (secondBegin >= 0 && firstEnd >= 0 && secondBegin < firstEnd)
        {
            return Build(AutoexecMarkerState.Nested, beginCount, endCount);
        }

        return beginCount > 1
            ? Build(AutoexecMarkerState.DuplicateBegin, beginCount, endCount)
            : Build(AutoexecMarkerState.DuplicateEnd, beginCount, endCount);
    }

    /// <summary>
    /// Splits autoexec.cfg into prefix / managed section / suffix.
    /// Only succeeds for a <see cref="AutoexecMarkerState.Complete"/> file.
    /// </summary>
    public static bool TryGetManagedLayout(string? content, out ManagedSectionLayout layout)
    {
        layout = new ManagedSectionLayout();
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        if (InspectMarkers(content).State != AutoexecMarkerState.Complete)
        {
            return false;
        }

        if (!TryGetSectionBounds(content, out var start, out var endExclusive))
        {
            return false;
        }

        layout = new ManagedSectionLayout
        {
            Prefix = content[..start],
            Managed = content[start..endExclusive],
            Suffix = content[endExclusive..]
        };

        return true;
    }

    /// <summary>
    /// Ensures a single FRAMEFORGE section exists and execs the managed cfg.
    /// The user prefix (before BEGIN) and suffix (after END) are preserved verbatim;
    /// only the managed section between the markers is rewritten.
    /// </summary>
    public static string EnsureFrameForgeSection(string? existingContent)
    {
        var content = existingContent ?? string.Empty;
        var newline = DetectNewline(content);

        if (TryGetManagedLayout(content, out var layout))
        {
            var sb = new StringBuilder();
            sb.Append(layout.Prefix);
            if (layout.Prefix.Length > 0 && !EndsWithNewline(layout.Prefix))
            {
                sb.Append(newline);
            }

            sb.Append(BuildManagedSection(newline));
            sb.Append(newline);
            sb.Append(layout.Suffix);
            return sb.ToString();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return BuildManagedSection(newline) + newline;
        }

        var builder = new StringBuilder();
        builder.Append(content);
        if (!EndsWithNewline(content))
        {
            builder.Append(newline);
        }

        if (!EndsWithBlankLine(builder.ToString()))
        {
            builder.Append(newline);
        }

        builder.Append(BuildManagedSection(newline));
        builder.Append(newline);
        return builder.ToString();
    }

    /// <summary>
    /// Returns content with the FrameForge section removed (user content preserved).
    /// Ambiguous marker states are returned unchanged — never silently repaired.
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
    /// User-owned text outside FRAMEFORGE markers (for reporting).
    /// Ambiguous marker states return the content unchanged — the caller must inspect first.
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

    /// <summary>
    /// Structural preservation check: compares the user-owned prefix and suffix before and
    /// after a modification. Only the managed section between the markers may change.
    /// Replaces the old "do the previous lines still appear somewhere" heuristic, which
    /// passed for reordered, duplicated or relocated user content.
    /// </summary>
    public static AutoexecStructuralCheck ValidateUserContentPreserved(string? before, string? after)
    {
        var beforeContent = before ?? string.Empty;
        var afterContent = after ?? string.Empty;

        var beforeMarkers = InspectMarkers(beforeContent);
        if (beforeMarkers.IsAmbiguous)
        {
            return AutoexecStructuralCheck.Invalid(
                $"autoexec.cfg FRAMEFORGE markers are ambiguous and cannot be modified safely. {beforeMarkers.Message}",
                requiresUserIntervention: true);
        }

        var afterMarkers = InspectMarkers(afterContent);
        if (afterMarkers.State != AutoexecMarkerState.Complete)
        {
            return AutoexecStructuralCheck.Invalid(
                $"autoexec.cfg does not contain exactly one complete FRAMEFORGE section after the update. {afterMarkers.Message}",
                requiresUserIntervention: afterMarkers.IsAmbiguous);
        }

        if (!TryGetManagedLayout(afterContent, out var afterLayout))
        {
            return AutoexecStructuralCheck.Invalid(
                "autoexec.cfg managed section could not be parsed after the update.");
        }

        if (beforeMarkers.State == AutoexecMarkerState.None)
        {
            // A brand-new section was appended: everything before BEGIN is user content.
            if (!ContentEquivalent(beforeContent, afterLayout.Prefix))
            {
                return AutoexecStructuralCheck.Invalid(
                    "user content before the FRAMEFORGE BEGIN marker changed while appending the managed section.");
            }

            if (!string.IsNullOrWhiteSpace(afterLayout.Suffix))
            {
                return AutoexecStructuralCheck.Invalid(
                    "content was added after the FRAMEFORGE END marker while appending the managed section.");
            }

            return AutoexecStructuralCheck.Valid();
        }

        if (!TryGetManagedLayout(beforeContent, out var beforeLayout))
        {
            return AutoexecStructuralCheck.Invalid(
                "autoexec.cfg managed section could not be parsed before the update.");
        }

        if (!ContentEquivalent(beforeLayout.Prefix, afterLayout.Prefix))
        {
            return AutoexecStructuralCheck.Invalid(
                "user content before the FRAMEFORGE BEGIN marker changed.");
        }

        if (!ContentEquivalent(beforeLayout.Suffix, afterLayout.Suffix))
        {
            return AutoexecStructuralCheck.Invalid(
                "user content after the FRAMEFORGE END marker changed.");
        }

        return AutoexecStructuralCheck.Valid();
    }

    /// <summary>
    /// Byte/content equivalence for user-owned regions.
    /// Line-ending style and trailing whitespace padding introduced by marker insertion are
    /// tolerated; every other byte must be identical.
    /// </summary>
    private static bool ContentEquivalent(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var a = NormalizeNewlines(left).TrimEnd(' ', '\t', '\r', '\n');
        var b = NormalizeNewlines(right).TrimEnd(' ', '\t', '\r', '\n');
        return string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');

    public static bool TryGetSectionBounds(string? content, out int start, out int endExclusive)
    {
        start = 0;
        endExclusive = 0;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        if (InspectMarkers(content).State != AutoexecMarkerState.Complete)
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

    private static AutoexecMarkerInspection Build(AutoexecMarkerState state, int beginCount, int endCount)
    {
        var message = state switch
        {
            AutoexecMarkerState.None => "autoexec.cfg contains no FRAMEFORGE markers.",
            AutoexecMarkerState.Complete => "autoexec.cfg contains exactly one FRAMEFORGE BEGIN/END section.",
            AutoexecMarkerState.MissingBegin =>
                $"autoexec.cfg contains {endCount} FRAMEFORGE END marker(s) but no FRAMEFORGE BEGIN marker.",
            AutoexecMarkerState.MissingEnd =>
                "autoexec.cfg contains a FRAMEFORGE BEGIN marker but no FRAMEFORGE END marker.",
            AutoexecMarkerState.OutOfOrder =>
                "autoexec.cfg contains a FRAMEFORGE END marker before the FRAMEFORGE BEGIN marker.",
            AutoexecMarkerState.DuplicateBegin =>
                $"autoexec.cfg contains {beginCount} FRAMEFORGE BEGIN markers (exactly one is supported).",
            AutoexecMarkerState.DuplicateEnd =>
                $"autoexec.cfg contains {endCount} FRAMEFORGE END markers (exactly one is supported).",
            AutoexecMarkerState.Nested =>
                $"autoexec.cfg contains nested FRAMEFORGE markers ({beginCount} BEGIN, {endCount} END).",
            _ => "autoexec.cfg FRAMEFORGE markers are ambiguous."
        };

        return new AutoexecMarkerInspection
        {
            State = state,
            BeginCount = beginCount,
            EndCount = endCount,
            Message = message,
            UserAction = state is AutoexecMarkerState.None or AutoexecMarkerState.Complete
                ? null
                : "FrameForge will not modify autoexec.cfg until the markers are corrected by hand: keep exactly " +
                  "one `// FRAMEFORGE BEGIN` line followed by exactly one `// FRAMEFORGE END` line, or remove every " +
                  "FrameForge marker line."
        };
    }

    private static int IndexOfMarker(string content, string marker, int startIndex = 0) =>
        content.IndexOf(marker, startIndex, StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string content, string marker)
    {
        if (marker.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (index <= content.Length)
        {
            var found = content.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            count++;
            index = found + marker.Length;
        }

        return count;
    }

    private static string DetectNewline(string content)
    {
        var index = content.IndexOf('\n');
        if (index < 0)
        {
            return Environment.NewLine;
        }

        return index > 0 && content[index - 1] == '\r' ? "\r\n" : "\n";
    }

    private static bool EndsWithNewline(string value) =>
        value.Length > 0 && (value[^1] == '\n' || value[^1] == '\r');

    private static bool EndsWithBlankLine(string value) =>
        NormalizeNewlines(value).EndsWith("\n\n", StringComparison.Ordinal);

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
