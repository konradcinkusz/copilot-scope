using System.Text.Json.Nodes;
using CopilotScope.Collector.Import;
using CopilotScope.Local.Capturing;
using CopilotScope.Local.Connecting;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The redaction behind <c>copilotscope capture-fixture</c>: what someone shares to get a reader
/// built for their assistant must show the shape of their history and nothing of its content.
/// </summary>
public sealed class FixtureRedactorTests
{
    private static readonly TimeSpan Shift = TimeSpan.FromDays(-100);

    private static FixtureRedactor Redactor() => new(Shift, seed: 7);

    private static JsonObject Redact(FixtureRedactor redactor, string json) =>
        redactor.Redact(JsonNode.Parse(json))!.AsObject();

    [Fact]
    public void TextIsReplacedByItsLengthAndStructureIsKept()
    {
        var result = Redact(Redactor(), """
            {"type":"user","message":{"role":"user","content":"please refactor the billing module"},
             "count":3,"flag":true,"none":null,"list":["a secret","another"]}
            """);

        Assert.Equal("user", (string?)result["type"]);
        Assert.Equal("user", (string?)result["message"]!["role"]);
        Assert.Equal("<text:34>", (string?)result["message"]!["content"]);
        Assert.Equal(3, (int?)result["count"]);
        Assert.Equal(true, (bool?)result["flag"]);
        Assert.Null(result["none"]);
        Assert.Equal(["<text:8>", "<text:7>"], result["list"]!.AsArray().Select(n => (string?)n));
    }

    [Theory]
    [InlineData("type", "free text with spaces")]
    [InlineData("model", "/home/jane/models/custom")]
    [InlineData("gitBranch", "feature/acme-merger")]
    public void AStructuralFieldIsKeptOnlyWhenItIsAPlainToken(string field, string value)
    {
        var result = Redact(Redactor(), new JsonObject { [field] = value }.ToJsonString());
        Assert.NotEqual(value, (string?)result[field]);
    }

    [Fact]
    public void IdsAreReplacedConsistentlyAcrossFilesAndKeepTheirForm()
    {
        var redactor = Redactor();
        var first = Redact(redactor, """{"sessionId":"11111111-2222-3333-4444-555555555555","id":"msg_01AbCdEf2345","phase":"in_progress"}""");
        var second = Redact(redactor, """{"parent":"11111111-2222-3333-4444-555555555555","refs":{"msg_01AbCdEf2345":1}}""");

        var session = (string?)first["sessionId"];
        Assert.NotEqual("11111111-2222-3333-4444-555555555555", session);
        Assert.True(Guid.TryParse(session, out _));
        Assert.Equal(session, (string?)second["parent"]);
        Assert.StartsWith("msg_", (string?)first["id"]);
        Assert.NotEqual("msg_01AbCdEf2345", (string?)first["id"]);
        Assert.NotNull(second["refs"]![(string)first["id"]!]); // the same id, used as a key
        Assert.Equal("<text:11>", (string?)first["phase"]); // a word, not an id to invent a stand-in for
    }

    [Fact]
    public void IdFieldsStayDistinctWhateverTheirShape()
    {
        // A parser deduplicates and pairs records by these; collapsing them into a length would
        // make three lines one.
        var redactor = Redactor();
        var ids = new[] { "u1", "a1", "u2", "u1" }
            .Select(id => (string?)Redact(redactor, new JsonObject { ["uuid"] = id }.ToJsonString())["uuid"]).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(ids[0], ids[3]);
        Assert.All(ids, id => Assert.StartsWith("<id:", id));

        // Named like ids, and not ids: what they say is the point of keeping them.
        var model = Redact(redactor, """{"modelId":"claude-sonnet-5","languageId":"typescript"}""");
        Assert.Equal(("claude-sonnet-5", "typescript"), ((string?)model["modelId"], (string?)model["languageId"]));
    }

    [Fact]
    public void TimesMoveTogetherSoDurationsStayExact()
    {
        var result = Redact(Redactor(), """
            {"timestamp":"2026-09-01T10:00:00.000Z","done":"2026-09-01T10:00:02.500Z","creationDate":1788256800000}
            """);

        var start = DateTimeOffset.Parse((string)result["timestamp"]!);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero) + Shift, start);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), DateTimeOffset.Parse((string)result["done"]!) - start);
        Assert.EndsWith(".000Z", (string)result["timestamp"]!);
        Assert.Equal(1788256800000 + (long)Shift.TotalMilliseconds, (long)result["creationDate"]!);
    }

    [Fact]
    public void PathsUrlsAndAddressesBecomeNumberedPlaceholders()
    {
        var result = Redact(Redactor(), """
            {"cwd":"/home/jane/acme","again":"/home/jane/acme","other":"C:\\Users\\jane\\x","url":"https://git.acme.internal/repo",
             "email":"jane@acme.com","/home/jane/acme/src/app.ts":{"x":1}}
            """);

        Assert.Equal("<path:1>", (string?)result["cwd"]);
        Assert.Equal("<path:1>", (string?)result["again"]);
        Assert.Equal("<path:2>", (string?)result["other"]);
        Assert.Equal("<url:1>", (string?)result["url"]);
        Assert.Equal("<email:1>", (string?)result["email"]);
        Assert.DoesNotContain(result, kv => kv.Key.Contains("jane"));
    }

    [Fact]
    public void ToolNamesAreKeptButNotTheServersOfMcpTools()
    {
        var redactor = Redactor();
        string? Name(string name) => (string?)Redact(redactor, new JsonObject { ["name"] = name }.ToJsonString())["name"];

        Assert.Equal("Bash", Name("Bash"));
        Assert.Equal("run_in_terminal", Name("run_in_terminal"));
        Assert.Equal("mcp__copilotscope__get_session", Name("mcp__copilotscope__get_session"));
        Assert.Equal("<mcp-tool:1>", Name("mcp__acme_internal_jira__create_issue"));
        Assert.Equal("<text:8>", Name("Jane Doe"));
    }
}

