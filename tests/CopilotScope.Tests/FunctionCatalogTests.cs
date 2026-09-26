using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotScope.Collector.Domain;
using CopilotScope.Dashboard.Functions;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The functions a user runs on their own assistant (docs/FUNCTIONS.md): what they ask, what they
/// hand over, and what they are allowed to do. Most of what makes a run safe is decided here, as
/// files and flags, before any process exists — so it is pinned here.
/// </summary>
public class FunctionCatalogTests
{
    private const string PackMarkdown = "# CopilotScope review pack\n\n- Sessions: 30 · fingerprint `abc123`\n";
    private const string PackJson = """
        {"scope":{"tier":"sessions","fingerprint":"abc123"},
         "patterns":[{"id":"P1","evidenceSessionIds":["sess-0001-aaaa","sess-0002-bbbb"]}],
         "exemplars":[{"id":"sess-0003-cccc"}],
         "sessions":[{"id":"sess-0004-dddd"},{"id":"short"}]}
        """;

    private static WorkspaceInput Input(AssistantFunction function, string tier = "sessions") =>
        new(function, "20260926-120000-" + function.Id + "-ab12", 30, tier, PackMarkdown, PackJson);

    public static TheoryData<string> FunctionIds()
    {
        var data = new TheoryData<string>();
        foreach (var f in FunctionCatalog.All) data.Add(f.Id);
        return data;
    }

    // ------------------------------------------------------------------ the catalog

