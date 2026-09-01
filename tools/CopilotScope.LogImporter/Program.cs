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

var files = ImportCommand.Discover(options.Root).ToList();
if (files.Count == 0)
{
    Console.Error.WriteLine($"No Claude Code transcripts found under {options.Root}.");
    Console.Error.WriteLine("Point --root at the directory holding *.jsonl session files " +
                            "(default: ~/.claude/projects).");
    return 1;
}

Console.WriteLine($"Found {files.Count} transcript file(s) under {options.Root}.");
if (!options.IncludeContent)
    Console.WriteLine("Prompt and response text is NOT being imported. Pass --include-content to include it.");

// git remotes are resolved once per working directory: a year of sessions in one repo would
// otherwise shell out thousands of times for the same answer.
var remotes = new Dictionary<string, string?>(StringComparer.Ordinal);
var sessions = new List<PersistedSession>();
var skippedLines = 0;

foreach (var file in files)
{
    TranscriptSession? parsed;
    try
    {
        var cwd = ImportCommand.WorkingDirectoryOf(file);
        var repository = cwd is null ? null : ImportCommand.RepositoryFor(cwd, remotes);
        parsed = ClaudeCodeTranscript.Parse(File.ReadLines(file), repository, options.IncludeContent);
    }
    catch (IOException ex)
    {
        // A session Claude Code is writing to right now is the most interesting one there is;
        // failing the whole run over a locked file would be the wrong trade.
        Console.Error.WriteLine($"skipped {Path.GetFileName(file)}: {ex.Message}");
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
        string Root, string Collector, string? ApiKey, bool IncludeContent, bool DryRun,
        DateTimeOffset? Since, string? Error = null);

    public static Options? Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                copilotscope-import — score the Claude Code history you already have.

                  --root <dir>        transcripts to read (default: ~/.claude/projects)
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

        var root = Value(args, "--root")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
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

    /// <summary>Every transcript under the root. Claude Code nests one directory per project.</summary>
    public static IEnumerable<string> Discover(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            : [];

    /// <summary>
    /// The working directory a transcript was recorded in, read from its first line. Cheaper
    /// and more reliable than decoding it out of Claude Code's directory-name encoding, which
    /// is lossy — a path containing a dash cannot be recovered from it.
    /// </summary>
    public static string? WorkingDirectoryOf(string file)
    {
        // Scans until it finds one, rather than reading only the first line: a transcript
        // routinely opens with a `summary` line, which carries no cwd. Bounded, because a
        // transcript with no cwd in its first few lines has none at all.
        const int MaxLines = 50;
        var seen = 0;

        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++seen > MaxLines) break;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("cwd", out var cwd)
                    && cwd.ValueKind == JsonValueKind.String
                    && cwd.GetString() is { Length: > 0 } path)
                    return path;
            }
            catch (JsonException) { /* a half-written line proves nothing about the rest */ }
        }
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
