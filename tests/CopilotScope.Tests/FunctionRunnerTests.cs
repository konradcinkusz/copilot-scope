using System.Net;
using System.Text.Json;
using CopilotScope.Collector.Domain;
using CopilotScope.Dashboard.Functions;
using CopilotScope.Local;
using CopilotScope.Local.Connecting;
using CopilotScope.Local.Functions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// What a function run's assistant is started with: an environment that sends nothing anywhere, and
/// how its reply is read.
/// </summary>
public class ChildEnvironmentTests
{
    [Fact]
    public void TelemetryAndSessionInheritanceAreWithheldAndLoginIsKept()
    {
        var env = ChildEnvironment.For(new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/me",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4318",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-api-key=secret",
            ["COPILOT_OTEL_ENABLED"] = "true",
            ["COPILOT_OTEL_FILE_EXPORTER_PATH"] = "/tmp/otel.jsonl",
            ["COPILOT_ALLOW_ALL"] = "true",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_SESSION_ID"] = "the-session-this-was-started-from",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "token",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["COPILOT_GITHUB_TOKEN"] = "gh-token",
            ["ANTHROPIC_API_KEY"] = "key"
        });

        foreach (var withheld in new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS", "COPILOT_OTEL_FILE_EXPORTER_PATH",
                     "COPILOT_ALLOW_ALL", "CLAUDECODE", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_ENTRYPOINT" })
            Assert.False(env.ContainsKey(withheld), withheld);

        // How each assistant signs in is the user's business, and the run is meant to use it.
        foreach (var kept in new[] { "PATH", "HOME", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "COPILOT_GITHUB_TOKEN", "ANTHROPIC_API_KEY" })
            Assert.True(env.ContainsKey(kept), kept);

        Assert.Equal("0", env["CLAUDE_CODE_ENABLE_TELEMETRY"]);
        Assert.Equal("false", env["COPILOT_OTEL_ENABLED"]);
        Assert.Equal("true", env["OTEL_SDK_DISABLED"]);
        Assert.Equal("copilotscope.observer=true", env["OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public void ClaudeCodesJsonResultIsTheReport()
    {
        var outcome = RunOutput.Parse(FunctionAssistant.ClaudeCode, 0,
            """{"type":"result","subtype":"success","is_error":false,"result":"# Review my sessions\n\nAll good.","session_id":"x"}""", "");
        Assert.True(outcome.Succeeded);
        Assert.Equal("# Review my sessions\n\nAll good.", outcome.Report);
    }

    [Fact]
    public void ClaudeCodesErrorResultIsAFailure()
    {
        var outcome = RunOutput.Parse(FunctionAssistant.ClaudeCode, 1,
            """{"type":"result","subtype":"error_max_turns","is_error":true,"result":""}""", "rate limited");
        Assert.False(outcome.Succeeded);
        Assert.Contains("error_max_turns", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("rate limited", outcome.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FunctionAssistant.CopilotCli, 0, "# Report\n", "", true)]
    [InlineData(FunctionAssistant.CopilotCli, 0, "  \n", "", false)]
    [InlineData(FunctionAssistant.CopilotCli, 2, "partial", "not signed in", false)]
    [InlineData(FunctionAssistant.ClaudeCode, 0, "plain text from an older version", "", true)]
    [InlineData(FunctionAssistant.ClaudeCode, 1, "", "Invalid API key", false)]
    public void ExitCodeAndReplyDecideTheOutcome(FunctionAssistant assistant, int exit, string stdout, string stderr, bool ok)
    {
        var outcome = RunOutput.Parse(assistant, exit, stdout, stderr);
        Assert.Equal(ok, outcome.Succeeded);
        if (!ok && stderr.Length > 0) Assert.Contains(stderr, outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedSettingsThatForceTelemetryOnAreSaidOutLoud()
    {
        var directory = TempDirectory.Create();
        try
        {
            var forced = Path.Combine(directory, "forced.json");
            File.WriteAllText(forced, """{"env":{"CLAUDE_CODE_ENABLE_TELEMETRY":"1","OTEL_EXPORTER_OTLP_ENDPOINT":"https://otel.corp.example"}}""");
            var quiet = Path.Combine(directory, "quiet.json");
            File.WriteAllText(quiet, """{"permissions":{"deny":["WebFetch"]}}""");

            Assert.Contains("https://otel.corp.example", AssistantLocator.ManagedTelemetryWarning([forced]), StringComparison.Ordinal);
            Assert.Null(AssistantLocator.ManagedTelemetryWarning([quiet, Path.Combine(directory, "missing.json")]));
        }
        finally
        {
            TempDirectory.Delete(directory);
        }
    }

    [Fact]
    public void AnAssistantIsFoundOnThePathOrWhereItsInstallerPutsIt()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = TempDirectory.Create();
        try
        {
            var bin = Path.Combine(home, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "copilot"), "#!/bin/sh\n");
            var local = Path.Combine(home, ".claude", "local");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "claude"), "#!/bin/sh\n");

            var machine = new Machine(home, Os.Linux, PathVariable: bin);
            Assert.Equal(Path.Combine(bin, "copilot"), AssistantLocator.Find(machine, FunctionAssistant.CopilotCli));
            Assert.Equal(Path.Combine(local, "claude"), AssistantLocator.Find(machine, FunctionAssistant.ClaudeCode));
            Assert.Null(AssistantLocator.Find(new Machine(home, Os.Linux, PathVariable: "/nonexistent"), FunctionAssistant.CopilotCli));
        }
        finally
        {
            TempDirectory.Delete(home);
        }
    }
}

/// <summary>
/// A function run end to end, against a stand-in assistant: a shell script that records how it was
/// started and replies as the real one would. POSIX only — the stand-in is a shell script; the
/// Windows start path is the same <see cref="System.Diagnostics.Process"/> call.
/// </summary>
public sealed class FunctionRunnerTests : IAsyncLifetime
{
    private readonly string _home = TempDirectory.Create();
    private string Root => Path.Combine(_home, "runs");

    private const string PackJson = """
        {"scope":{"tier":"sessions","fingerprint":"f00d"},
         "patterns":[{"id":"P1","evidenceSessionIds":["sess-0001-aaaa"]}],"sessions":[]}
        """;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TempDirectory.Delete(_home);
        return Task.CompletedTask;
    }

    /// <summary>A stand-in assistant. It writes its arguments and environment beside the task it
    /// was pointed at, then runs <paramref name="body"/>.</summary>
    private string Assistant(string body)
    {
        var path = Path.Combine(_home, $"assistant-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\n" +
                                "if [ \"$1\" = \"--version\" ]; then echo 'stand-in 1.0.0'; exit 0; fi\n" +
                                "for a in \"$@\"; do printf '%s\\n' \"$a\"; done > args.txt\n" +
                                "env > env.txt\n" +
                                body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private FunctionRunner Runner(string assistant, ObserverRegistry? observers = null, TimeSpan? timeout = null) =>
        new(Root, observers ?? new ObserverRegistry(), new Machine(_home, Os.Linux), NullLogger.Instance,
            timeout, find: _ => assistant, managedSettings: () => []);

    private static FunctionRequest Request(AssistantFunction function, FunctionAssistant assistant) =>
        new(function, assistant, 30, "sessions", "# CopilotScope review pack\n", PackJson);

    private static async Task<FunctionRunView> Settled(FunctionRunner runner, string runId)
    {
        for (var i = 0; i < 200; i++)
        {
            if (runner.Runs().FirstOrDefault(r => r.Id == runId) is { State: not RunState.Running } run) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("The run did not settle.");
    }

    [Fact]
    public async Task ARunIsRegisteredBeforeItStartsAndWritesItsReportAndDrafts()
    {
        if (OperatingSystem.IsWindows()) return;

        var reply = Path.Combine(_home, "reply.json");
        await File.WriteAllTextAsync(reply, JsonSerializer.Serialize(new
        {
            type = "result",
            subtype = "success",
            is_error = false,
            result = "# Propose instructions and skills\n\nP1 recurs in sess-0001-aaaa.\n\n" +
                     "```markdown file=skills/read-before-retry/SKILL.md\n---\nname: read-before-retry\n---\n1. Read.\n```\n\n" +
                     "```markdown file=skills/leaky/SKILL.md\nFrom sess-0001-aaaa.\n```\n"
        }));
        var observers = new ObserverRegistry();
        await using var runner = Runner(Assistant($"cat '{reply}'"), observers);

        Environment.SetEnvironmentVariable("OTEL_COPILOTSCOPE_PROBE", "leak");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_COPILOTSCOPE_PROBE", "leak");
        try
        {
            var preview = await runner.PrepareAsync(Request(FunctionCatalog.Proposals, FunctionAssistant.ClaudeCode));

            // Nothing launched yet: the files exist so the consent screen can list them.
            Assert.Contains(preview.Files, f => f.Path == "pack.json" && f.Bytes > 0);
            Assert.Contains(preview.Files, f => f.Path == FunctionWorkspace.ClaudeAgentsFile);
            Assert.Contains("--session-id", preview.Command, StringComparison.Ordinal);
            Assert.Contains("Anthropic", preview.DataUse, StringComparison.Ordinal);
            Assert.Empty(runner.Runs());
            Assert.Empty(observers.Registered);

            runner.Start(preview.RunId);
            var run = await Settled(runner, preview.RunId);

            Assert.Equal(RunState.Succeeded, run.State);
            var directory = Path.Combine(Root, preview.RunId);
            var args = await File.ReadAllLinesAsync(Path.Combine(directory, "args.txt"));
            var sessionId = args[Array.IndexOf(args, "--session-id") + 1];
            Assert.True(observers.IsRegistered(sessionId));

            // Started in the run's directory, with its telemetry off and marked, and nothing inherited
            // that could send it anywhere.
            var env = await File.ReadAllTextAsync(Path.Combine(directory, "env.txt"));
            Assert.DoesNotContain("COPILOTSCOPE_PROBE", env, StringComparison.Ordinal);
            Assert.Contains("OTEL_RESOURCE_ATTRIBUTES=copilotscope.observer=true", env, StringComparison.Ordinal);
            Assert.Contains("CLAUDE_CODE_ENABLE_TELEMETRY=0", env, StringComparison.Ordinal);

            var report = runner.Report(preview.RunId)!;
            Assert.Contains("Computed by CopilotScope", report, StringComparison.Ordinal);
            Assert.Contains("P1 recurs in sess-0001-aaaa.", report, StringComparison.Ordinal);
            Assert.Contains("proposals/skills/read-before-retry/SKILL.md", run.Outputs);
            Assert.True(File.Exists(Path.Combine(directory, "proposals", "skills", "read-before-retry", "SKILL.md")));
            Assert.False(Directory.Exists(Path.Combine(directory, "proposals", "skills", "leaky")));
            Assert.Contains(run.Notes, n => n.Contains("names session sess-0001-aaaa", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_COPILOTSCOPE_PROBE", null);
            Environment.SetEnvironmentVariable("CLAUDE_CODE_COPILOTSCOPE_PROBE", null);
        }
    }

    [Fact]
    public async Task ARestartListsEarlierRunsAndKeepsThemOutOfTheScores()
    {
        if (OperatingSystem.IsWindows()) return;

        var assistant = Assistant("echo '# Report'");
        string runId;
        await using (var first = Runner(assistant))
        {
            var preview = await first.PrepareAsync(Request(FunctionCatalog.Review, FunctionAssistant.CopilotCli));
            first.Start(preview.RunId);
            runId = (await Settled(first, preview.RunId)).Id;
        }

        var observers = new ObserverRegistry();
        await using var second = Runner(assistant, observers);
        var run = Assert.Single(second.Runs());
        Assert.Equal(runId, run.Id);
        Assert.Equal(RunState.Succeeded, run.State);
        Assert.Single(observers.Registered);
    }

    [Fact]
    public async Task ADiscardedRunLeavesNothingBehind()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var runner = Runner(Assistant("echo never"));
        var preview = await runner.PrepareAsync(Request(FunctionCatalog.ReviewPanel, FunctionAssistant.CopilotCli));
        Assert.True(Directory.Exists(preview.Directory));

        runner.Cancel(preview.RunId);

        Assert.False(Directory.Exists(preview.Directory));
        Assert.Throws<InvalidOperationException>(() => runner.Start(preview.RunId));
    }

    [Fact]
    public async Task OneRunAtATimeAndAStoppedRunIsStopped()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var runner = Runner(Assistant("sleep 30"));
        var first = await runner.PrepareAsync(Request(FunctionCatalog.Review, FunctionAssistant.CopilotCli));
        var second = await runner.PrepareAsync(Request(FunctionCatalog.Change, FunctionAssistant.CopilotCli));
        runner.Start(first.RunId);

        var refused = Assert.Throws<InvalidOperationException>(() => runner.Start(second.RunId));
        Assert.Contains("Another function is running", refused.Message, StringComparison.Ordinal);

        runner.Cancel(first.RunId);
        var run = await Settled(runner, first.RunId);
        Assert.Equal(RunState.Cancelled, run.State);
        Assert.Null(runner.Report(first.RunId));
    }

    [Fact]
    public async Task ARunThatOverstaysIsStoppedAndSaysSo()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var runner = Runner(Assistant("sleep 30"), timeout: TimeSpan.FromMilliseconds(300));
        var preview = await runner.PrepareAsync(Request(FunctionCatalog.Review, FunctionAssistant.ClaudeCode));
        runner.Start(preview.RunId);

        var run = await Settled(runner, preview.RunId);
        Assert.Equal(RunState.Failed, run.State);
        Assert.StartsWith("Stopped after", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRunKeepsWhatTheAssistantSaid()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var runner = Runner(Assistant("echo 'Error: not signed in to GitHub' >&2; exit 1"));
        var preview = await runner.PrepareAsync(Request(FunctionCatalog.Review, FunctionAssistant.CopilotCli));
        runner.Start(preview.RunId);

        var run = await Settled(runner, preview.RunId);
        Assert.Equal(RunState.Failed, run.State);
        Assert.Contains("not signed in to GitHub", run.Error, StringComparison.Ordinal);
        Assert.Contains(FunctionRunner.LogFile, run.Outputs);
    }

    [Fact]
    public async Task AssistantsAreFoundAtOnceAndTheirVersionsArriveLater()
    {
        if (OperatingSystem.IsWindows()) return;

        // Asking a version starts a process — seconds for an npm-installed assistant — so it must not
        // hold up the page that asked which assistants there are.
        await using var runner = Runner(Assistant("echo x"));
        var learned = new TaskCompletionSource();
        runner.Changed += () => learned.TrySetResult();

        var first = runner.Assistants();
        Assert.All(first, a => Assert.True(a.Installed));
        Assert.All(first, a => Assert.Null(a.Version));

        await learned.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(runner.Assistants(), a => Assert.Equal("stand-in 1.0.0", a.Version));
    }

    [Fact]
    public async Task AnAssistantThatIsNotInstalledIsRefusedBeforeAnythingIsWritten()
    {
        await using var runner = new FunctionRunner(Root, new ObserverRegistry(), new Machine(_home, Os.Linux),
            NullLogger.Instance, find: _ => null, managedSettings: () => []);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.PrepareAsync(Request(FunctionCatalog.Review, FunctionAssistant.CopilotCli)));
        Assert.Contains("not found", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Root));
        Assert.All(runner.Assistants(), a => Assert.False(a.Installed));
    }
}

/// <summary>The Functions page in the native binary, over the real pair of applications.</summary>
public sealed class FunctionsHostTests : IAsyncLifetime
{
    private readonly string _home = TempDirectory.Create();
    private readonly string _webRoot = TempDirectory.Create();

    public Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_webRoot, "app.css"), "body{}");
        Directory.CreateDirectory(Path.Combine(_webRoot, "_framework"));
        File.WriteAllText(Path.Combine(_webRoot, "_framework", "blazor.web.js"), "// blazor");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        TempDirectory.Delete(_home);
        TempDirectory.Delete(_webRoot);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheRunnerIsTheHostsAndKnowsTheCollectorsObservers()
    {
        await using var host = await LocalHost.StartAsync(
            new LocalHostOptions(0, 0, _home, null, _webRoot, "token", RunsDirectory: Path.Combine(_home, "runs")));

        var runner = host.Dashboard.Services.GetService<IFunctionRunner>();
        Assert.NotNull(runner);
        Assert.Same(host.Runner, runner);

        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{host.DashboardUrl}/functions")).StatusCode);
    }

