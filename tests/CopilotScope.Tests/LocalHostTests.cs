using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CopilotScope.Local;
using CopilotScope.Local.Scanning;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>The command line of the native <c>copilotscope</c> binary (ADR-004).</summary>
public sealed class LocalCommandLineTests
{
    [Fact]
    public void NoArgumentsMeansStartWithTheStandardPorts()
    {
        var (options, error) = CommandLine.Parse([]);
        Assert.Null(error);
        Assert.Equal("start", options!.Command);
        Assert.Equal(4318, options.OtlpPort);
        Assert.Equal(5200, options.DashboardPort);
        Assert.False(options.Memory);
    }

    [Fact]
    public void OptionsTakeTheirValueSpacedOrInline()
    {
        var (options, _) = CommandLine.Parse(["start", "--otlp-port", "14318", "--dashboard-port=15200",
            "--data", "/tmp/scope", "--webroot=/opt/www", "--no-browser", "--verbose"]);
        Assert.Equal(14318, options!.OtlpPort);
        Assert.Equal(15200, options.DashboardPort);
        Assert.Equal("/tmp/scope", options.DataDirectory);
        Assert.Equal("/opt/www", options.WebRoot);
        Assert.True(options.NoBrowser);
        Assert.True(options.Verbose);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("open")]
    [InlineData("url")]
    [InlineData("scan")]
    public void EveryCommandIsRecognised(string command) =>
        Assert.Equal(command, CommandLine.Parse([command]).Options!.Command);

    [Fact]
    public void LocalHistoryIsReadUnlessTurnedOff()
    {
        Assert.False(CommandLine.Parse([]).Options!.NoScan);
        Assert.True(CommandLine.Parse(["--no-scan"]).Options!.NoScan);
    }

    [Fact]
    public void HelpAndVersionAnswerWhateverElseIsOnTheLine()
    {
        Assert.Equal("help", CommandLine.Parse(["stop", "--help"]).Options!.Command);
        Assert.Equal("version", CommandLine.Parse(["--version"]).Options!.Command);
    }

    [Theory]
    [InlineData(new[] { "--bind", "0.0.0.0" }, "Unknown option")]
    [InlineData(new[] { "launch" }, "Unknown command")]
    [InlineData(new[] { "start", "stop" }, "Unexpected argument")]
    [InlineData(new[] { "--otlp-port" }, "needs a value")]
    [InlineData(new[] { "--otlp-port", "--memory" }, "needs a value")]
    [InlineData(new[] { "--otlp-port", "70000" }, "between 1 and 65535")]
    [InlineData(new[] { "--dashboard-port", "http" }, "between 1 and 65535")]
    [InlineData(new[] { "--memory", "--data", "/tmp/x" }, "contradict")]
    [InlineData(new[] { "--otlp-port", "5200" }, "must differ")]
    public void MistakesAreRefusedWithAReason(string[] args, string reason)
    {
        var (options, error) = CommandLine.Parse(args);
        Assert.Null(options);
        Assert.Contains(reason, error);
    }
}

