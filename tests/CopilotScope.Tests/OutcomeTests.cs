using System.Text;
using System.Text.Json;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Outcomes;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// Outcome linkage: joining a session to the pull requests it produced, and reading those
/// outcomes off GitHub webhooks. The join is a heuristic on repository + branch + time, so
/// these tests pin both that it matches what it should and that it advertises how sure it is.
/// </summary>
public class OutcomeTests
{
    private static readonly DateTimeOffset SessionStart = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SessionEnd = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static CopilotSession Session(string? repository = "https://github.com/acme/widgets.git",
        string? branch = "feature/rate-limit") =>
        new()
        {
            Id = "conv-1",
            Repository = repository,
            Branch = branch,
            FirstSeen = SessionStart,
            LastSeen = SessionEnd
        };

    private static PullRequestOutcome Pr(int number = 7, string repository = "acme/widgets",
        string branch = "feature/rate-limit", DateTimeOffset? openedAt = null,
        DateTimeOffset? mergedAt = null, bool reverted = false) =>
        new(repository, number, branch, "Add a Redis rate limiter",
            openedAt ?? SessionEnd.AddMinutes(30), mergedAt, null, null, 120, 8, 3, reverted);

    // ---------------------------------------------------------------- normalization

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "acme/widgets")]
    [InlineData("https://github.com/acme/widgets", "acme/widgets")]
    [InlineData("git@github.com:acme/widgets.git", "acme/widgets")]
    [InlineData("ACME/Widgets", "acme/widgets")]
    [InlineData("https://git.internal.example.com/team/acme/widgets.git", "acme/widgets")]
    public void RepositoryIdentifiersNormalizeToOwnerRepo(string input, string expected) =>
        Assert.Equal(expected, OutcomeLinker.NormalizeRepository(input));

    [Fact]
    public void BlankRepositoryNormalizesToNull()
    {
        // Sessions from emitters that report no git context must not join to anything.
        Assert.Null(OutcomeLinker.NormalizeRepository(null));
        Assert.Null(OutcomeLinker.NormalizeRepository("   "));
    }

    // ---------------------------------------------------------------- the join

    [Fact]
    public void BranchMatchInsideTheWindowIsHighConfidence()
    {
        var link = Assert.Single(OutcomeLinker.Link(Session(), [Pr()]));
        Assert.Equal(LinkConfidence.High, link.Confidence);
        Assert.Contains("branch", link.Reason);
    }

    [Fact]
    public void BranchMatchLongAfterTheSessionIsOnlyMedium()
    {
        // Same branch, but opened a week later — plausibly a different piece of work.
        var link = Assert.Single(OutcomeLinker.Link(Session(), [Pr(openedAt: SessionEnd.AddDays(7))]));
        Assert.Equal(LinkConfidence.Medium, link.Confidence);
    }

    [Fact]
    public void DifferentBranchInTheSameRepoIsLowConfidence()
    {
        var link = Assert.Single(OutcomeLinker.Link(Session(), [Pr(branch: "feature/unrelated")]));
        Assert.Equal(LinkConfidence.Low, link.Confidence);
    }

    [Fact]
    public void PullRequestsFromOtherRepositoriesAreNotLinked() =>
        Assert.Empty(OutcomeLinker.Link(Session(), [Pr(repository: "acme/other")]));

    [Fact]
    public void SessionsWithoutARepositoryLinkToNothing() =>
        Assert.Empty(OutcomeLinker.Link(Session(repository: null), [Pr()]));

    [Fact]
    public void APullRequestOpenedJustBeforeTheLastTelemetryStillCounts()
    {
        // A developer opens the PR and keeps talking to the assistant about it.
        var link = Assert.Single(OutcomeLinker.Link(Session(), [Pr(openedAt: SessionStart.AddMinutes(30))]));
        Assert.Equal(LinkConfidence.High, link.Confidence);
    }

    [Fact]
    public void BestConfidenceRanksFirst()
    {
        var links = OutcomeLinker.Link(Session(), [
            Pr(number: 1, branch: "feature/unrelated"),          // low
            Pr(number: 2, openedAt: SessionEnd.AddDays(7)),      // medium
            Pr(number: 3)                                        // high
        ]);

        Assert.Equal([3, 2, 1], links.Select(l => l.PullRequest.Number));
    }

    [Fact]
    public void OnlyMediumAndAboveCountAsEvidence()
    {
        // A repository-only guess in a correlation study would measure join errors, not
        // the relationship being tested.
        var links = OutcomeLinker.Link(Session(), [Pr(number: 1, branch: "other"), Pr(number: 2)]);
        var confident = OutcomeLinker.Confident(links).ToList();

        Assert.Equal(2, Assert.Single(confident).PullRequest.Number);
    }

    // ---------------------------------------------------------------- outcome state

    [Fact]
    public void StateReflectsWhatHappenedToTheChange()
    {
        Assert.Equal(PullRequestState.Open, Pr().State);
        Assert.Equal(PullRequestState.Merged, Pr(mergedAt: SessionEnd.AddHours(4)).State);
        // A revert outranks the merge — the change did not survive.
        Assert.Equal(PullRequestState.Reverted, Pr(mergedAt: SessionEnd.AddHours(4), reverted: true).State);
    }

    [Fact]
    public void TimeToMergeIsMeasuredFromOpening()
    {
        var pr = Pr(openedAt: SessionEnd, mergedAt: SessionEnd.AddHours(5));
        Assert.Equal(5, pr.TimeToMerge!.Value.TotalHours, precision: 3);
    }

    // ---------------------------------------------------------------- webhook

    private const string Secret = "hook-secret";

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void SignatureVerificationAcceptsAGenuineDelivery()
    {
        var body = Encoding.UTF8.GetBytes("""{"zen":"Design for failure."}""");
        var mac = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body);
        var signature = "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();

        Assert.True(GitHubWebhook.VerifySignature(body, signature, Secret));
    }

    [Fact]
    public void SignatureVerificationRejectsTamperingAndMissingHeaders()
    {
        // The endpoint writes into the data the score is about to be validated against,
        // so an unsigned or altered delivery must never be accepted.
        var body = Encoding.UTF8.GetBytes("""{"zen":"Design for failure."}""");
        var mac = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body);
        var signature = "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();

        Assert.False(GitHubWebhook.VerifySignature(body, null, Secret));
        Assert.False(GitHubWebhook.VerifySignature(body, "sha256=deadbeef", Secret));
        Assert.False(GitHubWebhook.VerifySignature(body, signature, "wrong-secret"));
        Assert.False(GitHubWebhook.VerifySignature(Encoding.UTF8.GetBytes("{}"), signature, Secret));
        Assert.False(GitHubWebhook.VerifySignature(body, signature, ""));
    }

    [Fact]
    public void MergedPullRequestEventIsParsed()
    {
        var outcome = GitHubWebhook.Parse("pull_request", Json("""
            {
              "repository": { "full_name": "acme/widgets" },
              "pull_request": {
                "number": 42,
                "title": "Add a Redis rate limiter",
                "head": { "ref": "feature/rate-limit" },
                "created_at": "2026-08-20T12:30:00Z",
                "merged_at": "2026-08-21T09:15:00Z",
                "closed_at": "2026-08-21T09:15:00Z",
                "additions": 120, "deletions": 8, "changed_files": 3
              }
            }
            """));

        Assert.NotNull(outcome);
        Assert.Equal("acme/widgets", outcome!.Repository);
        Assert.Equal(42, outcome.Number);
        Assert.Equal("feature/rate-limit", outcome.Branch);
        Assert.Equal(PullRequestState.Merged, outcome.State);
        Assert.Equal(120, outcome.Additions);
    }

    [Fact]
    public void ReviewEventCarriesTheReviewTimestampOnly()
    {
        var outcome = GitHubWebhook.Parse("pull_request_review", Json("""
            {
              "repository": { "full_name": "acme/widgets" },
              "review": { "submitted_at": "2026-08-21T08:00:00Z", "state": "approved" },
              "pull_request": {
                "number": 42,
                "title": "Add a Redis rate limiter",
                "head": { "ref": "feature/rate-limit" },
                "created_at": "2026-08-20T12:30:00Z"
              }
            }
            """));

        Assert.NotNull(outcome);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero), outcome!.FirstReviewAt);
        Assert.Equal(19.5, outcome.TimeToFirstReview!.Value.TotalHours, precision: 3);
    }

    [Fact]
    public void UnrelatedEventsAreIgnored() =>
        Assert.Null(GitHubWebhook.Parse("issues", Json("""{"action":"opened"}""")));

    [Fact]
    public void RevertCommitsAreDetectedFromAPush()
    {
        var reverts = GitHubWebhook.ParseReverts(Json("""
            {
              "repository": { "full_name": "acme/widgets" },
              "commits": [
                { "message": "Add more tests", "timestamp": "2026-08-22T10:00:00Z" },
                { "message": "Revert \"Add a Redis rate limiter\" (#42)", "timestamp": "2026-08-22T11:00:00Z" }
              ]
            }
            """)).ToList();

        var revert = Assert.Single(reverts);
        Assert.Equal("acme/widgets", revert.Repository);
        Assert.Equal(42, revert.Number);
    }

    [Fact]
    public void ARevertWithNoPullRequestNumberIsSkipped()
    {
        // Without a "#123" there is nothing to attach the revert to; guessing would mark
        // an unrelated change as reverted.
        var reverts = GitHubWebhook.ParseReverts(Json("""
            {
              "repository": { "full_name": "acme/widgets" },
              "commits": [ { "message": "Revert the caching change", "timestamp": "2026-08-22T11:00:00Z" } ]
            }
            """));

        Assert.Empty(reverts);
    }

    [Fact]
    public void ARevertOfATitleContainingAHashStillFindsTheTrailingReference()
    {
        // "Revert \"fix: escape C# strings (#42)\"" — taking the FIRST '#' finds "C#" and
        // gives up, silently losing the revert. GitHub appends the reference at the end.
        var reverts = GitHubWebhook.ParseReverts(Json("""
            {
              "repository": { "full_name": "acme/widgets" },
              "commits": [
                { "message": "Revert \"fix: escape C# strings\" (#42)", "timestamp": "2026-08-22T11:00:00Z" }
              ]
            }
            """)).ToList();

        Assert.Equal(42, Assert.Single(reverts).Number);
    }
}
