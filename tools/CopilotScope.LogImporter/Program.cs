using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Outcomes;
using CopilotScope.Collector.Persistence;
using CopilotScope.LogImporter;

// ---------------------------------------------------------------------------------------
// copilotscope-import — score the history you already have, with no OTel configuration.
//
// Claude Code writes a complete record of every session to ~/.claude/projects/**/ *.jsonl.
// Most developers never flip OTEL env vars, so for most people that file is the only record
// that exists. This reads it, reconstructs first-class sessions, and posts them to the
// collector's /api/import endpoint.
//
// Re-running is idempotent: sessions keep the assistant's own session id, and the collector
// replaces rather than merges. It is safe on a schedule or a file watcher.
// ---------------------------------------------------------------------------------------

var options = ImportCommand.Parse(args);
if (options is null) return 0;              // --help printed
if (options.Error is { } error)
{
    Console.Error.WriteLine($"error: {error}");
    return 2;
}

var files = options.Roots.SelectMany(ImportCommand.Discover).Distinct(StringComparer.Ordinal).ToList();
if (files.Count == 0)
{
    Console.Error.WriteLine($"No Claude Code transcripts found under {string.Join(" or ", options.Roots)}.");
    Console.Error.WriteLine("Point --root at the directory holding *.jsonl session files " +
                            "(default: ~/.claude/projects and ~/.config/claude/projects).");
    return 1;
}

var groups = ImportCommand.GroupBySession(files);
Console.WriteLine($"Found {files.Count} transcript file(s) under {string.Join(" and ", options.Roots)}, " +
                  $"holding {groups.Count} session(s).");
if (!options.IncludeContent)
    Console.WriteLine("Prompt and response text is NOT being imported. Pass --include-content to include it.");

// git remotes are resolved once per working directory: a year of sessions in one repo would
// otherwise shell out thousands of times for the same answer.
var remotes = new Dictionary<string, string?>(StringComparer.Ordinal);
var sessions = new List<PersistedSession>();
var skippedLines = 0;

foreach (var group in groups)
{
    TranscriptSession? parsed;
    try
    {
        var cwd = group.Files.Select(ImportCommand.WorkingDirectoryOf).FirstOrDefault(c => c is not null);
        var repository = cwd is null ? null : ImportCommand.RepositoryFor(cwd, remotes);
        parsed = ClaudeCodeTranscript.Parse(ImportCommand.LinesOf(group.Files), repository, options.IncludeContent);
    }
    catch (IOException ex)
    {
        // A session Claude Code is writing to right now is the most interesting one there is;
        // failing the whole run over a locked file would be the wrong trade.
        Console.Error.WriteLine($"skipped {group.SessionId}: {ex.Message}");
        continue;
    }

    if (parsed is null) continue;
    skippedLines += parsed.Skipped;

    if (options.Since is { } since && parsed.Session.LastSeen < since) continue;
    sessions.Add(PersistedSession.From(parsed.Session));
}

if (skippedLines > 0)
    Console.WriteLine($"{skippedLines} malformed line(s) skipped — usually the half-written " +
                      "last line of a session still in progress.");

if (sessions.Count == 0)
{
    Console.WriteLine("Nothing to import.");
    return 0;
}

Console.WriteLine($"Parsed {sessions.Count} session(s): " +
                  $"{sessions.Sum(s => s.ChatCalls)} model calls, " +
                  $"{sessions.Sum(s => s.InputTokens + s.OutputTokens):N0} tokens.");

if (options.DryRun)
{
    foreach (var s in sessions.OrderByDescending(s => s.LastSeen).Take(10))
        Console.WriteLine($"  {s.LastSeen.UtcDateTime:yyyy-MM-dd HH:mm}  {s.Id}  " +
                          $"{s.ChatCalls} calls, {s.Turns} turns, {s.InputTokens + s.OutputTokens:N0} tokens" +
                          (s.Repository is { } r ? $"  [{r}]" : ""));
    if (sessions.Count > 10) Console.WriteLine($"  … and {sessions.Count - 10} more.");
    Console.WriteLine("Dry run — nothing was sent. Drop --dry-run to import.");
    return 0;
}

using var http = new HttpClient { BaseAddress = new Uri(options.Collector), Timeout = TimeSpan.FromMinutes(2) };
if (!string.IsNullOrEmpty(options.ApiKey)) http.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);

