using System.Net;
using System.Net.Http.Json;
using CopilotScope.Collector.Alerting;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Regression alerts and the weekly digest — the push half of the product.
///
/// A dashboard that must be visited gets abandoned; an output that triggers a decision gets
/// renewed. Which makes the precision of the trigger the whole feature: an alert channel that
/// cries wolf gets muted, and a muted channel is worth less than no channel, because the team
/// believes they have coverage. So most of what is tested here is what the detector refuses to
/// fire on.
/// </summary>
public sealed class AlertTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static CohortRow Row(string dimension, string value, double score, int sessions,
        double confidence = 0.8) =>
        new(dimension, value, sessions, Subjects: Math.Max(1, sessions / 2),
            InputTokens: 1000 * sessions, OutputTokens: 500 * sessions, CacheReadTokens: 0,
            ChatCalls: 4 * sessions, ChatErrors: 0, ToolCalls: sessions, ToolErrors: 0, Turns: 2 * sessions,
            EditsAccepted: sessions, EditsRejected: 0,
            AvgQualityScore: score, AvgConfidence: confidence, ErrorRate: 0);

    private static CohortReport Report(params CohortRow[] rows) =>
        new(T0.AddDays(-7), T0, rows.Sum(r => r.Sessions),
            rows.Where(r => r.Dimension == "repository").ToList(),
            rows.Where(r => r.Dimension == "assistant").ToList(),
            rows.Where(r => r.Dimension == "model").ToList(),
            rows.Where(r => r.Dimension == "kind").ToList());

    private static AlertOptions Options(Action<AlertOptions>? tweak = null)
    {
        var o = new AlertOptions { Enabled = true, WebhookUrl = "https://example.invalid/hook" };
        tweak?.Invoke(o);
        return o;
    }

    // ------------------------------------------------------------------ detector

    [Fact]
    public void ASustainedDropIsReported()
    {
        var regression = Assert.Single(RegressionDetector.Detect(
            Report(Row("assistant", "VSCode", 78, 40)),
            Report(Row("assistant", "VSCode", 66, 40)),
            Options()));

        Assert.Equal("VSCode", regression.Value);
        Assert.Equal(12, regression.Drop, 1);
        Assert.False(regression.BasisChanged);
    }

    [Fact]
    public void ADropSmallerThanTheThresholdIsNotAnAlert()
    {
        // Five points on a 0-100 composite is about a grade band. Below that, the noise in a
        // weekly mean is larger than the signal.
        Assert.Empty(RegressionDetector.Detect(
            Report(Row("assistant", "VSCode", 78, 40)),
            Report(Row("assistant", "VSCode", 75, 40)),
            Options()));
    }

    [Fact]
    public void ACohortTooSmallToHaveAMeanIsNeverAlertedOn()
    {
        // Either window being thin is enough to disqualify it: a mean over three sessions can
        // swing twenty points on one bad afternoon.
        Assert.Empty(RegressionDetector.Detect(
            Report(Row("assistant", "VSCode", 90, 3)),
            Report(Row("assistant", "VSCode", 60, 40)),
            Options()));

        Assert.Empty(RegressionDetector.Detect(
            Report(Row("assistant", "VSCode", 90, 40)),
            Report(Row("assistant", "VSCode", 60, 3)),
            Options()));
    }

    [Fact]
    public void ACohortWithNoPreviousWindowHasNothingToRegressFrom()
    {
        // A repository that appeared this week is new work, not a regression — and alerting on
        // it would fire on every new repository forever.
        Assert.Empty(RegressionDetector.Detect(
            Report(Row("repository", "acme/api", 80, 40)),
            Report(Row("repository", "acme/api", 80, 40), Row("repository", "acme/new", 40, 40)),
            Options()));
    }

    [Fact]
    public void AScoreDropThatCameWithAConfidenceDropIsNotClaimedAsAQualityRegression()
    {
        // This is the trap the whole feature turns on. The composite renormalizes over the
        // components that HAVE data (#100), so a cohort that stopped reporting feedback or edit
        // decisions is being measured on different evidence — not performing worse. Sending a
        // team to hunt a change that never happened is how an alert channel gets muted.
        var regression = Assert.Single(RegressionDetector.Detect(
            Report(Row("assistant", "ClaudeCode", 82, 40, confidence: 0.85)),
            Report(Row("assistant", "ClaudeCode", 68, 40, confidence: 0.45)),
            Options()));

        Assert.True(regression.BasisChanged);
        Assert.Contains("measurement basis changed", regression.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("quality 82", regression.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void ASteadyConfidenceMeansTheDropIsRealAndSaidPlainly()
    {
        var regression = Assert.Single(RegressionDetector.Detect(
            Report(Row("assistant", "ClaudeCode", 82, 40, confidence: 0.85)),
            Report(Row("assistant", "ClaudeCode", 68, 40, confidence: 0.83)),
            Options()));

        Assert.False(regression.BasisChanged);
        Assert.Contains("quality 82.0 → 68.0", regression.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionKindIsNeverAlertedOn()
    {
        // "Internal helper calls scored worse this week" is not a decision anyone can act on,
        // and every unactionable alert costs the credibility of the ones that matter.
        Assert.Empty(RegressionDetector.Detect(
            Report(Row("kind", "InternalTitleGeneration", 90, 40)),
            Report(Row("kind", "InternalTitleGeneration", 40, 40)),
            Options()));
    }

    [Fact]
    public void TheWorstDropIsReportedFirst()
    {
        var regressions = RegressionDetector.Detect(
            Report(Row("assistant", "VSCode", 80, 40), Row("model", "gpt-5", 80, 40)),
            Report(Row("assistant", "VSCode", 74, 40), Row("model", "gpt-5", 55, 40)),
            Options());

        Assert.Equal(2, regressions.Count);
        Assert.Equal("gpt-5", regressions[0].Value);
    }

    // -------------------------------------------------------------------- digest

    [Fact]
    public void TheHeadlineMeanIsWeightedBySessionsNotAMeanOfMeans()
    {
        // An assistant with 10 sessions must not weigh the same as one with 200 — otherwise a
        // quiet experiment drags the number a director reads.
        var digest = Digest.Build(
            Report(Row("assistant", "VSCode", 90, 200), Row("assistant", "Cursor", 50, 10)),
            Report(), [], T0.AddDays(-7), T0);

        Assert.True(digest.AvgQualityScore > 85, $"weighted mean was {digest.AvgQualityScore}");
    }

    [Fact]
    public void GroupsBelowTheReportingFloorDoNotDragTheHeadlineDown()
    {
        // A two-session group reports no average (0). Averaging that in would move the headline
        // for a reason that has nothing to do with quality.
        var digest = Digest.Build(
            Report(Row("assistant", "VSCode", 88, 100), Row("assistant", "Cursor", 0, 2)),
            Report(), [], T0.AddDays(-7), T0);

        Assert.Equal(88, digest.AvgQualityScore, 1);
    }

    [Fact]
    public void AnEmptyWeekSaysSoRatherThanReportingZeros()
    {
        var digest = Digest.Build(Report(), Report(), [], T0.AddDays(-7), T0);

        Assert.Contains(digest.Notes, n => n.Contains("No sessions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ABasisChangeIsExplainedInTheDigestNotes()
    {
        var regressions = RegressionDetector.Detect(
            Report(Row("assistant", "ClaudeCode", 82, 40, confidence: 0.85)),
            Report(Row("assistant", "ClaudeCode", 68, 40, confidence: 0.40)),
            Options());

        var digest = Digest.Build(Report(Row("assistant", "ClaudeCode", 68, 40)), Report(Row("assistant", "ClaudeCode", 82, 40)),
            regressions, T0.AddDays(-7), T0);

        Assert.Contains(digest.Notes, n => n.Contains("reporting change", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDigestTextCarriesNoIndividualIdentifiers()
    {
        // Built from rollups, so there is nothing individual in scope. Asserted anyway, because
        // this payload leaves the deployment.
        var digest = Digest.Build(
            Report(Row("repository", "acme/api", 80, 40), Row("assistant", "VSCode", 80, 40)),
            Report(), [], T0.AddDays(-7), T0);

        var text = Digest.ToText(digest);

        Assert.Contains("CopilotScope weekly digest", text, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "session-", "host-", "user-", "subject" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASmallGroupShowsNoAverageInTheTextRatherThanZero()
    {
        var digest = Digest.Build(Report(Row("assistant", "Cursor", 0, 2)), Report(), [], T0.AddDays(-7), T0);
        Assert.Contains("quality n/a", Digest.ToText(digest), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- dispatcher

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }

    private static (AlertDispatcher Dispatcher, CapturingHandler Handler) Dispatcher(AlertOptions options)
    {
        var handler = new CapturingHandler();
        return (new AlertDispatcher(new HttpClient(handler), options,
            NullLogger<AlertDispatcher>.Instance), handler);
    }

    [Fact]
    public async Task TheSlackFormatSendsASingleTextFieldMostChatWebhooksRender()
    {
        var (dispatcher, handler) = Dispatcher(Options(o => o.Format = "slack"));

        Assert.True(await dispatcher.SendAsync("regression", new { ignored = true }, "quality fell", CancellationToken.None));

        using var doc = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal("quality fell", doc.RootElement.GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task TheJsonFormatSendsTheWholeDocument()
    {
        var (dispatcher, handler) = Dispatcher(Options());

        Assert.True(await dispatcher.SendAsync("digest", new { sessions = 12 }, "text", CancellationToken.None));

        using var doc = System.Text.Json.JsonDocument.Parse(handler.Body!);
        Assert.Equal("digest", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(12, doc.RootElement.GetProperty("payload").GetProperty("sessions").GetInt32());
    }

    [Fact]
    public async Task NothingIsSentWhenAlertsAreOffOrHaveNoDestination()
    {
        var (off, offHandler) = Dispatcher(new AlertOptions { Enabled = false, WebhookUrl = "https://example.invalid/h" });
        Assert.False(await off.SendAsync("regression", new { }, "t", CancellationToken.None));
        Assert.Null(offHandler.Body);

        var (noUrl, noUrlHandler) = Dispatcher(new AlertOptions { Enabled = true, WebhookUrl = "" });
        Assert.False(await noUrl.SendAsync("regression", new { }, "t", CancellationToken.None));
        Assert.Null(noUrlHandler.Body);
    }

    [Fact]
    public async Task ARejectedWebhookFailsQuietlyRatherThanThrowingIntoIngest()
    {
        var (dispatcher, handler) = Dispatcher(Options());
        handler.Status = HttpStatusCode.InternalServerError;

        Assert.False(await dispatcher.SendAsync("regression", new { }, "t", CancellationToken.None));
    }

    // --------------------------------------------------------------- provisioning

    private static string? GrafanaRulesPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "grafana", "provisioning", "alerting",
                "copilotscope-rules.yml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void GrafanaRulesAreProvisionedAndNamedAgainstTheProvisionedDatasource()
    {
        var path = GrafanaRulesPath();
        Assert.NotNull(path);
        var yaml = File.ReadAllText(path!);

        Assert.Contains("copilotscope-quality-regression", yaml, StringComparison.Ordinal);
        Assert.Contains("copilotscope-friction-spike", yaml, StringComparison.Ordinal);
        // The uid has to match grafana/provisioning/datasources/prometheus.yml, or the rules
        // provision successfully and then never evaluate.
        Assert.Contains("datasourceUid: copilotscope-prometheus", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GrafanaRulesNeverRateOverAFamilyThatCanDecrease()
    {
        // The collector recomputes its Prometheus families from a capped in-memory set, so the
        // families declared `counter` can DECREASE when sessions age out — and rate()/increase()
        // read a decrease as a counter reset, firing on eviction rather than on anything real
        // (#70). Every expression in this file is a ratio of gauges instead, which is why these
        // rules do not have to wait for that fix.
        var yaml = File.ReadAllText(GrafanaRulesPath()!);

        // Only inspect the expressions. The header comment names rate() and the counter
        // families on purpose — explaining why they are avoided is the point of it.
        var expressions = yaml.Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Where(l => l.Contains("copilotscope_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(expressions);
        Assert.All(expressions, line =>
        {
            Assert.DoesNotContain("rate(", line, StringComparison.Ordinal);
            Assert.DoesNotContain("increase(", line, StringComparison.Ordinal);
            Assert.DoesNotContain("_total", line, StringComparison.Ordinal);
        });
    }

    // ----------------------------------------------------------------------- API

    private static WebApplicationFactory<SessionSummaryDto> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    [Fact]
    public async Task TheDigestEndpointServesTheAggregateWeekWithoutAWebhookConfigured()
    {
        // Reading the summary costs nothing and does not require opting into outbound traffic.
        using var factory = Factory();
        using var client = factory.CreateClient();

        var digest = await client.GetFromJsonAsync<DigestReport>("/api/digest?days=7");

        Assert.NotNull(digest);
        Assert.NotNull(digest.ByAssistant);
        Assert.NotNull(digest.Regressions);
    }

    [Fact]
    public async Task SendingTheDigestNeedsAConfiguredWebhookAndSaysWhatToSet()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/digest/send", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("CopilotScope:Alerts:WebhookUrl",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendingTheDigestIsAdminOnly()
    {
        // It puts the team's numbers on an external service. A read credential must not be
        // able to trigger that.
        using var factory = Factory(
            ("CopilotScope:Keys:Read:0", "read-key"),
            ("CopilotScope:Keys:Admin:0", "admin-key"));
        using var client = factory.CreateClient();

        var read = new HttpRequestMessage(HttpMethod.Post, "/api/digest/send");
        read.Headers.Add("x-api-key", "read-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(read)).StatusCode);

        var admin = new HttpRequestMessage(HttpMethod.Post, "/api/digest/send");
        admin.Headers.Add("x-api-key", "admin-key");
        // Past the authorization check, into "no webhook configured".
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(admin)).StatusCode);
    }

    [Fact]
    public async Task TheDigestObeysTheKAnonymityFloor()
    {
        // It leaves the deployment, so if anything the floor matters more here than on a screen.
        using var factory = Factory(
            ("CopilotScope:Privacy:Enabled", "true"),
            ("CopilotScope:Privacy:Salt", "test-salt"),
            ("CopilotScope:Privacy:MinimumGroupSize", "5"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/digest")).StatusCode);
    }
}