    [Fact]
    public async Task WithoutARunsDirectoryThePageOffersKitsAndTheKitIsServed()
    {
        await using var host = await LocalHost.StartAsync(new LocalHostOptions(0, 0, _home, null, _webRoot, "token"));
        Assert.Null(host.Runner);
        Assert.Null(host.Dashboard.Services.GetService<IFunctionRunner>());

        using var http = new HttpClient();
        var page = await http.GetStringAsync($"{host.DashboardUrl}/functions");
        Assert.Contains("Download kit", page, StringComparison.Ordinal);

        // The kit is fetched from the collector over HTTP, like every other read the dashboard makes.
        using var kit = await http.GetAsync($"{host.DashboardUrl}/functions/review-panel/kit.zip?days=30");
        Assert.Equal(HttpStatusCode.OK, kit.StatusCode);
        Assert.Equal("application/zip", kit.Content.Headers.ContentType?.MediaType);
        using var archive = new System.IO.Compression.ZipArchive(await kit.Content.ReadAsStreamAsync());
        Assert.Contains(archive.Entries, e => e.FullName.EndsWith("/pack.md", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, e => e.FullName.EndsWith("/.github/agents/copilotscope-verifier.agent.md", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{host.DashboardUrl}/functions/nope/kit.zip")).StatusCode);
    }
}
