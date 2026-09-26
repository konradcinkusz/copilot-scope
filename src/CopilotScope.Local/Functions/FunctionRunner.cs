using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotScope.Collector.Domain;
using CopilotScope.Dashboard.Functions;
using CopilotScope.Local.Connecting;

namespace CopilotScope.Local.Functions;

/// <summary>A run as it is kept on disk, in <c>run.json</c> beside its files.</summary>
internal sealed class RunRecord
{
    public required string Id { get; init; }
    public required string FunctionId { get; init; }
    public required FunctionAssistant Assistant { get; init; }

    /// <summary>Chosen here and registered as an observer before the process starts.</summary>
    public required string SessionId { get; init; }
    public required string Program { get; init; }
    public required List<string> Arguments { get; init; }
    public required int Days { get; init; }
    public required string Tier { get; init; }
    public DateTimeOffset Created { get; init; }
    public RunState State { get; set; }
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public List<string> Outputs { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

/// <summary>
/// Runs functions on the assistants installed on this machine (docs/FUNCTIONS.md, ADR-005 decision 5).
///
/// <para>A run is a directory under <c>~/.copilotscope/runs</c>: the task, the pack and the agents,
/// written before anything starts so the consent screen can list them; then the assistant's report,
/// and any drafts it fenced for saving. The assistant is started directly — never through a shell —
/// with the environment <see cref="ChildEnvironment"/> leaves it, in that directory, with read-only
/// tools. One run at a time: a subscription's concurrency is the user's, and a second fan-out beside
/// the first would spend it twice.</para>
///
/// <para>Every run's session id is registered with the collector's <see cref="ObserverRegistry"/>
/// before its process exists, and every run found on disk at start is registered again, so no run —
/// this process's or an earlier one's — is ever scored as a session of the user's.</para>
/// </summary>
internal sealed class FunctionRunner : IFunctionRunner, IAsyncDisposable
{
    public const string RunFile = "run.json";
    public const string LogFile = "assistant.log";

    /// <summary>A reply longer than this is not a report anyone reads; the rest is drained and dropped.</summary>
    private const int MaxStdout = 4 * 1024 * 1024;
    private const int MaxStderr = 256 * 1024;
    private const int MaxReportRead = 2 * 1024 * 1024;

    /// <summary>How long the found assistants are trusted before they are looked for again.</summary>
    private static readonly TimeSpan AssistantsFor = TimeSpan.FromMinutes(5);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly ObserverRegistry _observers;
    private readonly Machine _machine;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<FunctionAssistant, string?> _find;
    private readonly Func<IEnumerable<string>> _managedSettings;

    private readonly object _gate = new();
    private readonly Dictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);
    private Active? _active;
    private bool _disposing;
    private (IReadOnlyList<AssistantAvailability> List, DateTimeOffset At)? _assistants;

    private sealed record Active(string RunId, Process Process, Task Watch);

    public event Action? Changed;

    /// <param name="root">Where runs are kept: <c>~/.copilotscope/runs</c>.</param>
    /// <param name="timeout">How long a run may take before it is stopped. Thirty minutes by default:
    /// a panel of five agents over a large pack takes a few; one that is still going after thirty is
    /// spending the user's quota on nothing.</param>
    /// <param name="find">Where each assistant is; tests hand in a stand-in.</param>
    /// <param name="managedSettings">Claude Code's managed settings files; tests hand in their own.</param>
    public FunctionRunner(string root, ObserverRegistry observers, Machine machine, ILogger logger,
        TimeSpan? timeout = null, Func<DateTimeOffset>? clock = null,
        Func<FunctionAssistant, string?>? find = null, Func<IEnumerable<string>>? managedSettings = null)
    {
        _root = Path.GetFullPath(root);
        _observers = observers;
        _machine = machine;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _find = find ?? (assistant => AssistantLocator.Find(machine, assistant));
        _managedSettings = managedSettings ?? (() => AssistantLocator.ClaudeManagedSettings(machine));
        Load();
    }

    // ---------------------------------------------------------------------- IFunctionRunner

    /// <summary>
    /// Which assistants are here, answered at once from the file system. Their versions take a
    /// process start each — seconds, for an npm-installed one — so they are asked in the background
    /// and arrive through <see cref="Changed"/>, rather than holding up the page that asked.
    /// </summary>
    public IReadOnlyList<AssistantAvailability> Assistants()
    {
        List<AssistantAvailability> found;
        lock (_gate)
        {
            if (_assistants is { } cached && _clock() - cached.At < AssistantsFor) return cached.List;
            found = Enum.GetValues<FunctionAssistant>()
                .Select(assistant => new AssistantAvailability(assistant, _find(assistant), null))
                .ToList();
            _assistants = (found, _clock());
        }

        if (found.Any(a => a.Installed))
            _ = Task.Run(() =>
            {
                var versioned = found
                    .Select(a => a.Path is { } path ? a with { Version = AssistantLocator.Version(path) } : a)
                    .ToList();
                lock (_gate)
                    if (_assistants is { } current && ReferenceEquals(current.List, found))
                        _assistants = (versioned, current.At);
                Raise();
            });
        return found;
    }

    public async Task<RunPreview> PrepareAsync(FunctionRequest request, CancellationToken ct = default)
    {
        var function = FunctionCatalog.Find(request.Function.Id)
                       ?? throw new InvalidOperationException($"There is no function '{request.Function.Id}'.");
        var name = FunctionWorkspace.DisplayName(request.Assistant);
        var program = _find(request.Assistant)
                      ?? throw new InvalidOperationException($"{name} was not found on this machine.");

        var now = _clock();
        var id = $"{now:yyyyMMdd-HHmmss}-{function.Id}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
        var directory = Path.Combine(_root, id);
        CreatePrivate(_root);
        CreatePrivate(directory);

        var files = FunctionWorkspace.Files(new WorkspaceInput(function, id, request.Days, request.Tier,
            request.PackMarkdown, request.PackJson));
        foreach (var file in files)
            await WriteAsync(directory, file.Path, file.Content, ct);

        var sessionId = Guid.NewGuid().ToString();
        var command = FunctionWorkspace.Command(request.Assistant, function, sessionId, program);

        var warnings = new List<string>();
        if (request.Tier != "sessions")
            warnings.Add("The collector served the aggregate tier, which carries no session ids: the report will cite " +
                         "patterns and pack sections only.");
        if (request.Assistant == FunctionAssistant.ClaudeCode
            && AssistantLocator.ManagedTelemetryWarning(_managedSettings()) is { } managed)
            warnings.Add(managed);

        var record = new RunRecord
        {
            Id = id,
            FunctionId = function.Id,
            Assistant = request.Assistant,
            SessionId = sessionId,
            Program = program,
            Arguments = [.. command.Arguments],
            Days = request.Days,
            Tier = request.Tier,
            Created = now,
            State = RunState.Prepared
        };
        lock (_gate)
        {
            Save(record);
            _runs[id] = record;
        }

        return new RunPreview(id, function.Id, request.Assistant, directory,
            files.Select(f => new PreparedFile(f.Path, Utf8.GetByteCount(f.Content))).ToList(),
            command.Display,
            $"{name} reads these files and sends what it reads to {FunctionWorkspace.Vendor(request.Assistant)} under " +
            "your subscription's terms, as it does in any session. CopilotScope itself sends nothing.",
            warnings);
    }

    public void Start(string runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run) || run.State != RunState.Prepared)
                throw new InvalidOperationException("That run is not waiting to start. Prepare it again.");
            if (_active is not null)
                throw new InvalidOperationException("Another function is running. Wait for it, or stop it first.");

