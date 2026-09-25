using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotScope.Local.Connecting;

namespace CopilotScope.Local.Capturing;

/// <summary>
/// <c>copilotscope scan --report</c>: what history each assistant keeps on this machine, counted
/// and described by its shape, without a word of what is in it — the files, their kinds, and the
/// property names at the top of each record. For the assistants CopilotScope cannot read yet,
/// that shape is how a new layout is noticed, and a capture is what it takes to read it.
/// </summary>
internal sealed partial class HistoryReport(Machine machine, Say say)
{
    /// <summary>How many files are opened to learn the shape of a source's records.</summary>
    private const int Sampled = 20;

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$.\-]{0,79}$")]
    private static partial Regex IdentifierKey();

    public int Run()
    {
        var history = new LocalHistory(machine);
        foreach (var source in LocalHistory.Sources)
        {
            say.Head(LocalHistory.DisplayName(source));
            var files = history.Files(source);
            if (files.Count == 0)
            {
                say.Info($"nothing found in {history.Searched(source)}");
                continue;
            }

            var bytes = files.Sum(f => f.Length);
            var roles = string.Join(", ", files.GroupBy(f => f.Role).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} ×{g.Count()}"));
            say.Ok($"{files.Count} file(s), {bytes / 1024.0 / 1024.0:0.#} MB, newest {files[0].LastWrite:yyyy-MM-dd}: {roles}");

            var keys = TopLevelKeys(files.Take(Sampled));
            if (keys.Count > 0)
                say.Info($"record fields, in the newest {Math.Min(Sampled, files.Count)}: " +
                         string.Join(", ", keys.Take(24).Select(k => $"{k.Key} ×{k.Value}")) + (keys.Count > 24 ? ", …" : ""));

            if (source == "claude-code")
                say.Info("read by CopilotScope while it runs: `copilotscope scan` shows what was imported");
            else
            {
                say.Info("no reader yet. A redacted sample is what it takes to write one:");
                say.Info($"`copilotscope capture-fixture {source}`, then read it and share it if you are happy to");
            }
        }
        return 0;
    }

    /// <summary>The property names at the top of each record — of the document, or of each line —
    /// counted over the sampled files. Names only, and only names that read as identifiers.</summary>
    private static List<KeyValuePair<string, int>> TopLevelKeys(IEnumerable<HistoryFile> files)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                var lines = file.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadLines(file.Path).Take(200)
                    : [File.ReadAllText(file.Path)];
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                        foreach (var property in document.RootElement.EnumerateObject())
                            if (IdentifierKey().IsMatch(property.Name))
                                counts[property.Name] = counts.GetValueOrDefault(property.Name) + 1;
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    }
}
