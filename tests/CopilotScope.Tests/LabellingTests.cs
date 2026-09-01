using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotScope.Collector.Api;
using CopilotScope.Collector.Calibration;
using CopilotScope.JudgeAgent.Calibration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The labelling flow — the missing half of the calibration machinery.
///
/// <para>This repo says twice over that the composite is unvalidated: the calibration docs state
/// plainly that no calibration has been run and that there are no human labels, and the product
/// review calls the score "an opinion with a confidence interval, not a measurement". The
/// consumption side — quadratic-weighted κ, bootstrap CIs, human-ceiling checks — was already
/// built and had nothing to consume. So the test that matters most here is the round trip: a
/// study's export has to drop into <c>CalibrationEngine</c> with no hand-editing, or the flow
/// produces labels nobody can use.</para>
/// </summary>
public sealed class LabellingTests
{
    private static LabelStore Store(bool enabled = true, int max = 20_000) =>
        new(new LabellingOptions { Enabled = enabled, MaxInMemoryLabels = max });

    private static SessionLabel Label(string session, string rater, string algorithm, int? level = 2,
        string? note = null) =>
        new(session, rater, algorithm, level, note, DateTimeOffset.UtcNow);

    // -------------------------------------------------------------------- storing

    [Fact]
    public void ALabelIsRecordedAgainstItsRubric()
    {
        var store = Store();
        Assert.True(store.Record(Label("s1", "rater-a", "G-Eval"), out _));

        var stored = Assert.Single(store.ForSession("s1"));
        Assert.Equal("rater-a", stored.Rater);
        Assert.Equal(2, stored.Level);
    }

    [Fact]
    public void ARaterRevisingTheirOwnJudgmentReplacesIt()
    {
        // Keeping both would record a person as disagreeing with themselves, which is exactly
        // the kind of noise a human-ceiling calculation is supposed to detect and would here
        // be manufacturing.
        var store = Store();
        store.Record(Label("s1", "rater-a", "G-Eval", 1), out _);
        store.Record(Label("s1", "rater-a", "G-Eval", 3), out _);

        Assert.Equal(3, Assert.Single(store.ForSession("s1")).Level);
    }

    [Fact]
    public void TwoRatersOnOneSessionAreTwoLabels()
    {
        var store = Store();
        store.Record(Label("s1", "rater-a", "G-Eval", 1), out _);
        store.Record(Label("s1", "rater-b", "G-Eval", 3), out _);

        Assert.Equal(2, store.ForSession("s1").Count);
    }

    [Fact]
    public void AnOutOfScaleBandIsRejectedRatherThanClamped()
    {
        // Silently clamping would bury a broken labelling form inside a κ.
        var store = Store();

        Assert.False(store.Record(Label("s1", "rater-a", "G-Eval", 4), out var error));
        Assert.Contains("0..3", error!, StringComparison.Ordinal);
        Assert.False(store.Record(Label("s1", "rater-a", "G-Eval", -1), out _));
    }

