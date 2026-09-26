namespace CopilotScope.Dashboard.Functions;

/// <summary>An assistant as the runner found it on this machine.</summary>
/// <param name="Path">The executable that would be started; null when none was found.</param>
/// <param name="Version">What <c>--version</c> printed, first line; null when it could not be asked.</param>
public sealed record AssistantAvailability(FunctionAssistant Assistant, string? Path, string? Version)
{
    public bool Installed => Path is not null;
    public string Name => FunctionWorkspace.DisplayName(Assistant);
}

/// <summary>A file a run would hand the assistant, and its size, for the consent screen.</summary>
public sealed record PreparedFile(string Path, long Bytes);

/// <summary>
/// Everything a person agrees to when they press Run: which files, how large, the exact command,
/// and who receives what the assistant reads. Nothing has been launched when this exists.
/// </summary>
public sealed record RunPreview(
    string RunId,
    string FunctionId,
    FunctionAssistant Assistant,
    string Directory,
    IReadOnlyList<PreparedFile> Files,
    string Command,
    string DataUse,
    IReadOnlyList<string> Warnings);

public enum RunState { Prepared, Running, Succeeded, Failed, Cancelled }

/// <summary>A function run, as the Functions page lists it.</summary>
public sealed record FunctionRunView(
    string Id,
    string FunctionId,
    string Title,
    FunctionAssistant Assistant,
    RunState State,
    DateTimeOffset Created,
    DateTimeOffset? Finished,
    string Directory,
    string? Error,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Notes);

/// <summary>What the page hands the runner: the function and the pack it fetched over the API.</summary>
public sealed record FunctionRequest(AssistantFunction Function, FunctionAssistant Assistant, int Days, string Tier,
    string PackMarkdown, string PackJson);

/// <summary>
/// Runs functions on an assistant installed on this machine. Registered only by the native
/// <c>copilotscope</c> binary (ADR-005, decision 5): it is the one process that owns local state and
/// runs as the person whose assistant it starts. A Compose deployment registers none, and its
/// Functions page offers the kit instead (decision 8).
///
/// <para>The runner never reads the collector. The page fetches the pack over the HTTP API — the path
/// the privacy guard, the aggregation floor and the access audit sit on — and hands it here.</para>
/// </summary>
public interface IFunctionRunner
{
    /// <summary>The assistants this machine has, found once and cached. Versions may arrive later,
    /// through <see cref="Changed"/>.</summary>
    IReadOnlyList<AssistantAvailability> Assistants();

    /// <summary>Writes the run's files and returns what running it would send. Launches nothing.</summary>
    Task<RunPreview> PrepareAsync(FunctionRequest request, CancellationToken ct = default);

    /// <summary>Starts a prepared run: the person has seen its preview and said yes.</summary>
    void Start(string runId);

    /// <summary>Stops a running run, or discards a prepared one.</summary>
    void Cancel(string runId);

    IReadOnlyList<FunctionRunView> Runs();

    /// <summary>The report a finished run wrote, or null.</summary>
    string? Report(string runId);

    /// <summary>Raised whenever a run changes state or an assistant's version is learned, from
    /// whatever thread learned it.</summary>
    event Action? Changed;
}