    [Fact]
    public void IdsAndAgentNamesAreSafeAsFileNamesAndUnique()
    {
        var name = new Regex("^[a-z][a-z0-9-]*$");
        Assert.Equal(FunctionCatalog.All.Count, FunctionCatalog.All.Select(f => f.Id).Distinct().Count());
        foreach (var f in FunctionCatalog.All)
        {
            Assert.Matches(name, f.Id);
            Assert.Equal(f.Agents.Count, f.Agents.Select(a => a.Name).Distinct().Count());
            foreach (var agent in f.Agents)
            {
                Assert.Matches(name, agent.Name);
                Assert.StartsWith("copilotscope-", agent.Name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MostFunctionsAreMultiAgentAndEachEndsWithTheVerifier()
    {
        // The one job ADR-005 reserves for a second agent: striking findings whose citations do not
        // show the pattern. A fan-out without it is more text, not more truth.
        var multi = FunctionCatalog.All.Where(f => f.MultiAgent).ToList();
        Assert.True(multi.Count >= 3);
        foreach (var f in multi)
        {
            Assert.Same(FunctionCatalog.Verifier, f.Agents[^1]);
            Assert.Contains("copilotscope-verifier", f.Lead, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(FunctionIds))]
    public void TheLeadNamesEveryAgentItCanDispatch(string id)
    {
        var f = FunctionCatalog.Find(id)!;
        foreach (var agent in f.Agents)
            Assert.Contains(agent.Name, f.Lead, StringComparison.Ordinal);
        if (f.Agents.Count > 2)
            Assert.Contains("in parallel", f.Lead, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRulesCarryTheProjectsRefusals()
    {
        // What CLAUDE.md says the project refuses, written for the reader that will generate the answer.
        var rules = FunctionCatalog.SharedRules;
        Assert.Contains("The pack counts; you narrate", rules, StringComparison.Ordinal);
        Assert.Contains("Sessions, never people", rules, StringComparison.Ordinal);
        Assert.Contains("Acceptance rate is not a target", rules, StringComparison.Ordinal);
        Assert.Contains("never \"causes\"", rules, StringComparison.Ordinal);
        Assert.Contains("Workflow friction is absent on purpose", rules, StringComparison.Ordinal);
        Assert.Contains("Certainty, not confidence", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void FindIsExact()
    {
        Assert.Same(FunctionCatalog.ReviewPanel, FunctionCatalog.Find("review-panel"));
        Assert.Null(FunctionCatalog.Find("Review-Panel"));
        Assert.Null(FunctionCatalog.Find(null));
    }

    // ------------------------------------------------------------------ the files

    [Theory]
    [MemberData(nameof(FunctionIds))]
    public void EveryRunCarriesTheTaskThePackAndTheSettings(string id)
    {
        var f = FunctionCatalog.Find(id)!;
        var files = FunctionWorkspace.Files(Input(f)).ToDictionary(x => x.Path, x => x.Content);

        Assert.Equal(PackMarkdown, files["pack.md"]);
        Assert.Equal(PackJson, files["pack.json"]);
        Assert.Contains(f.Lead.Trim(), files["TASK.md"], StringComparison.Ordinal);
        Assert.Contains(FunctionCatalog.SharedRules.Trim(), files["TASK.md"], StringComparison.Ordinal);
        Assert.True(files.ContainsKey($".github/prompts/copilotscope-{f.Id}.prompt.md"));
        Assert.True(files.ContainsKey("README.md"));
        Assert.True(files.ContainsKey(FunctionWorkspace.ClaudeSettingsFile));

        // Agents exist in both spellings exactly when there are agents.
        Assert.Equal(f.Agents.Count, files.Keys.Count(k => k.StartsWith(".github/agents/", StringComparison.Ordinal)));
        Assert.Equal(f.MultiAgent, files.ContainsKey(FunctionWorkspace.ClaudeAgentsFile));
        Assert.All(files.Keys, path => Assert.DoesNotContain('\\', path));
    }

    [Fact]
    public void AnAggregatePackIsSaidToCarryNoSessionIds()
    {
        var task = FunctionWorkspace.Task(Input(FunctionCatalog.Review, tier: "aggregate"));
        Assert.Contains("aggregate tier: it carries no session ids", task, StringComparison.Ordinal);
        Assert.DoesNotContain("aggregate tier", FunctionWorkspace.Task(Input(FunctionCatalog.Review)), StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeAgentsReadAndNothingElse()
    {
        using var doc = JsonDocument.Parse(FunctionWorkspace.ClaudeAgents(FunctionCatalog.ReviewPanel));
        var agents = doc.RootElement.EnumerateObject().ToList();
        Assert.Equal(FunctionCatalog.ReviewPanel.Agents.Select(a => a.Name), agents.Select(a => a.Name));
        foreach (var agent in agents)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Value.GetProperty("description").GetString()));
            Assert.Contains("Rules", agent.Value.GetProperty("prompt").GetString(), StringComparison.Ordinal);
            Assert.Equal(["Read", "Grep", "Glob"], agent.Value.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        }
    }

    [Fact]
    public void CopilotAgentProfilesAreReadOnly()
    {
        var agent = FunctionCatalog.ReviewPanel.Agents[0];
        var profile = FunctionWorkspace.CopilotAgent(agent);
        var lines = profile.Split('\n');

        Assert.Equal("---", lines[0]);
        Assert.Equal($"name: {agent.Name}", lines[1]);
        Assert.StartsWith("description: \"", lines[2], StringComparison.Ordinal);
        Assert.Equal("tools: [\"read\", \"search\"]", lines[3]);
        Assert.Equal("---", lines[4]);
        Assert.Contains(agent.Instructions.Trim(), profile, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeIsLaunchedWithItsTelemetryOffAndMarked()
    {
        using var doc = JsonDocument.Parse(FunctionWorkspace.ClaudeSettings());
        var env = doc.RootElement.GetProperty("env");
        Assert.Equal("0", env.GetProperty("CLAUDE_CODE_ENABLE_TELEMETRY").GetString());
        Assert.Equal("copilotscope.observer=true", env.GetProperty("OTEL_RESOURCE_ATTRIBUTES").GetString());
    }

    [Fact]
    public void TheMarkerIsTheOneTheCollectorDrops() =>
        // The dashboard takes no reference to the collector, so the spelling is held together here.
        Assert.Equal(ObserverRegistry.MarkerAttribute, FunctionWorkspace.ObserverMarker);

    // ------------------------------------------------------------------ the commands

    [Theory]
    [MemberData(nameof(FunctionIds))]
    public void ClaudeCodeRunsRestrictedAndLeavesNoTranscript(string id)
    {
        var f = FunctionCatalog.Find(id)!;
        var args = FunctionWorkspace.Command(FunctionAssistant.ClaudeCode, f, "11111111-2222-4333-8444-555555555555").Arguments;

        Assert.Equal("-p", args[0]);
        Assert.Equal(FunctionWorkspace.Prompt, args[1]);
        Assert.Equal("11111111-2222-4333-8444-555555555555", After(args, "--session-id"));
        foreach (var flag in new[] { "--restricted", "--strict-mcp-config", "--no-session-persistence", "--disable-slash-commands" })
            Assert.Contains(flag, args);
        Assert.Equal("none", After(args, "--permission-prompts"));
        Assert.Equal(FunctionWorkspace.ClaudeSettingsFile, After(args, "--settings"));
        Assert.Equal(f.MultiAgent ? "Read,Grep,Glob,Task" : "Read,Grep,Glob", After(args, "--tools"));
        Assert.Equal(f.MultiAgent, args.Contains("--agents"));

        // --bare skips OAuth, which would move the run off the subscription; --add-dir widens what it may read.
        foreach (var never in new[] { "--bare", "--add-dir", "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions" })
            Assert.DoesNotContain(never, args);
    }

    [Theory]
    [MemberData(nameof(FunctionIds))]
    public void CopilotCliSeesOnlyReadToolsAndFansOutOnlyWhenThereAreAgents(string id)
    {
        var f = FunctionCatalog.Find(id)!;
        var args = FunctionWorkspace.Command(FunctionAssistant.CopilotCli, f, "11111111-2222-4333-8444-555555555555").Arguments.ToList();

        Assert.Equal(FunctionWorkspace.Prompt, After(args, "-p"));
        Assert.Contains("-s", args);
        Assert.Equal("11111111-2222-4333-8444-555555555555", After(args, "--session-id"));

        var available = args.SkipWhile(a => a != "--available-tools").Skip(1).TakeWhile(a => !a.StartsWith('-')).ToList();
        string[] read = ["view", "glob", "grep"];
        Assert.Equal(f.MultiAgent ? [.. read, "task", "read_agent", "list_agents"] : read, available);

        var denied = args.Select((a, i) => (a, i)).Where(x => x.a == "--deny-tool").Select(x => args[x.i + 1]).ToList();
        Assert.Equal(["shell", "write", "url"], denied);
        foreach (var flag in new[] { "--disable-builtin-mcps", "--no-custom-instructions", "--no-ask-user", "--disallow-temp-dir" })
            Assert.Contains(flag, args);
        Assert.Equal(f.MultiAgent, args.Contains("--fleet"));

        foreach (var never in new[] { "--yolo", "--allow-all", "--allow-all-paths", "--allow-all-urls", "--autopilot" })
            Assert.DoesNotContain(never, args);
    }

    [Theory]
    [InlineData(FunctionAssistant.ClaudeCode)]
    [InlineData(FunctionAssistant.CopilotCli)]
    public void NoArgumentCarriesACharacterAShellOrCmdWouldInterpret(FunctionAssistant assistant)
    {
        // An npm-installed assistant on Windows is a .cmd shim, whose arguments cmd.exe parses again.
        foreach (var f in FunctionCatalog.All)
            foreach (var arg in FunctionWorkspace.Command(assistant, f, Guid.NewGuid().ToString()).Arguments)
                Assert.DoesNotMatch("[\"'&|<>^%!`$;()]", arg);
    }

    [Fact]
    public void AKitRunByHandIsMarkedAndCarriesNoSessionId()
    {
        var readme = FunctionWorkspace.Readme(Input(FunctionCatalog.ReviewPanel));
        Assert.DoesNotContain("--session-id", readme, StringComparison.Ordinal);
        Assert.Contains("OTEL_RESOURCE_ATTRIBUTES=copilotscope.observer=true copilot -p", readme, StringComparison.Ordinal);
        Assert.Contains("claude -p", readme, StringComparison.Ordinal);
        Assert.Contains("/copilotscope-review-panel", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisplayedCommandQuotesWhatNeedsQuoting()
    {
        var display = new LaunchCommand("/usr/bin/claude", ["-p", "two words", "it's", "--tools", "Read,Grep"]).Display;
        Assert.Equal("/usr/bin/claude -p 'two words' 'it'\\''s' --tools Read,Grep", display);
    }

    [Fact]
    public void TheReportSaysWhichPartsWereComputedAndWhichWritten()
    {
        var header = FunctionWorkspace.ReportHeader(FunctionCatalog.Review, FunctionAssistant.CopilotCli, "run-1",
            new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("Computed by CopilotScope", header, StringComparison.Ordinal);
        Assert.Contains("Written by GitHub Copilot CLI", header, StringComparison.Ordinal);
        Assert.Contains("not a score's confidence", header, StringComparison.Ordinal);
    }

    [Fact]
    public void AKitIsOneFolderOfTheSameFiles()
    {
        var files = FunctionWorkspace.Files(Input(FunctionCatalog.Proposals));
        var zip = FunctionKit.Zip("copilotscope-proposals-20260926-1200", files);

        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal(files.Select(f => "copilotscope-proposals-20260926-1200/" + f.Path), archive.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(archive.GetEntry("copilotscope-proposals-20260926-1200/pack.json")!.Open());
        Assert.Equal(PackJson, reader.ReadToEnd());
    }

    // ------------------------------------------------------------------ drafts

    [Fact]
    public void DraftsAreSavedOnlyInTheirTwoShapes()
    {
        var report = """
            # Propose instructions and skills

            ```markdown file=skills/retry-failing-tests/SKILL.md
            ---
            name: retry-failing-tests
            description: Use when a test run fails.
            ---
            1. Read the failing test first.
            ```

            ````markdown file=instructions/npm-test.md
            When `npm test` fails, read the test before editing.
            ```bash
            npm test -- --verbose
            ```
            ````

            ```markdown file=../../.bashrc
            rm -rf ~
            ```

            ```markdown file=skills/Bad_Name/SKILL.md
            x
            ```

            ```markdown file=skills/leaky/SKILL.md
            Seen in sess-0001-aaaa.
            ```

            ```markdown file=skills/retry-failing-tests/SKILL.md
            a second copy
            ```

            ```markdown file=skills/unclosed/SKILL.md
            never closed
            """;

        var (files, refused) = ProposalFiles.Extract(report, ProposalFiles.SessionIds(PackJson));

        Assert.Equal(["proposals/skills/retry-failing-tests/SKILL.md", "proposals/instructions/npm-test.md"], files.Select(f => f.Path));
        Assert.StartsWith("---\nname: retry-failing-tests", files[0].Content, StringComparison.Ordinal);
        // A longer fence keeps a shorter one inside it.
        Assert.Contains("```bash\nnpm test -- --verbose\n```", files[1].Content, StringComparison.Ordinal);

        Assert.Equal(5, refused.Count);
        Assert.Contains(refused, r => r.StartsWith("../../.bashrc:", StringComparison.Ordinal));
        Assert.Contains(refused, r => r.StartsWith("skills/Bad_Name/SKILL.md:", StringComparison.Ordinal));
        Assert.Contains(refused, r => r.Contains("names session sess-0001-aaaa", StringComparison.Ordinal));
        Assert.Contains(refused, r => r.Contains("drafted twice", StringComparison.Ordinal));
        Assert.Contains(refused, r => r.Contains("never closed", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePackNamesItsSessionsInThreePlaces()
    {
        var ids = ProposalFiles.SessionIds(PackJson);
        Assert.Equal(["sess-0001-aaaa", "sess-0002-bbbb", "sess-0003-cccc", "sess-0004-dddd"], ids.Order());
        Assert.Empty(ProposalFiles.SessionIds("not json"));
    }

    private static string? After(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
