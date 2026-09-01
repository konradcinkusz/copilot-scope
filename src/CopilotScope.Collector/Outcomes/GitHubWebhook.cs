using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotScope.Collector.Outcomes;

/// <summary>Settings for outcome ingestion, bound from <c>CopilotScope:Outcomes</c>.</summary>
public sealed class OutcomeOptions
{
    /// <summary>
    /// Shared secret configured on the GitHub webhook. Required: without it the endpoint
    /// would let anyone on the network write arbitrary "outcomes" into the very data the
    /// score is about to be validated against.
    /// </summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Outcome ingestion is on only once a secret is configured.</summary>
    public bool Enabled => !string.IsNullOrEmpty(WebhookSecret);
}

/// <summary>
/// Parses GitHub <c>pull_request</c>, <c>pull_request_review</c> and <c>push</c> webhook
/// deliveries into <see cref="PullRequestOutcome"/> records.
///
/// Only the fields needed to answer "did this change ship, and how hard was it to land"
/// are read. No author, no reviewer, no commit message beyond revert detection — the
/// outcome pillar must not become a per-developer record by the back door.
/// </summary>
public static class GitHubWebhook
{
    /// <summary>
    /// Verifies the <c>X-Hub-Signature-256</c> HMAC over the raw body. Constant-time, and
    /// the raw bytes are used rather than a re-serialization, because any reformatting
    /// changes the digest.
    /// </summary>
    public static bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        Span<byte> computed = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, computed);

        var expected = Convert.ToHexString(computed).ToLowerInvariant();
        var provided = signatureHeader[prefix.Length..].ToLowerInvariant();
        return provided.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(provided), Encoding.ASCII.GetBytes(expected));
    }

    /// <summary>
    /// Reads one delivery. Returns null for events that carry no outcome information —
    /// most deliveries are noise, and a webhook is cheaper to over-subscribe than to
    /// reconfigure.
    /// </summary>
    public static PullRequestOutcome? Parse(string eventName, JsonElement payload) => eventName switch
    {
        "pull_request" => ParsePullRequest(payload),
        "pull_request_review" => ParseReview(payload),
        _ => null
    };

    private static PullRequestOutcome? ParsePullRequest(JsonElement payload)
    {
        if (!payload.TryGetProperty("pull_request", out var pr)) return null;
        if (Repository(payload) is not { } repo) return null;
        if (!pr.TryGetProperty("number", out var number)) return null;

        return new PullRequestOutcome(
            Repository: repo,
            Number: number.GetInt32(),
            Branch: pr.TryGetProperty("head", out var head) && head.TryGetProperty("ref", out var branchRef)
                ? branchRef.GetString() ?? "" : "",
            Title: Str(pr, "title") ?? "",
            OpenedAt: Time(pr, "created_at") ?? DateTimeOffset.UnixEpoch,
            MergedAt: Time(pr, "merged_at"),
            ClosedAt: Time(pr, "closed_at"),
            FirstReviewAt: null,
            Additions: Int(pr, "additions"),
            Deletions: Int(pr, "deletions"),
            ChangedFiles: Int(pr, "changed_files"));
    }

    private static PullRequestOutcome? ParseReview(JsonElement payload)
    {
        if (!payload.TryGetProperty("pull_request", out var pr)) return null;
        if (!payload.TryGetProperty("review", out var review)) return null;
        if (Repository(payload) is not { } repo) return null;
        if (!pr.TryGetProperty("number", out var number)) return null;

        // Only the timestamp: time-to-first-review is a property of the change, and the
        // upsert keeps the earliest one seen. Who reviewed it is not recorded.
        return new PullRequestOutcome(
            Repository: repo,
            Number: number.GetInt32(),
            Branch: pr.TryGetProperty("head", out var head) && head.TryGetProperty("ref", out var branchRef)
                ? branchRef.GetString() ?? "" : "",
            Title: Str(pr, "title") ?? "",
            OpenedAt: Time(pr, "created_at") ?? DateTimeOffset.UnixEpoch,
            MergedAt: Time(pr, "merged_at"),
            ClosedAt: Time(pr, "closed_at"),
            FirstReviewAt: Time(review, "submitted_at"),
            Additions: Int(pr, "additions"),
            Deletions: Int(pr, "deletions"),
            ChangedFiles: Int(pr, "changed_files"));
    }

    /// <summary>
    /// Finds merges reverted by a later push. GitHub's revert UI and `git revert` both
    /// produce a message naming the reverted PR, which is the only reliable link back
    /// without cloning the repository.
    /// </summary>
    public static IEnumerable<(string Repository, int Number, DateTimeOffset At)> ParseReverts(JsonElement payload)
    {
        if (Repository(payload) is not { } repo) yield break;
        if (!payload.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array) yield break;

        foreach (var commit in commits.EnumerateArray())
        {
            var message = Str(commit, "message");
            if (message is null || !message.StartsWith("Revert", StringComparison.OrdinalIgnoreCase)) continue;
            if (ExtractPrNumber(message) is not { } number) continue;
            yield return (repo, number, Time(commit, "timestamp") ?? DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Pulls the "#123" out of a revert message.</summary>
    private static int? ExtractPrNumber(string message)
    {
        var hash = message.IndexOf('#');
        if (hash < 0) return null;
        var digits = message[(hash + 1)..].TakeWhile(char.IsDigit).ToArray();
        return digits.Length > 0 && int.TryParse(new string(digits), out var n) ? n : null;
    }

    private static string? Repository(JsonElement payload) =>
        payload.TryGetProperty("repository", out var repo)
            ? OutcomeLinker.NormalizeRepository(Str(repo, "full_name"))
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var parsed) ? parsed : null;
}