    [Fact]
    public void AnUnknownRubricIsRejectedWithTheListOfRealOnes()
    {
        var store = Store();

        Assert.False(store.Record(Label("s1", "rater-a", "vibes"), out var error));
        Assert.Contains("G-Eval", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARetiredRubricIdIsCanonicalizedOnTheWayIn()
    {
        // Otherwise a label written against the old name forms a rubric of its own and never
        // pairs with a judge score — which reads as a judge that stopped emitting it.
        var store = Store();
        Assert.True(store.Record(Label("s1", "rater-a", "deep-frustration", 3), out _));

        Assert.Equal("deep-friction", Assert.Single(store.ForSession("s1")).Algorithm);
    }

    [Fact]
    public void ARequiredFieldMissingIsRejected()
    {
        var store = Store();
        Assert.False(store.Record(Label("", "rater-a", "G-Eval"), out _));
        Assert.False(store.Record(Label("s1", "  ", "G-Eval"), out _));
    }

    [Fact]
    public void TheInMemoryStoreIsBounded()
    {
        var store = Store(max: 2);
        Assert.True(store.Record(Label("s1", "r", "G-Eval"), out _));
        Assert.True(store.Record(Label("s2", "r", "G-Eval"), out _));
        Assert.False(store.Record(Label("s3", "r", "G-Eval"), out var error));
        Assert.Contains("full", error!, StringComparison.OrdinalIgnoreCase);

        // Revising an existing label still works at the cap: it replaces rather than grows.
        Assert.True(store.Record(Label("s1", "r", "G-Eval", 3), out _));
    }

    // --------------------------------------------------------------------- export

    [Fact]
    public void ASkippedRubricIsRecordedButNotExportedAsALabel()
    {
        // "This session has no retrieval context to judge RAGAS on" is information worth
        // keeping, and it is not a grade. Exporting it as one would be inventing an opinion.
        var store = Store();
        store.Record(Label("s1", "rater-a", "RAGAS", level: null), out _);
        store.Record(Label("s1", "rater-a", "G-Eval", 2), out _);

        Assert.Equal(2, store.ForSession("s1").Count);
        Assert.Equal("G-Eval", Assert.Single(store.Export().Labels).Algorithm);
    }

    [Fact]
    public void SeededSessionsAreExcludedFromTheExportByDefault()
    {
        // The repo's own research plan suggests labelling Seeder-generated sessions to
        // bootstrap the dataset — which would validate the scoring model against the synthetic
        // personas the same repository wrote. A calibration report built on that circle is
        // worse than none, because it arrives carrying a κ and a confidence interval.
        var store = Store();
        store.Record(Label("seed-quick-01-golden", "rater-a", "G-Eval", 3), out _);
        store.Record(Label("real-session-1", "rater-a", "G-Eval", 2), out _);

        var export = store.Export();

        Assert.Equal("real-session-1", Assert.Single(export.Labels).SessionId);
        Assert.DoesNotContain("synthetic", export.DatasetVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludingSyntheticSessionsMarksTheDatasetAsSuch()
    {
        // So the report cannot later be read as though it came from real work.
        var store = Store();
        store.Record(Label("seed-quick-01-golden", "rater-a", "G-Eval", 3), out _);

        var export = store.Export(includeSynthetic: true);

        Assert.Single(export.Labels);
        Assert.Contains("synthetic", export.DatasetVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludingSyntheticWithNoSeededLabelsDoesNotMisreportTheDataset()
    {
        var export = Store().Export(includeSynthetic: true);
        Assert.DoesNotContain("synthetic", export.DatasetVersion, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- round trip

    [Fact]
    public void AnExportedDatasetIsIngestedByTheCalibrationEngineWithoutHandEditing()
    {
        // The acceptance criterion this whole feature turns on: a study's output has to be
        // consumable, or the flow produces labels nobody can use. Serialized and re-read
        // through the same JSON shape the calibration/ files use, so the test would catch a
        // property-name drift rather than only an object-graph mismatch.
        var store = Store();
        var rubrics = RubricScale.Rubrics.Keys.ToList();
        foreach (var session in new[] { "s1", "s2", "s3", "s4" })
            foreach (var rater in new[] { "rater-a", "rater-b" })
                foreach (var rubric in rubrics)
                    store.Record(Label(session, rater, rubric, session == "s1" ? 3 : 1), out _);

        var json = JsonSerializer.Serialize(store.Export(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var dataset = JsonSerializer.Deserialize<CalibrationDataset>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(dataset);
        Assert.NotEmpty(dataset!.Labels!);

        // The engine has to accept it — verdict content is CalibrationEngineTests' business;
        // what matters here is that it parses and evaluates rather than throwing.
        var report = new CalibrationEngine().Evaluate(dataset);
        Assert.NotEmpty(report.Rubrics);
        Assert.All(report.Rubrics, r => Assert.NotNull(RubricScale.Find(r.Algorithm)));
    }

    [Fact]
    public void TheExportedShapeMatchesTheCommittedExampleDataset()
    {
        // calibration/labels.example.json is the format template a labeller's output has to
        // match. Comparing property names rather than eyeballing them is what keeps the two
        // from drifting apart silently.
        var store = Store();
        store.Record(Label("s1", "rater-a", "G-Eval", 2, "a note"), out _);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(store.Export(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var label = doc.RootElement.GetProperty("labels")[0];

        foreach (var field in new[] { "sessionId", "rater", "algorithm", "level", "note" })
            Assert.True(label.TryGetProperty(field, out _), $"exported label is missing '{field}'");
        Assert.True(doc.RootElement.TryGetProperty("datasetVersion", out _));
    }

    // ------------------------------------------------------------------- the API

    private static WebApplicationFactory<SessionSummaryDto> Factory(bool labelling) =>
        new WebApplicationFactory<SessionSummaryDto>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CopilotScope:Labelling:Enabled"] = labelling ? "true" : "false" })));

    [Fact]
    public async Task LabellingIsOffByDefaultAndTheEndpointSaysHowToTurnItOn()
    {
        using var factory = Factory(labelling: false);
        using var client = factory.CreateClient();

        using var rubrics = JsonDocument.Parse(await client.GetStringAsync("/api/labels/rubrics"));
        Assert.False(rubrics.RootElement.GetProperty("enabled").GetBoolean());

        var response = await client.PostAsJsonAsync("/api/labels",
            new[] { Label("s1", "rater-a", "G-Eval") });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("CopilotScope:Labelling:Enabled",
            await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRubricsEndpointServesTheQuestionsARaterAnswers()
    {
        using var factory = Factory(labelling: true);
        using var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/labels/rubrics"));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(RubricScale.Categories, root.GetProperty("categories").GetInt32());
        Assert.Equal(RubricScale.Categories, root.GetProperty("bands").GetArrayLength());

        var rubrics = root.GetProperty("rubrics").EnumerateArray().ToList();
        Assert.Equal(RubricScale.Rubrics.Count, rubrics.Count);
        // Every band carries its anchor text: a rater grading without anchors is grading on
        // their own scale, and the agreement statistic would measure the scales, not the work.
        Assert.All(root.GetProperty("bands").EnumerateArray(),
            b => Assert.False(string.IsNullOrWhiteSpace(b.GetProperty("anchor").GetString())));
        // The inverted rubric is flagged, so the form can warn about its direction.
        Assert.Contains(rubrics, r => !r.GetProperty("higherIsBetter").GetBoolean());
    }

    [Fact]
    public async Task ARaterCanLabelASessionAndReadItBack()
    {
        using var factory = Factory(labelling: true);
        using var client = factory.CreateClient();

        var submitted = RubricScale.Rubrics.Keys
            .Select(r => Label("real-session-1", "rater-a", r, 2)).ToArray();
        Assert.True((await client.PostAsJsonAsync("/api/labels", submitted)).IsSuccessStatusCode);

        var read = await client.GetFromJsonAsync<List<SessionLabel>>("/api/labels?sessionId=real-session-1");
        Assert.Equal(RubricScale.Rubrics.Count, read!.Count);
    }

    [Fact]
    public async Task ABadBandIsReportedPerRubricRatherThanFailingTheWholeForm()
    {
        using var factory = Factory(labelling: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/labels", new[]
        {
            Label("real-session-1", "rater-a", "G-Eval", 2),
            Label("real-session-1", "rater-a", "SPUR", 9),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The good one was still taken: losing a rater's whole form over one mis-click is how
        // a study loses its raters.
        Assert.Contains("\"accepted\":1", body.Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains("SPUR", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExportEndpointExcludesSeededSessionsUnlessAsked()
    {
        using var factory = Factory(labelling: true);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/labels", new[]
        {
            Label("seed-quick-01-golden", "rater-a", "G-Eval", 3),
            Label("real-session-1", "rater-a", "G-Eval", 2),
        });

        var quarantined = await client.GetFromJsonAsync<LabelDataset>("/api/labels/export");
        Assert.Equal("real-session-1", Assert.Single(quarantined!.Labels).SessionId);

        var everything = await client.GetFromJsonAsync<LabelDataset>("/api/labels/export?includeSynthetic=true");
        Assert.Equal(2, everything!.Labels.Count);
        Assert.Contains("synthetic", everything.DatasetVersion, StringComparison.Ordinal);
    }
}
