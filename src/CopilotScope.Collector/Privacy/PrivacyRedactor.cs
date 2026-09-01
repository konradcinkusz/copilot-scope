using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Otlp;

namespace CopilotScope.Collector.Privacy;

/// <summary>
/// Redacts a decoded OTLP batch before anything aggregates it.
///
/// Redacting at ingest rather than at render is the difference between a privacy control
/// and a privacy setting: nothing downstream — the session store, the write-behind
/// snapshot, the Prometheus exporter, a future analyzer nobody has written yet — ever sees
/// the identifying value, so no future feature can leak what was never kept. It also means
/// the guarantee survives a database dump, which is the artefact a DPO actually asks about.
///
/// Two things happen here, and only these two:
///   1. Identifying attributes are replaced by stable tokens. Equality survives, so session
///      correlation and the k-anonymity subject count still work; the value does not.
///   2. Prompt and response content is dropped outright. There is no token for it — it is
///      not an identifier to be pseudonymized, it is the conversation, and privacy mode's
///      promise is that the collector does not hold it.
/// </summary>
public sealed class PrivacyRedactor(PrivacyOptions options, Pseudonymizer pseudonymizer)
{
    /// <summary>
    /// Resource and attribute keys that name a person or the machine they sit at.
    ///
    /// Host-shaped keys are in here because a hostname at most companies is a person
    /// ("konrad-macbook"), and because host attributes are what SessionStore's fingerprint
    /// uses to tell two developers apart — a token preserves that, a deletion would merge
    /// the whole team into one bucket.
    /// </summary>
    public static readonly IReadOnlySet<string> IdentifyingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // Machine / process identity
        "host.name", "host.id", "host.ip", "host.mac",
        "container.id", "container.name",
        "k8s.pod.name", "k8s.node.name",
        "service.instance.id",
        "process.pid", "process.owner", "process.command_line", "process.executable.path",
        "device.id", "client.address", "network.peer.address",

        // Person identity
        "enduser.id", "enduser.name", "user.id", "user.name", "user.email", "user.full_name",
        "os.user", "session.user", "account.uuid",
        // Claude Code / Cowork carry these on the resource when the account is signed in.
        "claude_code.user.id", "claude_code.user.email", "claude_code.user.account_uuid",
        "claude_code.organization.id", "organization.id", "organization.uuid",
        // GitHub Copilot's per-user attribution.
        "github.copilot.user", "github.user.login", "copilot_chat.user",
    };

    /// <summary>
    /// Attribute keys carrying prompt or response text. Dropped, never tokenized: a token
    /// for a prompt is meaningless, and keeping the text is exactly what the works
    /// agreement forbids. Covers the GenAI conventions, their legacy spellings, and Claude
    /// Code's own <c>prompt</c>/<c>response</c> event attributes (OTEL_LOG_USER_PROMPTS).
    /// </summary>
    public static readonly IReadOnlySet<string> ContentKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        Sem.InputMessages, Sem.OutputMessages, Sem.Prompt, Sem.Completion,
        "gen_ai.input", "gen_ai.output",
        "gen_ai.system_instructions",
        "prompt", "response", "completion", "message", "content",
    };

    /// <summary>Branch attribute spellings, raw and normalized.</summary>
    private static readonly IReadOnlySet<string> BranchKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        Sem.GitBranch, "copilot_chat.git.branch", "vcs.ref.head.name", "git.branch",
    };

    /// <summary>Log events whose <c>Body</c> carries conversation text rather than a message.
    /// Mirrors the event names SessionStore reads a transcript out of.</summary>
    private static readonly IReadOnlySet<string> ContentEventNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "gen_ai.content.prompt", "gen_ai.content.completion",
        "gen_ai.user.message", "gen_ai.assistant.message", "gen_ai.choice",
    };

    private readonly HashSet<string> _identifying =
        new(IdentifyingKeys.Concat(options.ExtraIdentifyingAttributes.Where(k => !string.IsNullOrWhiteSpace(k))),
            StringComparer.Ordinal);

    /// <summary>How many content-bearing attributes have been dropped since startup. Surfaced
    /// on /api/privacy so an operator can show that the control is live, not merely configured.</summary>
    public long ContentDropped => Interlocked.Read(ref _contentDropped);
    private long _contentDropped;

    /// <summary>How many attribute values have been replaced by tokens since startup.</summary>
    public long AttributesPseudonymized => Interlocked.Read(ref _pseudonymized);
    private long _pseudonymized;

    /// <summary>Rewrites the batch in place. No-op when privacy mode is off.</summary>
    public void Apply(OtlpBatch batch)
    {
        if (!options.Enabled) return;

        foreach (var span in batch.Spans)
        {
            Scrub(span.Resource);
            Scrub(span.Attributes);
        }
        foreach (var point in batch.Metrics)
        {
            Scrub(point.Resource);
            Scrub(point.Attributes);
        }
        foreach (var log in batch.Logs)
        {
            Scrub(log.Resource);
            Scrub(log.Attributes);
            // On the GenAI content events the body is the prompt or the completion —
            // SessionStore reads it as such — so dropping only the attributes would leave
            // the transcript path open for exactly the emitters that use log bodies.
            if (ContentEventNames.Contains(log.EventName ?? "") && log.Body is not null)
            {
                log.Body = null;
                Interlocked.Increment(ref _contentDropped);
            }
        }
    }

    private void Scrub(Dictionary<string, AttrValue> attributes)
    {
        if (attributes.Count == 0) return;

        // Materialize the keys first: both branches below mutate the dictionary.
        foreach (var key in attributes.Keys.ToArray())
        {
            if (ContentKeys.Contains(key))
            {
                attributes.Remove(key);
                Interlocked.Increment(ref _contentDropped);
                continue;
            }

            if (options.PseudonymizeBranch && BranchKeys.Contains(key))
            {
                Tokenize(attributes, key, "branch");
                continue;
            }

            if (_identifying.Contains(key)) Tokenize(attributes, key, Prefix(key));
        }
    }

    private void Tokenize(Dictionary<string, AttrValue> attributes, string key, string prefix)
    {
        var current = attributes[key].ToString();
        if (current.Length == 0) return;
        attributes[key] = AttrValue.Str(pseudonymizer.Token(prefix, current));
        Interlocked.Increment(ref _pseudonymized);
    }

    /// <summary>
    /// A readable prefix per identifier family. Not a security property — the token is
    /// non-reversible either way — but it keeps a redacted export legible: "host-a1b2…"
    /// says the column is a machine, which is what a reviewer needs to follow the data map.
    /// </summary>
    private static string Prefix(string key) =>
        key.StartsWith("host.", StringComparison.Ordinal) ? "host"
        : key.StartsWith("k8s.", StringComparison.Ordinal) || key.StartsWith("container.", StringComparison.Ordinal) ? "node"
        : key.StartsWith("process.", StringComparison.Ordinal) ? "proc"
        : key.Contains("organization", StringComparison.Ordinal) ? "org"
        : key.StartsWith("service.", StringComparison.Ordinal) ? "inst"
        : "user";
}
