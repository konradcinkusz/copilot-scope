using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CopilotScope.Local;
using CopilotScope.Local.Connecting;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Someone else's settings files, edited by the native binary's <c>connect</c> (ADR-004): the
/// rules a tool has to keep to be trusted with them.
/// </summary>
public sealed class SettingsFileTests : IDisposable
{
    private readonly string _directory = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_directory);

    private string File(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static JsonObject Patch => Assistants.ClaudeCodePatch(new ConnectSettings("http://localhost:4318"));

    [Fact]
    public void MergingKeepsEverythingElseAndBacksUpTheOriginal()
    {
        const string original = "{\"model\":\"opus\",\"env\":{\"FOO\":\"bar\"}}";
        var path = File("settings.json", original);

        Assert.Equal(EditResult.Written, SettingsFile.Merge(path, Patch));

        var merged = JsonNode.Parse(System.IO.File.ReadAllText(path))!;
        Assert.Equal("opus", (string?)merged["model"]);
        Assert.Equal("bar", (string?)merged["env"]!["FOO"]);
        Assert.Equal("http://localhost:4318", (string?)merged["env"]!["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal(original, System.IO.File.ReadAllText(path + SettingsFile.BackupSuffix));
    }

    [Fact]
    public void ConnectingTwiceChangesNothingAndKeepsTheFirstBackup()
    {
        const string original = "{\"model\":\"opus\"}";
        var path = File("settings.json", original);
        SettingsFile.Merge(path, Patch);
        var written = System.IO.File.ReadAllText(path);

        Assert.Equal(EditResult.Unchanged, SettingsFile.Merge(path, Patch));
        Assert.Equal(written, System.IO.File.ReadAllText(path));
        Assert.Equal(original, System.IO.File.ReadAllText(path + SettingsFile.BackupSuffix));
    }

    [Theory]
    [InlineData("{\n  // editor settings\n  \"editor.fontSize\": 14\n}")]
    [InlineData("{\"editor.fontSize\": 14,}")]
    [InlineData("[1, 2]")]
    public void AFileThatIsNotStrictJsonIsLeftAlone(string content)
    {
        // Rewriting it through a parser would silently drop the comments someone wrote.
        var path = File("settings.json", content);
        Assert.Equal(EditResult.Unparseable, SettingsFile.Merge(path, Patch));
        Assert.Equal(content, System.IO.File.ReadAllText(path));
        Assert.False(System.IO.File.Exists(path + SettingsFile.BackupSuffix));
    }

    [Fact]
    public void AMissingFileIsCreated()
    {
        var path = Path.Combine(_directory, "new", "settings.json");
        Assert.Equal(EditResult.Written, SettingsFile.Merge(path, Patch));
        Assert.NotNull(JsonNode.Parse(System.IO.File.ReadAllText(path))!["env"]);
    }

    [Fact]
    public void ALinkedSettingsFileIsWrittenThroughNotReplaced()
    {
        if (OperatingSystem.IsWindows()) return; // symbolic links need privileges there
        // Dotfile managers link settings into place; replacing the link would unmanage the file.
        var real = File("real.json", "{}");
        var link = Path.Combine(_directory, "settings.json");
        System.IO.File.CreateSymbolicLink(link, real);

        SettingsFile.Merge(link, Patch);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.NotNull(JsonNode.Parse(System.IO.File.ReadAllText(real))!["env"]);
    }

    [Fact]
    public void LineEndingsPermissionsAndTextAreKept()
    {
        var path = File("settings.json", "{\r\n  \"greeting\": \"Zażółć gęślą jaźń\"\r\n}\r\n");
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        SettingsFile.Merge(path, Patch);

        var text = System.IO.File.ReadAllText(path);
        Assert.Contains("Zażółć gęślą jaźń", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.EndsWith("}\r\n", text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(path));
    }

    [Fact]
    public void DisconnectingTakesOutExactlyWhatConnectingPutIn()
    {
        var path = File("settings.json", "{\"env\":{\"FOO\":\"bar\"}}");
        SettingsFile.Merge(path, Assistants.ClaudeCodePatch(new ConnectSettings("http://localhost:4318", "k", true, true)));

        Assert.Equal(EditResult.Written, SettingsFile.Remove(path, Assistants.ClaudeCodeKeys));

        var env = JsonNode.Parse(System.IO.File.ReadAllText(path))!["env"]!.AsObject();
        Assert.Equal(["FOO"], env.Select(kv => kv.Key));
    }

    [Fact]
    public void AnEnvBlockEmptiedByDisconnectingGoesToo()
    {
        var path = File("settings.json", "{\"model\":\"opus\"}");
        SettingsFile.Merge(path, Patch);
        SettingsFile.Remove(path, Assistants.ClaudeCodeKeys);
        Assert.Equal(["model"], JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void AFileWithoutOurKeysIsNotRewritten()
    {
        const string content = "{ \"model\" :   \"opus\" }";
        var path = File("settings.json", content);
        Assert.Equal(EditResult.Unchanged, SettingsFile.Remove(path, Assistants.ClaudeCodeKeys));
        Assert.Equal(content, System.IO.File.ReadAllText(path));
        Assert.Equal(EditResult.Missing, SettingsFile.Remove(Path.Combine(_directory, "none.json"), Assistants.ClaudeCodeKeys));
    }

    [Fact]
    public void VsCodesDottedNamesAreFlatKeys()
    {
        var path = File("settings.json", "{\"editor.fontSize\":14}");
        SettingsFile.Merge(path, Assistants.VsCodePatch(new ConnectSettings("http://localhost:4318", Capture: true)));

        var merged = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        Assert.Equal(true, (bool?)merged["github.copilot.chat.otel.enabled"]);
        Assert.Null(merged["github"]);

        SettingsFile.Remove(path, Assistants.VsCodeKeys);
        Assert.Equal(["editor.fontSize"], JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject().Select(kv => kv.Key));
    }
}

/// <summary>The block connect keeps in a shell profile for Copilot CLI.</summary>
public sealed class RcBlockTests : IDisposable
{
    private readonly string _directory = TempDirectory.Create();

    public void Dispose() => TempDirectory.Delete(_directory);

    [Fact]
    public void TheBlockIsReplacedNotRepeated()
    {
        var profile = Path.Combine(_directory, ".bashrc");
        File.WriteAllText(profile, "alias ll='ls -l'\n");

        RcBlock.Write(profile, "copilot-cli", ["export A=\"1\""]);
        RcBlock.Write(profile, "copilot-cli", ["export A=\"2\""]);

        var text = File.ReadAllText(profile);
        Assert.Single(Regex.Matches(text, Regex.Escape(RcBlock.Begin("copilot-cli"))));
        Assert.Contains("export A=\"2\"", text);
        Assert.StartsWith("alias ll='ls -l'\n", text);
    }

    [Fact]
    public void RemovingTheBlockLeavesTheFileAsItWas()
    {
        const string original = "alias ll='ls -l'\nexport PATH=\"$HOME/bin:$PATH\"\n";
        var profile = Path.Combine(_directory, ".zshrc");
        File.WriteAllText(profile, original);

        for (var i = 0; i < 3; i++)
        {
            RcBlock.Write(profile, "copilot-cli", ["export A=\"1\""]);
            RcBlock.Write(profile, "copilot-cli", []);
        }

        Assert.Equal(original, File.ReadAllText(profile));
    }

    [Fact]
    public void ABlockTheControlScriptWroteIsReadAndRemoved()
    {
        // What rc_block in scripts/copilotscope appends.
        var profile = Path.Combine(_directory, ".bashrc");
        File.WriteAllText(profile, "alias ll='ls -l'\n\n# >>> CopilotScope (copilot-cli) >>>\n" +
                                   "export COPILOT_OTEL_ENABLED=true\n" +
                                   "export OTEL_EXPORTER_OTLP_ENDPOINT=\"http://localhost:4318\"\n" +
                                   "# <<< CopilotScope (copilot-cli) <<<\n");

        var values = Assistants.ParseProfile(RcBlock.Read(profile, "copilot-cli")!);
        Assert.Equal("true", values["COPILOT_OTEL_ENABLED"]);
        Assert.Equal("http://localhost:4318", values["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        Assert.True(RcBlock.Write(profile, "copilot-cli", []));
        Assert.Equal("alias ll='ls -l'\n", File.ReadAllText(profile));
    }
}

/// <summary><c>connect</c>, <c>disconnect</c> and <c>setup</c> against a home directory of their own.</summary>
public sealed class ConnectorTests : IDisposable
{
    private const string Endpoint = "http://localhost:4318";
    private readonly string _home = TempDirectory.Create();
    private readonly StringWriter _output = new();

    public void Dispose() => TempDirectory.Delete(_home);

    private Machine Linux(string shell = "/bin/bash") => new(_home, Os.Linux, Shell: shell);

    private Connector Connector(Machine machine, IUserEnvironment? userEnvironment = null) =>
        new(machine, new Say(_output), userEnvironment);

    private static ConnectSettings Settings => new(Endpoint);

    private string VsCodeUserDirectory() =>
        Directory.CreateDirectory(Path.Combine(_home, ".config", "Code", "User")).FullName;

    [Fact]
    public void ConnectingClaudeCodeMakesItSendTelemetryHere()
    {
        var machine = Linux();
        Assert.Equal(0, Connector(machine).Connect("claude-code", Settings, print: false));

        Assert.Equal(Connection.Connected, Assistants.ClaudeCodeStatus(machine, Endpoint).Connection);
        var env = JsonNode.Parse(File.ReadAllText(machine.ClaudeSettings))!["env"]!.AsObject();
        Assert.Equal("1", (string?)env["CLAUDE_CODE_ENABLE_TELEMETRY"]);
        Assert.Equal("otlp", (string?)env["OTEL_LOGS_EXPORTER"]);
        Assert.Null(env["OTEL_LOG_USER_PROMPTS"]); // text stays out unless asked for
    }

    [Fact]
    public void AllMeansClaudeCodeAndVsCodeButNotAShellProfile()
    {
        var machine = Linux();
        VsCodeUserDirectory();

        Assert.Equal(0, Connector(machine).Connect("all", Settings, print: false));

        Assert.Equal(Connection.Connected, Assistants.ClaudeCodeStatus(machine, Endpoint).Connection);
        Assert.Equal(Connection.Connected, Assistants.VsCodeStatus(machine, Endpoint).Connection);
        Assert.False(File.Exists(Path.Combine(_home, ".bashrc")));
    }

    [Fact]
    public void CopilotCliGoesInTheShellProfileAndComesOutAgain()
    {
        var machine = Linux("/usr/bin/zsh");
        Connector(machine).Connect("copilot-cli", Settings, print: false);

        Assert.Contains("export COPILOT_OTEL_ENABLED=\"true\"", File.ReadAllText(Path.Combine(_home, ".zshrc")));
        Assert.Equal(Connection.Connected, Assistants.CopilotCliStatus(machine, Endpoint, null).Connection);

        Assert.Equal(0, Connector(machine).Disconnect("copilot-cli"));
        Assert.Equal("", File.ReadAllText(Path.Combine(_home, ".zshrc")));
    }

    [Fact]
    public void FishIsWrittenInFish()
    {
        var machine = Linux("/usr/bin/fish");
        Connector(machine).Connect("copilot-cli", Settings, print: false);

        var profile = File.ReadAllText(Path.Combine(_home, ".config", "fish", "config.fish"));
        Assert.Contains("set -gx COPILOT_OTEL_ENABLED \"true\"", profile);
        Assert.Equal(Connection.Connected, Assistants.CopilotCliStatus(machine, Endpoint, null).Connection);
    }

    [Fact]
    public void OnWindowsCopilotCliIsGivenUserEnvironmentVariables()
    {
        var machine = new Machine(_home, Os.Windows);
        var variables = new FakeUserEnvironment();

        Connector(machine, variables).Connect("copilot-cli", Settings, print: false);
        Assert.Equal("true", variables.Get("COPILOT_OTEL_ENABLED"));
        Assert.Equal(Connection.Connected, Assistants.CopilotCliStatus(machine, Endpoint, variables).Connection);

        Connector(machine, variables).Disconnect("all");
        Assert.Empty(variables.Values);
    }

    [Fact]
    public void PrintShowsTheChangeAndMakesNone()
    {
        var machine = Linux();
        VsCodeUserDirectory();

        Connector(machine).Connect("all", Settings, print: true);
        Connector(machine).Connect("copilot-cli", Settings, print: true);

        Assert.False(File.Exists(machine.ClaudeSettings));
        Assert.False(File.Exists(machine.VsCodeSettings()));
        Assert.False(File.Exists(Path.Combine(_home, ".bashrc")));
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", _output.ToString());
    }

    [Fact]
    public void SetupWritesOnlyWhatWasAgreedTo()
    {
        var machine = Linux();
        Directory.CreateDirectory(machine.ClaudeDirectory);
        VsCodeUserDirectory();
        var asked = new List<string>();

        Connector(machine).Setup(Settings, yes: false, ask: question =>
        {
            asked.Add(question);
            return question.Contains("Claude Code");
        });

        Assert.Equal(2, asked.Count);
        Assert.Equal(Connection.Connected, Assistants.ClaudeCodeStatus(machine, Endpoint).Connection);
        Assert.Equal(Connection.NotConnected, Assistants.VsCodeStatus(machine, Endpoint).Connection);
    }

    [Fact]
    public void SetupWithNoOneToAskWritesNothing()
    {
        var machine = Linux();
        Directory.CreateDirectory(machine.ClaudeDirectory);

        Connector(machine).Setup(Settings, yes: false, ask: null);

        Assert.False(File.Exists(machine.ClaudeSettings));
        Assert.Contains("setup --yes", _output.ToString());
    }

    [Fact]
    public void SetupPrintShowsEverythingAndAsksNothing()
    {
        var machine = Linux();
        Directory.CreateDirectory(machine.ClaudeDirectory);

        Connector(machine).Setup(Settings, yes: true, ask: _ => throw new InvalidOperationException("asked"), print: true);

        Assert.False(File.Exists(machine.ClaudeSettings));
        Assert.Contains("CLAUDE_CODE_ENABLE_TELEMETRY", _output.ToString());
    }

    [Fact]
    public void SetupYesConnectsEverythingFoundAndNothingElse()
    {
        var machine = Linux();
        Directory.CreateDirectory(machine.ClaudeDirectory);

        Connector(machine).Setup(Settings, yes: true, ask: null);

        Assert.Equal(Connection.Connected, Assistants.ClaudeCodeStatus(machine, Endpoint).Connection);
        Assert.Equal(Connection.NotInstalled, Assistants.VsCodeStatus(machine, Endpoint).Connection);
        Assert.False(File.Exists(Path.Combine(_home, ".bashrc"))); // Copilot CLI is not installed
    }

    [Fact]
    public void ASettingPointingSomewhereElseIsNotConnected()
    {
        var machine = Linux();
        Connector(machine).Connect("claude-code", new ConnectSettings("http://collector.internal:4318"), print: false);

        var status = Assistants.ClaudeCodeStatus(machine, Endpoint);
        Assert.Equal(Connection.Elsewhere, status.Connection);
        Assert.Equal("http://collector.internal:4318", status.Detail);
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318/", true)]
    [InlineData("http://[::1]:4318", true)]
    [InlineData("HTTP://LOCALHOST:4318", true)]
    [InlineData("http://localhost:4319", false)]
    [InlineData("https://localhost:4318", false)]
    [InlineData("http://collector.internal:4318", false)]
    public void AnyLoopbackSpellingOfThisEndpointIsThisEndpoint(string configured, bool same) =>
        Assert.Equal(same, Assistants.SameEndpoint(configured, Endpoint));

    private sealed class FakeUserEnvironment : IUserEnvironment
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public string? Get(string name) => Values.GetValueOrDefault(name);

        public void Set(string name, string? value)
        {
            if (value is null) Values.Remove(name);
            else Values[name] = value;
        }
    }
}

/// <summary>
/// The native binary and the control scripts must agree on what "connected" means, or
/// disconnecting with one would leave behind what the other wrote. The scripts are the
/// reference: these tests read them.
/// </summary>
public sealed class ConnectParityTests
{
    private static string Script(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", name))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new FileNotFoundException(name), "scripts", name));
    }

    private static string Function(string script, string name)
    {
        var start = script.IndexOf($"\n{name}() {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found");
        return script[start..script.IndexOf("\n}\n", start, StringComparison.Ordinal)];
    }

    private static readonly ConnectSettings Everything = new("http://localhost:4318", "key", Capture: true, Traces: true);

    [Fact]
    public void ClaudeCodeGetsTheControlScriptsVariables()
    {
        var written = Regex.Matches(Function(Script("copilotscope"), "claude_env_json"), "printf '[,{]*\"([A-Z_]+)\"")
            .Select(m => m.Groups[1].Value).Where(v => v != "env").ToHashSet();
        var ours = Assistants.ClaudeCodePatch(Everything)["env"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        Assert.Equal(written.Order(), ours.Order());
        Assert.Equal(written.Order(), Assistants.ClaudeCodeVariables.Order());
    }

    [Fact]
    public void DisconnectRemovesWhatTheControlScriptRemoves()
    {
        var lists = Regex.Matches(Function(Script("copilotscope"), "cmd_disconnect"), @"remove_json_keys ""\$\w+"" '(\[\[.*?\]\])'")
            .Select(m => JsonNode.Parse(m.Groups[1].Value)!.AsArray()
                .Select(path => string.Join("/", path!.AsArray().Select(s => (string)s!))).Order().ToList())
            .ToList();

        Assert.Equal(2, lists.Count);
        Assert.Equal(lists[0], Assistants.ClaudeCodeKeys.Select(p => string.Join("/", p)).Order());
        Assert.Equal(lists[1], Assistants.VsCodeKeys.Select(p => string.Join("/", p)).Order());
    }

    [Fact]
    public void VsCodeGetsTheControlScriptsSettings()
    {
        var written = Regex.Matches(Function(Script("copilotscope"), "vscode_settings_json"), "\"(github\\.[a-zA-Z.]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(written.Order(), Assistants.VsCodePatch(Everything).Select(kv => kv.Key).Order());
    }

    [Fact]
    public void CopilotCliGetsTheControlScriptsVariables()
    {
        var bash = Regex.Matches(Function(Script("copilotscope"), "connect_copilot_cli"), @"export ([A-Z_]+)=")
            .Select(m => m.Groups[1].Value).ToHashSet();
        var powershellRemoves = Regex.Match(Script("copilotscope.ps1"), @"foreach \(\$name in @\(([^)]*)\)\)")
            .Groups[1].Value.Split(',').Select(n => n.Trim().Trim('\'', '\n', '\r', ' ')).Where(n => n.Length > 0).ToHashSet();

        Assert.Equal(bash.Order(), Assistants.CopilotCliVariables(Everything).Select(v => v.Name).Order());
        Assert.Equal(powershellRemoves.Order(), Assistants.CopilotCliVariableNames.Order());
    }
}

/// <summary><c>copilotscope doctor</c>: the first broken link, named, with its fix.</summary>
public sealed class DoctorTests : IAsyncLifetime
{
    private readonly string _home = TempDirectory.Create();
    private readonly string _webRoot = TempDirectory.Create();
    private readonly string _transcripts = TempDirectory.Create();
    private readonly StringWriter _output = new();
    private static readonly Func<string, string?> CleanShell = _ => null;

    public Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_webRoot, "app.css"), "body{}");
        Directory.CreateDirectory(Path.Combine(_webRoot, "_framework"));
        File.WriteAllText(Path.Combine(_webRoot, "_framework", "blazor.web.js"), "// blazor");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var directory in new[] { _home, _webRoot, _transcripts }) TempDirectory.Delete(directory);
        return Task.CompletedTask;
    }

    private Machine Machine => new(_home, Os.Linux, Shell: "/bin/bash");

    private Doctor Doctor(Func<string, string?>? variables = null) =>
        new(Machine, new Say(_output), variables: variables ?? CleanShell);

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task NothingRunningIsTheFirstProblem()
    {
        var port = FreePort();
        var code = await Doctor().RunAsync(null, null, $"http://localhost:{port}", port, _webRoot, [_transcripts]);

        Assert.Equal(1, code);
        Assert.Contains("not running", _output.ToString());
    }

    [Fact]
    public async Task ARunningInstanceWithAConnectedAssistantIsHealthy()
    {
        await using var host = await LocalHost.StartAsync(new LocalHostOptions(0, 0, _home, null, _webRoot, "token"));
        var port = new Uri(host.CollectorUrl).Port;
        var instance = new Instance(Environment.ProcessId, port, new Uri(host.DashboardUrl).Port, "token", "test",
            DateTimeOffset.UtcNow, "memory");
        new Connector(Machine, new Say(TextWriter.Null)).Connect("claude-code", new ConnectSettings(instance.CollectorUrl), print: false);
        using var http = new HttpClient { BaseAddress = new Uri(host.CollectorUrl) };

        var code = await Doctor().RunAsync(instance, http, instance.CollectorUrl, port, _webRoot, [_transcripts]);

        Assert.True(code == 0, _output.ToString());
        Assert.Contains("Claude Code sends telemetry here", _output.ToString());
    }

    /// <summary>Claude Code connected an hour ago and used since: a transcript written after
    /// its settings were.</summary>
    private async Task<(LocalHost Host, Instance Instance, HttpClient Http)> ConnectedAnHourAgoAndUsedSinceAsync()
    {
        var host = await LocalHost.StartAsync(new LocalHostOptions(0, 0, _home, null, _webRoot, "token"));
        var port = new Uri(host.CollectorUrl).Port;
        var instance = new Instance(Environment.ProcessId, port, new Uri(host.DashboardUrl).Port, "token", "test",
            DateTimeOffset.UtcNow, "memory");
        new Connector(Machine, new Say(TextWriter.Null)).Connect("claude-code", new ConnectSettings(instance.CollectorUrl), print: false);
        File.SetLastWriteTimeUtc(Machine.ClaudeSettings, DateTime.UtcNow.AddHours(-1));
        var project = Directory.CreateDirectory(Path.Combine(_transcripts, "-home-dev")).FullName;
        File.WriteAllText(Path.Combine(project, "s.jsonl"), "{}\n");
        return (host, instance, new HttpClient { BaseAddress = new Uri(host.CollectorUrl) });
    }

    [Fact]
    public async Task ASessionStartedBeforeConnectingIsCaught()
    {
        // Nothing live has arrived since: the running claude read its settings before they
        // changed. An imported session is not telemetry, so it does not count.
        var (host, instance, http) = await ConnectedAnHourAgoAndUsedSinceAsync();
        await using (host)
        using (http)
        {
            var imported = new CopilotScope.Collector.Domain.CopilotSession
            {
                Id = "imported-since", Origin = CopilotScope.Collector.Domain.SessionOrigin.LogImport,
                EmitterKind = CopilotScope.Collector.Domain.EmitterKind.ClaudeCode,
                FirstSeen = DateTimeOffset.UtcNow.AddMinutes(-5), LastSeen = DateTimeOffset.UtcNow, ChatCalls = 1
            };
            (await http.PostAsJsonAsync("/api/import", new CopilotScope.Collector.Api.ImportRequest(
                [CopilotScope.Collector.Persistence.PersistedSession.From(imported)]))).EnsureSuccessStatusCode();

            var code = await Doctor().RunAsync(instance, http, instance.CollectorUrl, instance.OtlpPort, _webRoot, [_transcripts]);

            Assert.Equal(1, code);
            Assert.Contains("Restart claude", _output.ToString());
        }
    }

    [Fact]
    public async Task TelemetryThatArrivedSinceConnectingIsHealthy()
    {
        var (host, instance, http) = await ConnectedAnHourAgoAndUsedSinceAsync();
        await using (host)
        using (http)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var span = """
                {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"claude-code"}}]},
                "scopeSpans":[{"spans":[{"traceId":"0102030405060708090a0b0c0d0e0f10","spanId":"0102030405060708","name":"chat",
                "startTimeUnixNano":"START","endTimeUnixNano":"END","attributes":[
                {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                {"key":"gen_ai.conversation.id","value":{"stringValue":"live-since"}}]}]}]}]}
                """.Replace("START", now.ToString()).Replace("END", (now + 1_000_000_000).ToString());
            (await http.PostAsync("/v1/traces", new StringContent(span, System.Text.Encoding.UTF8, "application/json")))
                .EnsureSuccessStatusCode();

            var code = await Doctor().RunAsync(instance, http, instance.CollectorUrl, instance.OtlpPort, _webRoot, [_transcripts]);

            Assert.True(code == 0, _output.ToString());
            Assert.Contains("telemetry has arrived since it was connected", _output.ToString());
        }
    }

    [Fact]
    public async Task AnOverrideInTheShellIsNamed()
    {
        var port = FreePort();
        await Doctor(name => name == "OTEL_EXPORTER_OTLP_ENDPOINT" ? "http://elsewhere:4318" : null)
            .RunAsync(null, null, $"http://localhost:{port}", port, _webRoot, [_transcripts]);

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT is set to http://elsewhere:4318", _output.ToString());
    }
}
