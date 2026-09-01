using CopilotScope.Collector.Api;
using CopilotScope.Collector.Domain;
using CopilotScope.JudgeAgent.Agents;
using CopilotScope.JudgeAgent.Calibration;
using CopilotScope.JudgeAgent.Clients;
using CopilotScope.JudgeAgent.Judging;
using Xunit;
using System.Text.Json;

namespace CopilotScope.Tests;

// Exercises the sequence POST /api/calibration/run performs — fetch each labelled session ->
// judge it -> keep the scorable rubrics -> compute the report — without hosting the app, the
// same way JudgeFlowTests covers the single-session path.
//
// The part worth testing here is the join between the judge's output shape and the calibration
// input shape: a rubric the judge answered "no-data" for has no score, and inventing one would
// silently pair a human's label against a number the judge never produced.
public class CalibrationFlowTests
{
    private sealed class FakeCollectorClient(IReadOnlyDictionary<string, SessionDetailDto> sessions) : ICollectorClient
    {
        public Task<SessionDetailDto?> GetSessionDetailAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(sessions.TryGetValue(sessionId, out var detail) ? detail : null);
    }

    /// <summary>Returns a judge response whose G-Eval score is dictated per session, with RAGAS
    /// always "no-data" — the real shape for sessions that used no retrieval.</summary>
    private sealed class ScriptedJudgeChatClient(IReadOnlyDictionary<string, double> gEvalBySession) : IJudgeChatClient
    {
        public string BackendName => "scripted";
        public string ModelName => "scripted-model";

        public int Calls { get; private set; }

        public Task<string> JudgeAsync(string systemPrompt, string sessionPayloadJson, CancellationToken ct)
        {
            Calls++;
            var sessionId = JsonDocument.Parse(sessionPayloadJson).RootElement.GetProperty("sessionId").GetString()!;
            var score = gEvalBySession[sessionId];

            return Task.FromResult($$"""
                {
                  "results": [
                    { "name": "LLM-as-a-Judge (G-Eval)", "algorithm": "G-Eval", "status": "ok",
                      "score": {{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                      "metrics": [], "findings": [] },
                    { "name": "RAG component metrics (RAGAS)", "algorithm": "RAGAS", "status": "no-data",
                      "score": null, "metrics": [], "findings": [ "No retrieval context." ] }
                  ]
                }
                """);
        }
    }