/// <summary>The native host's own state: its web root, its instance record, its port probe.</summary>
public sealed class LocalStateTests : IDisposable
{
    private readonly string _directory = TempDirectory.Create();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WebRoot.Variable, null);
        TempDirectory.Delete(_directory);
    }

    [Fact]
    public void TheWebRootComesFromTheOptionThenTheEnvironmentThenBesideTheBinary()
    {
        Environment.SetEnvironmentVariable(WebRoot.Variable, "/from/environment");
        Assert.Equal(Path.GetFullPath("/from/option"), WebRoot.Resolve("/from/option"));
        Assert.Equal(Path.GetFullPath("/from/environment"), WebRoot.Resolve(null));

        Environment.SetEnvironmentVariable(WebRoot.Variable, null);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot")), WebRoot.Resolve(null));
    }

    [Fact]
    public void AWebRootWithoutTheBlazorScriptIsNotComplete()
    {
        // The failure that renders every page once and then never responds (Dockerfile.dashboard).
        File.WriteAllText(Path.Combine(_directory, "app.css"), "");
        Assert.False(WebRoot.IsComplete(_directory));

        Directory.CreateDirectory(Path.Combine(_directory, "_framework"));
        File.WriteAllText(Path.Combine(_directory, "_framework", "blazor.web.js"), "");
        Assert.True(WebRoot.IsComplete(_directory));
    }

    [Fact]
    public void TheInstanceRecordIsTheOwnersAloneAndOnlyItsOwnerRemovesIt()
    {
        var path = Path.Combine(_directory, "instance.json");
        var instance = new Instance(42, 4318, 5200, "token-a", "1.2.3", DateTimeOffset.UtcNow, "/data");
        instance.Write(path);

        Assert.Equal(instance, Instance.Read(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        // A shutdown racing a newer start must not delete the newer start's record.
        Instance.Delete(path, "token-b");
        Assert.True(File.Exists(path));
        Instance.Delete(path, "token-a");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnUnreadableInstanceRecordCountsAsNone()
    {
        var path = Path.Combine(_directory, "instance.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(Instance.Read(path));
    }

    [Fact]
    public async Task APortHeldByAnotherProgramIsNotFree()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortState.Other, await Ports.ProbeAsync(port));

        listener.Stop();
        Assert.Equal(PortState.Free, await Ports.ProbeAsync(port));
    }
}

/// <summary>
/// The collector and the dashboard running together in one process, as <c>copilotscope</c>
/// runs them — on dynamic ports, so the test never collides with a real instance.
/// </summary>
public sealed class LocalHostTests : IAsyncLifetime
{
    private readonly string _home = TempDirectory.Create();
    private readonly string _webRoot = TempDirectory.Create();
    private const string Token = "test-token";

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

    private LocalHostOptions Options(string? data, string? webRoot = null) =>
        new(0, 0, _home, data, webRoot ?? _webRoot, Token);

    private static HttpContent ChatSpan(string conversationId) => new StringContent("""
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"copilot-chat"}}]},
        "scopeSpans":[{"spans":[{"traceId":"0102030405060708090a0b0c0d0e0f10","spanId":"0102030405060708","name":"chat gpt-5",
        "startTimeUnixNano":"1790000000000000000","endTimeUnixNano":"1790000001000000000","attributes":[
        {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
        {"key":"gen_ai.conversation.id","value":{"stringValue":"CONVERSATION"}},
        {"key":"gen_ai.usage.input_tokens","value":{"intValue":"42"}}]}]}]}]}
        """.Replace("CONVERSATION", conversationId), Encoding.UTF8, "application/json");

    [Fact]
    public async Task BothApplicationsServeFromOneProcessAndKeepSessionsOnDisk()
    {
        var data = Path.Combine(_home, "data");
        await using (var host = await LocalHost.StartAsync(Options(data)))
        {
            using var http = new HttpClient();

            var health = await http.GetFromJsonAsync<JsonElement>($"{host.CollectorUrl}/api/health");
            Assert.Equal("files", health.GetProperty("storage").GetString());

            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{host.DashboardUrl}/_framework/blazor.web.js")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{host.DashboardUrl}/docs")).StatusCode);

            Assert.Equal(HttpStatusCode.OK,
                (await http.PostAsync($"{host.CollectorUrl}/v1/traces", ChatSpan("conv-local"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{host.CollectorUrl}/api/sessions/conv-local")).StatusCode);
        }

        // Disposing stopped the collector, whose final flush wrote the session.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(data, "sessions"), "*.json"));
    }

    [Fact]
    public async Task OnlyLoopbackNamesAreAnswered()
    {
        // A page on another site that got the browser to resolve its name to 127.0.0.1 (DNS
        // rebinding) sends its own name as Host, and must not be able to read the transcripts.
        await using var host = await LocalHost.StartAsync(Options(null));
        using var http = new HttpClient();

        foreach (var url in new[] { host.CollectorUrl + "/api/sessions", host.DashboardUrl + "/docs" })
        {
            var rebound = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Host = "attacker.example" } };
            Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(rebound)).StatusCode);
        }
    }

    [Fact]
    public async Task OnlyTheTokenStopsIt()
    {
        await using var host = await LocalHost.StartAsync(Options(null));
        using var http = new HttpClient();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"{host.CollectorUrl}/_copilotscope/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"{host.CollectorUrl}/_copilotscope/stop", null)).StatusCode);
        Assert.False(host.Stopped.IsCompleted);

        var ping = new HttpRequestMessage(HttpMethod.Get, $"{host.CollectorUrl}/_copilotscope/ping");
        ping.Headers.Add("X-CopilotScope-Token", Token);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(ping)).StatusCode);

        var stop = new HttpRequestMessage(HttpMethod.Post, $"{host.CollectorUrl}/_copilotscope/stop");
        stop.Headers.Add("X-CopilotScope-Token", Token);
        Assert.Equal(HttpStatusCode.Accepted, (await http.SendAsync(stop)).StatusCode);
        await host.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MemoryModeKeepsNothingOnDisk()
    {
        await using var host = await LocalHost.StartAsync(Options(null));
        using var http = new HttpClient();
        var health = await http.GetFromJsonAsync<JsonElement>($"{host.CollectorUrl}/api/health");
        Assert.Equal("memory", health.GetProperty("storage").GetString());
        Assert.False(Directory.Exists(Path.Combine(_home, "data")));
    }

    [Fact]
    public async Task MissingDashboardFilesAreSaidOutLoudInsteadOfServedBroken()
    {
        var empty = TempDirectory.Create();
        try
        {
            await using var host = await LocalHost.StartAsync(Options(null, empty));
            using var http = new HttpClient();

            var page = await http.GetAsync(host.DashboardUrl + "/");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
            Assert.Contains("dashboard's files are missing", await page.Content.ReadAsStringAsync());
            // Telemetry is still collected while the dashboard cannot work.
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"{host.CollectorUrl}/api/health")).StatusCode);
        }
        finally { TempDirectory.Delete(empty); }
    }

    [Fact]
    public async Task LocalHistoryIsImportedAndTheScanStateKeptWithTheData()
    {
        var data = Path.Combine(_home, "data");
        var projects = Directory.CreateDirectory(Path.Combine(_home, "claude", "projects", "-home-dev-acme")).FullName;
        var transcript = Path.Combine(projects, "11111111-2222-3333-4444-555555555555.jsonl");
        File.Copy(LogImportTests.FixturePath(), transcript);
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddHours(-1)); // quiet, so read at once

        var options = Options(data) with { ScanSources = [new ClaudeCodeSource([Path.GetDirectoryName(projects)!])] };
        await using (var host = await LocalHost.StartAsync(options))
        {
            using var http = new HttpClient();
            var found = false;
            for (var i = 0; i < 100 && !found; i++)
            {
                found = (await http.GetAsync($"{host.CollectorUrl}/api/sessions/11111111-2222-3333-4444-555555555555"))
                    .StatusCode == HttpStatusCode.OK;
                if (!found) await Task.Delay(100);
            }
            Assert.True(found, "the transcript was not imported by the background scan");

            // `copilotscope scan` asks the running instance, with its token.
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"{host.CollectorUrl}/_copilotscope/scan", null)).StatusCode);
            var scan = new HttpRequestMessage(HttpMethod.Post, $"{host.CollectorUrl}/_copilotscope/scan");
            scan.Headers.Add("X-CopilotScope-Token", Token);
            using var response = await http.SendAsync(scan);
            var report = await response.Content.ReadFromJsonAsync<ScanReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var source = Assert.Single(report!.Sources);
            Assert.Equal((1, 1), (source.Found, source.Unchanged));
        }

        Assert.True(File.Exists(Path.Combine(data, ScanState.FileName)));
    }

    [Fact]
    public async Task ScanningCanBeOff()
    {
        await using var host = await LocalHost.StartAsync(Options(null));
        using var http = new HttpClient();
        var scan = new HttpRequestMessage(HttpMethod.Post, $"{host.CollectorUrl}/_copilotscope/scan");
        scan.Headers.Add("X-CopilotScope-Token", Token);
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(scan)).StatusCode);
        Assert.Null(host.Scanner);
    }

    [Fact]
    public async Task ARunningCollectorIsRecognisedOnItsPort()
    {
        await using var host = await LocalHost.StartAsync(Options(null));
        Assert.Equal(PortState.CopilotScope, await Ports.ProbeAsync(new Uri(host.CollectorUrl).Port));
    }
}