public sealed class LeakScanTests
{
    private static readonly Identity Jane = new("/home/jane", "janedoe", "janes-laptop", "jane@acme.com", "Claude");

    [Theory]
    [InlineData("{\"cwd\":\"/home/jane/acme\"}", "your home directory")]
    [InlineData("{\"who\":\"janedoe\"}", "your user name")]
    [InlineData("{\"host\":\"janes-laptop\"}", "this machine's name")]
    [InlineData("{\"by\":\"jane@acme.com\"}", "your git e-mail address")]
    [InlineData("{\"by\":\"Claude\"}", "your git name")]
    public void WhatWouldIdentifySomeoneIsFound(string text, string what) =>
        Assert.Contains(what, LeakScan.Find(text, Jane));

    [Fact]
    public void TokensAreFound()
    {
        Assert.Contains("a GitHub token", LeakScan.Find($"{{\"t\":\"{FakeSecret.Token("ghp_")}\"}}", Jane));
        Assert.Contains("an API key", LeakScan.Find($"{{\"t\":\"{FakeSecret.Token("sk-ant-api03-")}\"}}", Jane));
    }

    [Fact]
    public void ANameInsideAnIdentifierIsAModelNotAPerson()
    {
        // Someone whose git name is also a model's must still be able to capture.
        Assert.Empty(LeakScan.Find("{\"model\":\"claude-opus-5-5\",\"svc\":\"service.name=claude-code\"}", Jane));
    }
}