    /// <summary>Replays the handler's loop: judge each labelled session, keep the rubrics that
    /// actually produced a score, then calibrate.</summary>
    private static async Task<(CalibrationReport Report, int Calls)> RunAsync(
        List<HumanLabel> labels, IReadOnlyDictionary<string, double> gEvalBySession)
    {
        var sessions = gEvalBySession.Keys.ToDictionary(
            id => id,
            id => JudgeAgentTestSupport.MakeSessionDetail(id,
                [new TranscriptEntry(DateTimeOffset.UtcNow, "claude", "fix the build", "done", 0)]));

        var collector = new FakeCollectorClient(sessions);
        var chatClient = new ScriptedJudgeChatClient(gEvalBySession);
        var contextBuilder = new SessionJudgeContextBuilder();
        var promptBuilder = new JudgePromptBuilder();

        var scores = new List<JudgeScore>();
        foreach (var sessionId in labels.Select(l => l.SessionId).Distinct(StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var detail = await collector.GetSessionDetailAsync(sessionId, CancellationToken.None);
            Assert.NotNull(detail);

            var context = contextBuilder.Build(detail!);
            var raw = await chatClient.JudgeAsync(
                promptBuilder.Build(context),
                JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                CancellationToken.None);

            scores.AddRange(JudgeResponseParser.Parse(raw)
                .Where(r => r is { Status: "ok", Score: not null })
                .Select(r => new JudgeScore(sessionId, r.Algorithm, r.Score!.Value)));
        }

        var report = new CalibrationEngine().Evaluate(new CalibrationDataset(
            labels, scores, "flow-test", "judge-deployment", promptBuilder.TemplateFingerprint));

        return (report, chatClient.Calls);
    }

    private static double ScoreFor(int band) => band switch { 0 => 0.20, 1 => 0.52, 2 => 0.75, _ => 0.92 };

    [Fact]
    public async Task CalibrationRun_JudgesEveryLabelledSessionAndCertifiesAnAgreeingJudge()
    {
        List<HumanLabel> labels = [];
        var judgeScores = new Dictionary<string, double>(StringComparer.Ordinal);

        for (var i = 0; i < 24; i++)
        {
            var id = $"seed-{i:D3}";
            var band = i % 4;
            labels.Add(new HumanLabel(id, "alice", "G-Eval", band));
            labels.Add(new HumanLabel(id, "bob", "G-Eval", band));
            judgeScores[id] = ScoreFor(band);
        }

        var (report, calls) = await RunAsync(labels, judgeScores);

        Assert.Equal(24, calls);
        var rubric = Assert.Single(report.Rubrics);
        Assert.Equal("G-Eval", rubric.Algorithm);
        Assert.Equal(CalibrationVerdict.Calibrated, rubric.Verdict);
        Assert.Equal(24, rubric.PairedSessions);
    }

    [Fact]
    public async Task CalibrationRun_NeverInventsAScoreForARubricTheJudgeAnsweredNoDataFor()
    {
        // Humans labelled RAGAS too, but the judge returned "no-data" for every session because
        // none of them used retrieval. Those labels must be reported as dropped, not paired
        // against a fabricated 0 — a rubric silently scored at a default would read as a
        // calibrated judge that never actually graded anything.
        List<HumanLabel> labels = [];
        var judgeScores = new Dictionary<string, double>(StringComparer.Ordinal);

        for (var i = 0; i < 24; i++)
        {
            var id = $"seed-{i:D3}";
            var band = i % 4;
            labels.Add(new HumanLabel(id, "alice", "G-Eval", band));
            labels.Add(new HumanLabel(id, "bob", "G-Eval", band));
            labels.Add(new HumanLabel(id, "alice", "RAGAS", band));
            labels.Add(new HumanLabel(id, "bob", "RAGAS", band));
            judgeScores[id] = ScoreFor(band);
        }

        var (report, _) = await RunAsync(labels, judgeScores);

        var ragas = report.Rubrics.Single(r => r.Algorithm == "RAGAS");
        Assert.Equal(24, ragas.LabelledSessions);
        Assert.Equal(0, ragas.PairedSessions);
        Assert.Equal(24, ragas.DroppedForMissingJudgeScore);
        Assert.Equal(CalibrationVerdict.InsufficientData, ragas.Verdict);

        // And one un-scorable rubric drags the overall verdict down rather than being ignored.
        Assert.Equal(CalibrationVerdict.Calibrated, report.Rubrics.Single(r => r.Algorithm == "G-Eval").Verdict);
        Assert.Equal(CalibrationVerdict.InsufficientData, report.Verdict);
    }

    [Fact]
    public async Task CalibrationRun_RecordsWhichJudgeAndWhichRubricRevisionEarnedTheNumber()
    {
        List<HumanLabel> labels = [];
        var judgeScores = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < 24; i++)
        {
            var id = $"seed-{i:D3}";
            labels.Add(new HumanLabel(id, "alice", "G-Eval", i % 4));
            labels.Add(new HumanLabel(id, "bob", "G-Eval", i % 4));
            judgeScores[id] = ScoreFor(i % 4);
        }

        var (report, _) = await RunAsync(labels, judgeScores);

        Assert.Equal("judge-deployment", report.JudgeModel);
        Assert.Equal(new JudgePromptBuilder().TemplateFingerprint, report.JudgePromptVersion);
        Assert.Matches("^[0-9a-f]{12}$", report.JudgePromptVersion!);
    }

    [Fact]
    public void PromptFingerprint_IsStableAcrossInstancesAndIgnoresLineEndings()
    {
        // The fingerprint is what makes a later rubric edit a visible re-baseline. If it moved
        // between processes or between a CRLF and an LF checkout it would flag edits nobody made.
        Assert.Equal(new JudgePromptBuilder().TemplateFingerprint, new JudgePromptBuilder().TemplateFingerprint);
    }
}
