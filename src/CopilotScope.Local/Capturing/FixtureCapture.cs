using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotScope.Collector.Import;
using CopilotScope.Local.Connecting;

namespace CopilotScope.Local.Capturing;

/// <summary>
/// <c>copilotscope capture-fixture &lt;assistant&gt;</c>: a redacted sample of an assistant's
/// history files, written to a folder the person reads before they share it (ADR-004,
/// decision 5). It is how a reader for VS Code Copilot Chat or Copilot CLI gets built: from the
/// shape of a real file, never a guessed one. What leaves the machine, if anything does, is the
/// person's decision and their action; this writes to a local folder and sends nothing.
/// </summary>
internal sealed class FixtureCapture(Machine machine, Say say, Identity identity)
{
    /// <summary>Larger files are left out rather than read whole into memory.</summary>
    private const long MaxFileBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public int Run(string source, string outRoot, int limit, DateTimeOffset now, int? seed = null)
    {
        var history = new LocalHistory(machine);
        say.Head($"Capturing {LocalHistory.DisplayName(source)}");

        var files = SampleOf(source, history.Files(source), limit);
        if (files.Count == 0)
        {
            say.Bad($"no {LocalHistory.DisplayName(source)} history found in {history.Searched(source)}.");
            return 1;
        }

        // One offset per capture, between a month and a year: dates stop saying when the work
        // happened, and durations between them stay exact.
        var random = seed is { } s ? new Random(s) : new Random();
        var redactor = new FixtureRedactor(TimeSpan.FromDays(-random.Next(30, 366)) - TimeSpan.FromSeconds(random.Next(86_400)), seed);
        var written = new List<(string Name, string Content, HistoryFile From)>();
        var notes = new List<string>();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (file.Length > MaxFileBytes)
            {
                notes.Add($"a {file.Role} file of {file.Length / 1024 / 1024} MB was left out: larger than {MaxFileBytes / 1024 / 1024} MB");
                continue;
            }
            string text;
            try { text = File.ReadAllText(file.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"a {file.Role} file could not be read ({ex.GetType().Name})");
                continue;
            }

            var jsonl = file.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            var redacted = jsonl ? RedactLines(text, redactor) : RedactDocument(text, redactor);
            if (redacted is null)
            {
                notes.Add($"a {file.Role} file is not valid JSON and was left out");
                continue;
            }
            var number = counters[file.Role] = counters.GetValueOrDefault(file.Role) + 1;
            written.Add(($"{file.Role}-{number}{(jsonl ? ".jsonl" : ".json")}", redacted, file));
        }

        if (written.Count == 0)
        {
            say.Bad("nothing could be captured:");
            foreach (var note in notes) say.Info(note);
            return 1;
        }

        var manifest = Manifest(source, written, notes, redactor, now);
        var everything = string.Join("\n", written.Select(w => w.Content).Append(manifest));
        if (LeakScan.Find(everything, identity) is { Count: > 0 } leaks)
        {
            say.Bad($"Refused: after redaction the capture still contains {string.Join(", ", leaks)}. Nothing was written.");
            say.Info("The redaction missed something it should not have. Please report that, without the files.");
            return 1;
        }

        var directory = Path.Combine(outRoot, source, now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        foreach (var (name, content, _) in written) File.WriteAllText(Path.Combine(directory, name), content);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(directory, "README.md"), Readme(source));

        say.Ok($"{written.Count} redacted file(s) in {directory}");
        foreach (var note in notes) say.Info(note);
        say.Info("Every string outside a short list of structural fields is replaced, ids and paths are stand-ins,");
        say.Info("and dates are moved. Read the files before you share them: you decide what leaves this machine.");
        return 0;
    }

    /// <summary>The newest files, but whole sessions for Claude Code, whose subagents write
    /// files of their own.</summary>
    private static List<HistoryFile> SampleOf(string source, IReadOnlyList<HistoryFile> files, int limit)
    {
        if (source != "claude-code") return files.Take(limit).ToList();
        return files
            .GroupBy(f => ClaudeCodeFiles.SessionIdOf(f.Path) ?? f.Path, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(f => f.LastWrite))
            .Take(limit)
            .SelectMany(g => g.OrderBy(f => f.Role == "subagent"))
            .ToList();
    }

    private static string? RedactDocument(string text, FixtureRedactor redactor)
    {
        try { return redactor.Redact(JsonNode.Parse(text))?.ToJsonString(Indented) + "\n"; }
        catch (JsonException) { return null; }
    }

    /// <summary>Line by line; a line that is not JSON — the half-written last line of a live
    /// session — is replaced by a note of its length, not dropped, so line counts still agree.</summary>
    private static string RedactLines(string text, FixtureRedactor redactor)
    {
        var output = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            try { output.Append(redactor.Redact(JsonNode.Parse(trimmed))?.ToJsonString(Compact)); }
            catch (JsonException) { output.Append($"\"<not json:{trimmed.Length}>\""); }
            output.Append('\n');
        }
        return output.ToString();
    }

    private static string Manifest(string source, List<(string Name, string Content, HistoryFile From)> written,
        List<string> notes, FixtureRedactor redactor, DateTimeOffset now) =>
        new JsonObject
        {
            ["source"] = source,
            ["capturedWith"] = $"copilotscope {Commands.Version}",
            ["capturedOn"] = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["redaction"] = "structure kept; strings replaced except structural fields; ids, paths, urls remapped; times shifted",
            ["files"] = new JsonArray(written.Select(w => (JsonNode)new JsonObject
            {
                ["name"] = w.Name,
                ["role"] = w.From.Role,
                ["lines"] = w.Content.Count(c => c == '\n'),
                ["bytesBeforeRedaction"] = w.From.Length
            }).ToArray()),
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray())
        }.ToJsonString(Indented) + "\n";

    private static string Readme(string source) => $"""
        # {LocalHistory.DisplayName(source)} history — a redacted capture

        Written by `copilotscope capture-fixture {source}`, to build and test a reader for this
        assistant's history files (ADR-004, decision 5: no parser without a captured real file).

        What was kept: the JSON structure; property names that read as identifiers; numbers and
        booleans; and the values of structural fields (a record's type, a message's role, a
        model id, a version) when they are short plain tokens.

        What was changed, consistently across these files: ids became other ids of the same
        form; paths, URLs and e-mail addresses became numbered placeholders; every timestamp
        moved by one random offset, so durations between them are exact. Every other string
        became `<text:length>`.

        Before it was written, the result was searched for your home directory, user name,
        machine name, git name and e-mail, and for token and key patterns, and would have been
        refused on any match. Read it anyway before sharing it: you decide what leaves your
        machine.
        """;
}
