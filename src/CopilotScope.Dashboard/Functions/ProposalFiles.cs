using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotScope.Dashboard.Functions;

/// <summary>A draft the assistant fenced with a <c>file=</c> label, and where it may be saved.</summary>
public sealed record ProposalFile(string Path, string Content);

/// <summary>
/// The drafts in a report, taken out as files — deterministically, and only where a draft passes.
///
/// <para>The proposal function asks for each draft fenced as <c>```markdown file=skills/&lt;name&gt;/SKILL.md</c>
/// or <c>file=instructions/&lt;name&gt;.md</c>. Those are the only two shapes saved: a name is lower-case
/// letters, digits and hyphens, so no label can climb out of the run's <c>proposals/</c> directory or
/// name a file anyone's tools would load from there. A draft that carries a session id from the pack
/// is not saved — it is written to be committed and shared, and a session id would carry one session's
/// provenance into every repository it lands in (ADR-005, decision 7). Nothing here is installed:
/// the files sit in the run's directory until the person copies them somewhere.</para>
/// </summary>
public static partial class ProposalFiles
{
    public const string Directory = "proposals";

    [GeneratedRegex(@"^(?<fence>`{3,})[ \t]*[A-Za-z0-9_+-]*[ \t]+file=(?<path>\S+)[ \t]*$")]
    private static partial Regex Opening();

    [GeneratedRegex(@"^(skills/[a-z0-9][a-z0-9-]{0,63}/SKILL\.md|instructions/[a-z0-9][a-z0-9-]{0,63}\.md)$")]
    private static partial Regex Allowed();

    /// <summary>Drafts to save, and one line for each labelled draft that was not.</summary>
    public static (IReadOnlyList<ProposalFile> Files, IReadOnlyList<string> Refused) Extract(
        string report, IReadOnlySet<string> sessionIds)
    {
        var files = new List<ProposalFile>();
        var refused = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = report.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (Opening().Match(lines[i]) is not { Success: true } open) continue;

            var fence = open.Groups["fence"].Value;
            var path = open.Groups["path"].Value;
            var body = new List<string>();
            var closed = false;
            for (i++; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() == fence) { closed = true; break; }
                body.Add(lines[i]);
            }

            if (!closed) { refused.Add($"{path}: the fence is never closed."); continue; }
            if (!Allowed().IsMatch(path))
            {
                refused.Add($"{path}: not skills/<name>/SKILL.md or instructions/<name>.md.");
                continue;
            }
            if (!seen.Add(path)) { refused.Add($"{path}: drafted twice; the first is kept."); continue; }

            var content = string.Join('\n', body).Trim() + "\n";
            if (sessionIds.FirstOrDefault(id => content.Contains(id, StringComparison.Ordinal)) is { } leaked)
            {
                refused.Add($"{path}: names session {leaked}; a draft meant to be shared carries no session id.");
                continue;
            }
            files.Add(new($"{Directory}/{path}", content));
        }

        return (files, refused);
    }

    /// <summary>
    /// Every session id a pack names: its session rows, its exemplars and its patterns' evidence.
    /// Ids shorter than eight characters are left out — too short to find in prose without matching
    /// ordinary words, and no assistant's ids are that short.
    /// </summary>
    public static IReadOnlySet<string> SessionIds(string packJson)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(packJson);
            var root = doc.RootElement;
            foreach (var name in new[] { "sessions", "exemplars" })
                if (root.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array)
                    foreach (var row in rows.EnumerateArray())
                        if (row.TryGetProperty("id", out var id) && id.GetString() is { } text) ids.Add(text);
            if (root.TryGetProperty("patterns", out var patterns) && patterns.ValueKind == JsonValueKind.Array)
                foreach (var pattern in patterns.EnumerateArray())
                    if (pattern.TryGetProperty("evidenceSessionIds", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
                        foreach (var id in evidence.EnumerateArray())
                            if (id.GetString() is { } text) ids.Add(text);
        }
        catch (JsonException)
        {
            // A pack that does not parse names no sessions this check could find.
        }
        ids.RemoveWhere(id => id.Length < 8);
        return ids;
    }
}
