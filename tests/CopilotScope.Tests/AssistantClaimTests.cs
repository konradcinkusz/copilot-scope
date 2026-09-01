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