            // Registered before the process exists, so not one signal of it can arrive first.
            _observers.Register(run.SessionId);

            var start = new ProcessStartInfo(run.Program)
            {
                WorkingDirectory = DirectoryOf(run.Id),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8
            };
            foreach (var argument in run.Arguments) start.ArgumentList.Add(argument);
            ChildEnvironment.Apply(start);

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException($"{run.Program} did not start.");
            }
            catch (Win32Exception ex)
            {
                run.State = RunState.Failed;
                run.Error = $"Could not start {run.Program}: {ex.Message}";
                run.Finished = _clock();
                Save(run);
                throw new InvalidOperationException(run.Error, ex);
            }

            // Nothing is typed into a run. An assistant that waits for stdin would wait forever.
            process.StandardInput.Close();
            run.State = RunState.Running;
            run.Started = _clock();
            Save(run);
            _active = new Active(run.Id, process, Task.Run(() => WatchAsync(run, process)));
            _logger.LogInformation("Function {Function} started on {Assistant} (run {Run}).",
                run.FunctionId, FunctionWorkspace.DisplayName(run.Assistant), run.Id);
        }
        Raise();
    }

    public void Cancel(string runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run)) return;
            switch (run.State)
            {
                case RunState.Prepared:
                    // Discarded before it ran: its files include a copy of the pack, and a copy nobody
                    // asked to keep is not kept.
                    _runs.Remove(runId);
                    TryDelete(DirectoryOf(runId));
                    break;
                case RunState.Running when _active?.RunId == runId:
                    run.State = RunState.Cancelled;
                    run.Notes.Add("Stopped from the dashboard.");
                    Kill(_active.Process);
                    break;
                default:
                    return;
            }
        }
        Raise();
    }

    public IReadOnlyList<FunctionRunView> Runs()
    {
        lock (_gate)
            return _runs.Values
                .Where(r => r.State != RunState.Prepared)
                .OrderByDescending(r => r.Created)
                .Select(View)
                .ToList();
    }

    public string? Report(string runId)
    {
        string path;
        lock (_gate)
        {
            if (!_runs.ContainsKey(runId)) return null;
            path = Path.Combine(DirectoryOf(runId), FunctionWorkspace.ReportFile);
        }
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path, Utf8);
            var buffer = new char[MaxReportRead];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read) + (reader.EndOfStream ? "" : "\n\n… (truncated here; the file holds the rest)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Active? active;
        lock (_gate)
        {
            _disposing = true;
            active = _active;
            if (active is not null) Kill(active.Process);
        }
        if (active is not null)
            await Task.WhenAny(active.Watch, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    // ---------------------------------------------------------------------- the run

    private async Task WatchAsync(RunRecord run, Process process)
    {
        var stdout = ReadCappedAsync(process.StandardOutput, MaxStdout);
        var stderr = ReadCappedAsync(process.StandardError, MaxStderr);

        var timedOut = false;
        using (var timeout = new CancellationTokenSource(_timeout))
        {
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                Kill(process);
                await process.WaitForExitAsync();
            }
        }

        var output = await stdout;
        var errors = await stderr;
        var exitCode = process.ExitCode;
        process.Dispose();

        lock (_gate)
        {
            var directory = DirectoryOf(run.Id);
            try
            {
                if (errors.Length > 0) File.WriteAllText(Path.Combine(directory, LogFile), errors, Utf8);

                if (run.State == RunState.Cancelled)
                {
                    // Stopped by the person; whatever it printed is not a report.
                }
                else if (_disposing)
                {
                    run.State = RunState.Failed;
                    run.Error = "CopilotScope stopped while this run was in progress.";
                }
                else if (timedOut)
                {
                    run.State = RunState.Failed;
                    run.Error = $"Stopped after {_timeout.TotalMinutes:0} minutes without finishing.";
                }
                else
                {
                    run.ExitCode = exitCode;
                    var outcome = RunOutput.Parse(run.Assistant, exitCode, output, errors);
                    if (outcome.Succeeded) Finish(run, directory, outcome.Report!);
                    else
                    {
                        run.State = RunState.Failed;
                        run.Error = outcome.Error;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.State = RunState.Failed;
                run.Error = $"The run finished, but its files could not be written: {ex.Message}";
            }

            if (errors.Length > 0 && run.State == RunState.Failed) run.Outputs.Add(LogFile);
            run.Finished = _clock();
            Save(run);
            _active = null;
        }
        _logger.LogInformation("Function run {Run} ended: {State}.", run.Id, run.State);
        Raise();
    }

    /// <summary>Writes the report under its provenance header, and the drafts it fenced for saving.</summary>
    private void Finish(RunRecord run, string directory, string report)
    {
        var function = FunctionCatalog.Find(run.FunctionId)!;
        File.WriteAllText(Path.Combine(directory, FunctionWorkspace.ReportFile),
            FunctionWorkspace.ReportHeader(function, run.Assistant, run.Id, _clock()) + report + "\n", Utf8);
        run.Outputs.Add(FunctionWorkspace.ReportFile);

        var packJson = Path.Combine(directory, FunctionWorkspace.PackJsonFile);
        var sessionIds = File.Exists(packJson)
            ? ProposalFiles.SessionIds(File.ReadAllText(packJson, Utf8))
            : new HashSet<string>();
        var (drafts, refused) = ProposalFiles.Extract(report, sessionIds);
        foreach (var draft in drafts)
        {
            WriteAsync(directory, draft.Path, draft.Content, CancellationToken.None).GetAwaiter().GetResult();
            run.Outputs.Add(draft.Path);
        }
        foreach (var reason in refused) run.Notes.Add($"Not saved: {reason}");
        if (drafts.Count > 0)
            run.Notes.Add($"{drafts.Count} draft(s) saved under {ProposalFiles.Directory}/. Nothing was installed.");

        run.State = RunState.Succeeded;
        run.Error = null;
    }

    /// <summary>Reads a stream to its end, keeping at most <paramref name="cap"/> characters: the
    /// rest is drained, so an assistant that prints too much still exits instead of blocking on a
    /// full pipe.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int cap)
    {
        var kept = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (kept.Length < cap) kept.Append(buffer, 0, Math.Min(read, cap - kept.Length));
        return kept.ToString();
    }

    // ---------------------------------------------------------------------- files

    /// <summary>
    /// Every run found on disk: listed again, and its session id registered again, so a run an
    /// earlier start launched stays out of the scores. A run this process never finished is marked
    /// as such rather than listed as running forever.
    /// </summary>
    private void Load()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            RunRecord? run;
            try
            {
                var file = Path.Combine(directory, RunFile);
                run = File.Exists(file) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file, Utf8), Json) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Skipped the function run in {Directory}: {Reason}", directory, ex.Message);
                continue;
            }
            if (run is null || run.Id != Path.GetFileName(directory)) continue;

            _observers.Register(run.SessionId);
            if (run.State is RunState.Running or RunState.Prepared)
            {
                if (run.State == RunState.Running) run.Error = "CopilotScope stopped while this run was in progress.";
                else run.Notes.Add("Prepared, never started.");
                run.State = run.State == RunState.Running ? RunState.Failed : RunState.Cancelled;
                run.Finished ??= _clock();
                Save(run);
            }
            _runs[run.Id] = run;
        }
    }

    private void Save(RunRecord run)
    {
        try
        {
            var path = Path.Combine(DirectoryOf(run.Id), RunFile);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(run, Json), Utf8);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not record function run {Run}: {Reason}", run.Id, ex.Message);
        }
    }

    /// <summary>Writes one of a run's files, refusing any path that would leave the run's directory.</summary>
    private static async Task WriteAsync(string directory, string relative, string content, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(directory, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refused to write {relative}: it is outside the run's directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, Utf8, ct);
    }

    private string DirectoryOf(string runId) => Path.Combine(_root, runId);

    private void TryDelete(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        try { Directory.Delete(full, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the discarded run in {Directory}: {Reason}", full, ex.Message);
        }
    }

    private void CreatePrivate(string directory)
    {
        if (_machine.Os == Os.Windows || OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private FunctionRunView View(RunRecord run) => new(
        run.Id, run.FunctionId, FunctionCatalog.Find(run.FunctionId)?.Title ?? run.FunctionId, run.Assistant, run.State,
        run.Created, run.Finished, DirectoryOf(run.Id), run.Error, [.. run.Outputs], [.. run.Notes]);

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogDebug(ex, "A function-run listener failed."); }
    }
}
