using CopilotScope.Collector.Domain;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// What the project claims to support, held to what it can actually demonstrate.
///
/// <para>These are documentation tests, which is unusual — but the claim they guard is the one
/// this project can least afford to get wrong. Radical honesty is its main competitive virtue:
/// it is the reason the score publishes its own confidence and the reason the calibration docs
/// say "no calibration has been run". A support claim the code cannot back costs more than the
/// feature is worth, because the first reader who checks finds the gap and then nothing else
/// the project says is trusted either.</para>
///
/// <para>Cursor was exactly that gap — a <c>service.name</c> substring check with no captured
/// payload, counted in a "five assistants" headline while another document said four. Resolved
/// as demote rather than implement in ADR-002 (#93). These tests are what stop it drifting
/// back, and what stops the two counts disagreeing again.</para>
/// </summary>
public sealed class AssistantClaimTests
{
    private static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }

    /// <summary>The assistants the project actually claims. Cursor is deliberately absent.</summary>
    private static readonly EmitterKind[] Supported =
        [EmitterKind.VSCode, EmitterKind.CLI, EmitterKind.ClaudeCode, EmitterKind.Cowork];

    [Fact]
    public void TheReadmeAndTheStrategyAgreeOnTheAssistantCount()
    {
        // They previously agreed on neither the number nor the membership: the README counted
        // five (including Cowork and Cursor) while STRATEGY counted four (including Cursor and
        // omitting Cowork). A count that slips between two documents is a claim nobody is
        // maintaining.
        var readme = RepoFile("README.md");
        var strategy = RepoFile("docs/STRATEGY.md");

        Assert.Contains("Four assistants", readme, StringComparison.Ordinal);
        Assert.Contains("Four assistants", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Five assistants", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Five assistants", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void BothDocumentsNameTheSameFourAssistants()
    {
        // The count agreeing is not enough — two documents can both say "four" and disagree
        // about which four, which is the state this replaced.
        foreach (var document in new[] { RepoFile("README.md"), RepoFile("docs/STRATEGY.md") })
            foreach (var assistant in new[] { "VS Code Copilot", "Copilot CLI", "Claude Code", "Claude Cowork" })
                Assert.Contains(assistant, document, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoverageMatrixMarksCursorUnverified()
    {
        // The matrix is served to the dashboard, the docs page and GET /api/coverage from this
        // one table, so marking it here is what makes the demotion visible everywhere at once.
        var cursor = EmitterCoverage.For(EmitterKind.Cursor);

        Assert.NotNull(cursor);
        Assert.Contains("UNVERIFIED", cursor!.Note, StringComparison.Ordinal);
        Assert.Contains("ADR-002", cursor.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSupportedAssistantIsMarkedUnverified()
    {
        // The guard has to cut both ways: a demotion that quietly spread to an assistant the
        // project does support would be the same failure in the other direction.
        foreach (var emitter in Supported)
        {
            var row = EmitterCoverage.For(emitter);
            Assert.NotNull(row);
            Assert.DoesNotContain("UNVERIFIED", row!.Note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CursorClaimsNoSignalItCannotDemonstrate()
    {
        // Its export sends metrics and logs and no traces, and nothing else has ever been
        // observed. Any row here claiming Full support for anything would be a claim with no
        // captured payload behind it — which is what ADR-002 is about.
        var cursor = EmitterCoverage.For(EmitterKind.Cursor)!;

        Assert.Equal(SignalSupport.None, cursor.Traces);
        Assert.Equal(SignalSupport.None, cursor.EditDecisions);
        Assert.Equal(SignalSupport.None, cursor.Feedback);
        Assert.Equal(SignalSupport.None, cursor.TimeToFirstToken);
        foreach (var support in new[] { cursor.Traces, cursor.Metrics, cursor.Events,
                                        cursor.EditDecisions, cursor.EditSurvival,
                                        cursor.Feedback, cursor.TimeToFirstToken })
            Assert.NotEqual(SignalSupport.Full, support);
    }

    [Fact]
    public void TheDecisionIsRecordedWhereTheIssueAsksForIt()
    {
        // "A recorded decision (A or B) in docs/ or an ADR" — the acceptance criterion. A
        // decision that lives only in a merged pull request is a decision nobody can find.
        var adr = RepoFile("docs/architecture/ADR-002-cursor-support.md");

        Assert.Contains("Status: **Accepted**", adr, StringComparison.Ordinal);
        Assert.Contains("demote", adr, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ positioning

    [Fact]
    public void TheStrategyNoLongerClaimsAnEmptyCategory()
    {
        // "Nobody does this" is refuted by a two-minute search since DX, Datadog and New Relic
        // shipped the category in mid-2026. A strategy document kept in the repo *on purpose*,
        // as proof of published reasoning, is the first thing a skeptical reader opens — so a
        // claim that fails there discredits everything else on the page (ADR-003).
        var strategy = RepoFile("docs/STRATEGY.md");

        Assert.DoesNotContain("the only open-source tool that turns telemetry", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("the first is empty and defensible", strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("Nobody in open source scores", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStrategyNamesTheCompetitorsItIsPositionedAgainst()
    {
        var strategy = RepoFile("docs/STRATEGY.md");

        foreach (var entrant in new[] { "DX Agent Experience", "Datadog Agent Console",
                                        "New Relic AI Coding Observability" })
            Assert.Contains(entrant, strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettledStandardClaimIsCorrected()
    {
        // Semantic conventions v1.42.0 deprecated gen_ai.* into a separate repository with no
        // stable release. The standard split rather than settled, and the honest version — a
        // canary that reports drift — is checkable in a way the original claim never was.
        var strategy = RepoFile("docs/STRATEGY.md");

        // The old phrase is still on the page — quoted, inside the correction that retracts it.
        // Retracting a claim by name is better than deleting it: a reader who saw the original
        // gets told it was wrong rather than finding it silently gone. So the assertion is that
        // the correction is present, not that the words are absent.
        Assert.Contains("v1.42.0", strategy, StringComparison.Ordinal);
        Assert.Contains("did not settle; it split", strategy, StringComparison.Ordinal);
        Assert.Contains("semantic-conventions-genai", strategy, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComparisonPageIsPublishedAndLinkedFromTheReadme()
    {
        Assert.Contains("docs/COMPARISON.md", RepoFile("README.md"), StringComparison.Ordinal);
        Assert.Contains("CopilotScope vs.", RepoFile("docs/COMPARISON.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheComparisonPageSaysWhereTheCompetitorsAreBetter()
    {
        // The guard against this page decaying into marketing. A comparison written by one
        // side that only flatters that side is worth nothing to the reader it is aimed at, and
        // this project's whole argument is that it tells you the uncomfortable half.
        var comparison = RepoFile("docs/COMPARISON.md");

        Assert.Contains("simply better", comparison, StringComparison.OrdinalIgnoreCase);
        // And that it keeps admitting the thing that is genuinely unresolved.
        Assert.Contains("not calibrated", comparison, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheInAppDocsNoLongerTellUsersToGuessAtCursorSetup()
    {
        // The page used to say "try adding the same VS Code settings … if Cursor exposes an
        // OTLP env-var hook". That is an instruction to guess, published as documentation.
        var docs = RepoFile("src/CopilotScope.Dashboard/Components/Pages/Docs.razor");

        Assert.DoesNotContain("try adding the same VS Code settings", docs, StringComparison.Ordinal);
        Assert.Contains("Not a supported assistant", docs, StringComparison.Ordinal);
    }
}
