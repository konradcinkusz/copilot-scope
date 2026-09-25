namespace CopilotScope.Local.Connecting;

/// <summary>
/// A block of lines CopilotScope owns inside a shell start-up file, between two marker lines:
///
/// <code>
/// # >>> CopilotScope (copilot-cli) >>>
/// export …
/// # &lt;&lt;&lt; CopilotScope (copilot-cli) &lt;&lt;&lt;
/// </code>
///
/// The markers are the control script's (<c>rc_block</c> in <c>scripts/copilotscope</c>), so
/// either one replaces or removes a block the other wrote. Writing replaces the block rather
/// than adding a second; removing takes the blank line written before it too, so connecting and
/// disconnecting again and again leaves the file as it was.
/// </summary>
internal static class RcBlock
{
    public static string Begin(string marker) => $"# >>> CopilotScope ({marker}) >>>";

    public static string End(string marker) => $"# <<< CopilotScope ({marker}) <<<";

    public static bool Contains(string path, string marker) =>
        File.Exists(path) && File.ReadLines(path).Any(line => line == Begin(marker));

    /// <summary>The lines inside the block, or null when there is none.</summary>
    public static IReadOnlyList<string>? Read(string path, string marker)
    {
        if (!File.Exists(path)) return null;
        List<string>? lines = null;
        foreach (var line in File.ReadLines(path))
        {
            if (line == Begin(marker)) { lines = []; continue; }
            if (line == End(marker)) return lines;
            lines?.Add(line);
        }
        return null;
    }

    /// <summary>Replaces the block with the given lines, or removes it when there are none.
    /// False when the file already read that way and was left untouched.</summary>
    public static bool Write(string path, string marker, IReadOnlyList<string> content)
    {
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        var newLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Length == 0 ? [] : original.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        // Split leaves one empty entry after a final newline; it comes back when the file is joined.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var kept = new List<string>(lines.Count);
        var inside = false;
        foreach (var line in lines)
        {
            if (line == Begin(marker))
            {
                inside = true;
                if (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
                continue;
            }
            if (inside)
            {
                if (line == End(marker)) inside = false;
                continue;
            }
            kept.Add(line);
        }

        if (content.Count > 0)
        {
            if (kept.Count > 0) kept.Add("");
            kept.Add(Begin(marker));
            kept.AddRange(content.Where(line => line.Length > 0));
            kept.Add(End(marker));
        }

        var text = kept.Count == 0 ? "" : string.Join(newLine, kept) + newLine;
        if (text == original || (!File.Exists(path) && content.Count == 0)) return false;
        TextFiles.WriteAtomically(path, text);
        return true;
    }
}