// Batched so one POST cannot become a hundred-megabyte body on a heavy user's history.
const int BatchSize = 50;
int imported = 0, updated = 0, skipped = 0;
var rejected = new List<string>();

foreach (var batch in sessions.Chunk(BatchSize))
{
    HttpResponseMessage response;
    try { response = await http.PostAsJsonAsync("/api/import", new ImportRequest([.. batch])); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: could not reach the collector at {options.Collector} — {ex.Message}");
        return 1;
    }

    using (response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Console.Error.WriteLine("error: the collector rejected the credential. Import needs an " +
                                    "Admin-scoped key — pass --api-key.");
            return 1;
        }
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error: the collector returned {(int)response.StatusCode}: " +
                                    await response.Content.ReadAsStringAsync());
            return 1;
        }

        var result = await response.Content.ReadFromJsonAsync<ImportResult>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (result is null) continue;
        imported += result.Imported;
        updated += result.Updated;
        skipped += result.Skipped;
        rejected.AddRange(result.Rejected);
    }
}

Console.WriteLine($"Imported {imported} new, updated {updated}, skipped {skipped}.");
foreach (var reason in rejected.Take(10)) Console.WriteLine($"  skipped: {reason}");
if (rejected.Count > 10) Console.WriteLine($"  … and {rejected.Count - 10} more.");
Console.WriteLine($"Open the dashboard to see them scored. Imported sessions are badged " +
                  "\"imported\" and carry lower confidence — they have no latency or edit-decision " +
                  "signal, because the transcript does not record any.");
return 0;

/// <summary>
/// Command-line parsing, file discovery and repository resolution. Separated from the flow
/// above so the parts worth testing are reachable without running the process.
/// </summary>
public static class ImportCommand
{
    public sealed record Options(
        IReadOnlyList<string> Roots, string Collector, string? ApiKey, bool IncludeContent, bool DryRun,
        DateTimeOffset? Since, string? Error = null);

    /// <summary>One session's transcript files: the main one, and any a subagent wrote under
    /// the same session id.</summary>
    public sealed record SessionFiles(string SessionId, IReadOnlyList<string> Files);

    public static Options? Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                copilotscope-import — score the Claude Code history you already have.