/// <summary><c>capture-fixture</c> and <c>scan --report</c> against a home directory of their own.</summary>
public sealed class CaptureCommandTests : IDisposable
{
    private readonly string _home = TempDirectory.Create();
    private readonly string _out = TempDirectory.Create();
    private readonly StringWriter _output = new();
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        TempDirectory.Delete(_home);
        TempDirectory.Delete(_out);
    }

    private Machine Machine => new(_home, Os.Linux);

    private Identity Identity => new(_home, "janedoe", "janes-laptop", "jane@acme.com", "Jane Q Secret");

    private string Written(string source) =>
        Directory.GetDirectories(Path.Combine(_out, source)).Single();

    private string ClaudeTranscript()
    {
        var project = Directory.CreateDirectory(Path.Combine(_home, ".claude", "projects", "-home-jane-acme")).FullName;
        var path = Path.Combine(project, "aaaaaaaa-0000-0000-0000-000000000001.jsonl");
        string Line(string type, string uuid, string at, JsonObject message) => new JsonObject
        {
            ["type"] = type, ["sessionId"] = "aaaaaaaa-0000-0000-0000-000000000001", ["uuid"] = uuid,
            ["cwd"] = Path.Combine(_home, "acme"), ["gitBranch"] = "feature/acme-merger", ["timestamp"] = at,
            ["message"] = message
        }.ToJsonString();
        File.WriteAllLines(path, [
            Line("user", "u1", "2026-09-01T10:00:00.000Z", new JsonObject
            {
                ["role"] = "user", ["content"] = $"My name is Jane Q Secret; the key is {FakeSecret.Token("sk-ant-api03-")}"
            }),
            Line("assistant", "a1", "2026-09-01T10:00:05.000Z", new JsonObject
            {
                ["id"] = "msg_01Secret0001", ["model"] = "claude-sonnet-5",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_01Abc0001", ["name"] = "Bash",
                    ["input"] = new JsonObject { ["command"] = "cat ~/acme/.env" } }),
                ["usage"] = new JsonObject { ["input_tokens"] = 120, ["output_tokens"] = 30 }
            }),
            Line("user", "u2", "2026-09-01T10:00:09.000Z", new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "toolu_01Abc0001",
                    ["content"] = "API_KEY=" + FakeSecret.Token("ghp_") })
            })
        ]);
        return path;
    }

    [Fact]
    public void ACapturedClaudeCodeTranscriptKeepsItsShapeAndNothingElse()
    {
        var original = ClaudeTranscript();

        Assert.Equal(0, new FixtureCapture(Machine, new Say(_output), Identity).Run("claude-code", _out, 3, Now, seed: 1));

        var directory = Written("claude-code");
        var captured = File.ReadAllText(Path.Combine(directory, "transcript-1.jsonl"));
        foreach (var secret in new[] { "Jane", "sk-ant", "ghp_", "acme", _home, "Secret", "cat ~" })
            Assert.DoesNotContain(secret, captured);

        // The point of a fixture: it reads as the original did.
        var before = ClaudeCodeTranscript.Parse(File.ReadLines(original))!.Session;
        var after = ClaudeCodeTranscript.Parse(captured.Split('\n', StringSplitOptions.RemoveEmptyEntries))!.Session;
        Assert.Equal((before.ChatCalls, before.Turns, before.ToolCalls, before.InputTokens, before.OutputTokens),
            (after.ChatCalls, after.Turns, after.ToolCalls, after.InputTokens, after.OutputTokens));
        Assert.Equal(before.LastSeen - before.FirstSeen, after.LastSeen - after.FirstSeen);

        Assert.True(File.Exists(Path.Combine(directory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(directory, "README.md")));
    }

    [Fact]
    public void ACaptureThatStillIdentifiesSomeoneIsNotWritten()
    {
        ClaudeTranscript();
        // An identity whose "home" is a string the redaction keeps: a model id.
        var identity = new Identity("claude-sonnet-5", null, null, null, null);

        Assert.Equal(1, new FixtureCapture(Machine, new Say(_output), identity).Run("claude-code", _out, 3, Now, seed: 1));

        Assert.False(Directory.Exists(Path.Combine(_out, "claude-code")));
        Assert.Contains("your home directory", _output.ToString());
        Assert.DoesNotContain("claude-sonnet-5", _output.ToString()); // named by kind, not by value
    }

    [Fact]
    public void VsCodeChatSessionsAreFoundInEveryWorkspace()
    {
        var sessions = Directory.CreateDirectory(Path.Combine(_home, ".config", "Code", "User", "workspaceStorage", "9f8e7d", "chatSessions")).FullName;
        File.WriteAllText(Path.Combine(sessions, "s1.json"), """{"version":3,"requests":[{"message":{"text":"secret question"}}]}""");
        var empty = Directory.CreateDirectory(Path.Combine(_home, ".config", "Code", "User", "globalStorage", "emptyWindowChatSessions")).FullName;
        File.WriteAllText(Path.Combine(empty, "s2.json"), """{"version":3,"requests":[]}""");

        Assert.Equal(0, new FixtureCapture(Machine, new Say(_output), Identity).Run("vscode", _out, 3, Now, seed: 1));

        var names = Directory.GetFiles(Written("vscode")).Select(Path.GetFileName).ToList();
        Assert.Contains("chatSessions-1.json", names);
        Assert.Contains("emptyWindowChatSessions-1.json", names);
        Assert.DoesNotContain("secret question", File.ReadAllText(Path.Combine(Written("vscode"), "chatSessions-1.json")));
    }

    [Fact]
    public void CopilotCliSessionStateIsTakenButNeverItsConfiguration()
    {
        var copilot = Path.Combine(_home, ".copilot");
        Directory.CreateDirectory(Path.Combine(copilot, "session-state"));
        File.WriteAllText(Path.Combine(copilot, "session-state", "abc.jsonl"), "{\"type\":\"user.message\"}\n");
        File.WriteAllText(Path.Combine(copilot, "config.json"), $"{{\"token\":\"{FakeSecret.Token("gho_")}\"}}");
        File.WriteAllText(Path.Combine(copilot, "state.json"), "{\"lastUser\":\"janedoe\"}"); // top level: settings, not sessions

        var files = new LocalHistory(Machine).Files("copilot-cli");

        Assert.Equal(["session-state"], files.Select(f => f.Role));
    }

    [Fact]
    public void NothingToCaptureIsSaid()
    {
        Assert.Equal(1, new FixtureCapture(Machine, new Say(_output), Identity).Run("copilot-cli", _out, 3, Now));
        Assert.Contains("no GitHub Copilot CLI history found", _output.ToString());
    }

    [Fact]
    public void TheReportDescribesShapeAndNeverContent()
    {
        ClaudeTranscript();
        var sessions = Directory.CreateDirectory(Path.Combine(_home, ".config", "Code", "User", "workspaceStorage", "1a2b", "chatSessions")).FullName;
        File.WriteAllText(Path.Combine(sessions, "s1.json"), """{"version":3,"requests":[{"message":{"text":"secret question"}}]}""");

        Assert.Equal(0, new HistoryReport(Machine, new Say(_output)).Run());

        var report = _output.ToString();
        Assert.Contains("1 file(s)", report);
        Assert.Contains("sessionId", report);   // a field name
        Assert.Contains("requests", report);
        Assert.DoesNotContain("Jane", report); // never a value
        Assert.DoesNotContain("acme", report);
        Assert.DoesNotContain("secret question", report);
        Assert.Contains("capture-fixture vscode", report);
        Assert.Contains("GitHub Copilot CLI", report);
    }
}

/// <summary>
/// Token-shaped strings for the tests that must recognise tokens, built when the test runs: a
/// literal one in the source would be reported by the repository's own secret scanning, and
/// should be.
/// </summary>
internal static class FakeSecret
{
    public static string Token(string prefix) => prefix + new string('x', 30);
}