                  --root <dir>        transcripts to read (default: $CLAUDE_CONFIG_DIR/projects,
                                      else ~/.claude/projects and ~/.config/claude/projects)
                  --collector <url>   collector base URL (default: http://localhost:4318)
                  --api-key <key>     Admin-scoped key, when the collector is gated
                  --since <date>      only sessions last active on or after this date
                  --include-content   also import prompt and response TEXT (off by default)
                  --dry-run           parse and summarize; send nothing
                  -h, --help          this text

                No OTel configuration is required. Re-running is safe: sessions keep Claude
                Code's own session id, so an import replaces rather than duplicates.
                """);
            return null;
        }

        IReadOnlyList<string> root = Value(args, "--root") is { } explicitRoot ? [explicitRoot] : DefaultRoots();
        var collector = Value(args, "--collector") ?? "http://localhost:4318";
        var apiKey = Value(args, "--api-key") ?? Environment.GetEnvironmentVariable("COPILOTSCOPE_API_KEY");
        var includeContent = args.Contains("--include-content");
        var dryRun = args.Contains("--dry-run");

        DateTimeOffset? since = null;
        if (Value(args, "--since") is { } sinceText)
        {
            if (!DateTimeOffset.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return new Options(root, collector, apiKey, includeContent, dryRun, null,
                    $"--since '{sinceText}' is not a date.");
            since = parsed;
        }

        if (!Uri.TryCreate(collector, UriKind.Absolute, out _))
            return new Options(root, collector, apiKey, includeContent, dryRun, since,
                $"--collector '{collector}' is not an absolute URL.");

        return new Options(root, collector, apiKey, includeContent, dryRun, since);
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Where Claude Code keeps its transcripts: <c>$CLAUDE_CONFIG_DIR/projects</c> when that is
    /// set, otherwise both <c>~/.claude/projects</c> and <c>~/.config/claude/projects</c> — Claude
    /// Code has used both locations, and a machine upgraded across the change has history in
    /// each. Reading one would silently leave the other half of someone's history out.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return [Path.Combine(configured, "projects")];

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [Path.Combine(home, ".claude", "projects"), Path.Combine(home, ".config", "claude", "projects")];
    }

    /// <summary>Every transcript under the root. Claude Code nests one directory per project.</summary>
    public static IEnumerable<string> Discover(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            : [];

    /// <summary>
    /// Groups transcript files by the session id written inside them, not by file name. A
    /// subagent's transcript carries its parent's session id; imported one file at a time, the
    /// two became two imports of the same session and whichever was sent last replaced the
    /// other — a session's whole main conversation lost to its subagent's side trip. Files with
    /// no session id in them hold nothing to import and are left out.
    /// </summary>
    public static IReadOnlyList<SessionFiles> GroupBySession(IEnumerable<string> files) =>
        files.Select(file => (File: file, SessionId: SessionIdOf(file)))
            .Where(f => f.SessionId is not null)
            .GroupBy(f => f.SessionId!, StringComparer.Ordinal)
            .Select(g => new SessionFiles(g.Key, g.Select(f => f.File).Order(StringComparer.Ordinal).ToList()))
            .ToList();

    /// <summary>
    /// A session's lines. One file is read as it is, lazily. Several are interleaved by each
    /// line's own timestamp, so a subagent's calls land in the turn that ran them rather than
    /// all in the last one. A line without a timestamp travels with the line before it in its
    /// own file; one before any timestamp at all goes ahead of everything, in file order.
    /// </summary>
    public static IEnumerable<string> LinesOf(IReadOnlyList<string> files)
    {
        if (files.Count == 1) return File.ReadLines(files[0]);

        var lines = new List<(string Line, DateTimeOffset? At, int Order)>();
        foreach (var file in files)
        {
            DateTimeOffset? last = null;
            foreach (var line in File.ReadLines(file))
            {
                last = TimestampOf(line) ?? last;
                lines.Add((line, last, lines.Count));
            }
        }
        // Stable, with nulls first: equal times keep the order they were read in, so a line
        // without a timestamp stays right behind the line whose time it took.
        return lines.OrderBy(l => l.At).ThenBy(l => l.Order).Select(l => l.Line).ToList();
    }

    /// <summary>A line's own top-level timestamp, or null when it has none that parses.</summary>
    private static DateTimeOffset? TimestampOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("timestamp", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The session id a transcript belongs to, from the first line that carries one.</summary>
    public static string? SessionIdOf(string file) => FirstString(file, "sessionId");

    /// <summary>
    /// The working directory a transcript was recorded in, read from its first line. Cheaper
    /// and more reliable than decoding it out of Claude Code's directory-name encoding, which
    /// is lossy — a path containing a dash cannot be recovered from it.
    /// </summary>
    public static string? WorkingDirectoryOf(string file) => FirstString(file, "cwd");

    private static string? FirstString(string file, string property)
    {
        // Scans until it finds one, rather than reading only the first line: a transcript
        // routinely opens with a `summary` line, which carries neither a cwd nor a session id.
        // Bounded, because a transcript without one in its first few lines has none at all.
        const int MaxLines = 50;
        var seen = 0;

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (++seen > MaxLines) break;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty(property, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } found)
                        return found;
                }
                catch (JsonException) { /* a half-written line proves nothing about the rest */ }
            }
        }
        catch (IOException) { /* locked or vanished: the caller reports what it could not read */ }
        return null;
    }

    /// <summary>
    /// The repository label for a working directory.
    ///
    /// This is the one thing the importer can do that the OTel path cannot help with: it runs
    /// on the developer's machine, where the repository is checked out, so it can read the
    /// actual git remote and normalize it exactly the way outcome linkage does. Without that
    /// an imported session would be labelled with a bare directory name and would form its own
    /// cohort next to the live sessions from the same repository — two rows for one project,
    /// which is worse than no label.
    /// </summary>
    public static string? RepositoryFor(string cwd, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(cwd, out var cached)) return cached;

        string? resolved = null;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "remote get-url origin")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (git is not null)
            {
                var url = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit(5000);
                if (git.ExitCode == 0 && url.Length > 0)
                    resolved = OutcomeLinker.NormalizeRepository(url);
            }
        }
        catch (Exception) { /* no git, not a repo, or a directory that no longer exists */ }

        // Falling back to the directory name would invent a second cohort for a repository the
        // collector already knows by its remote. A missing label is the honest answer.
        cache[cwd] = resolved;
        return resolved;
    }
}
