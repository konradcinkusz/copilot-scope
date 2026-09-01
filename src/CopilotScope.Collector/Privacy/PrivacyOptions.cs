namespace CopilotScope.Collector.Privacy;

/// <summary>
/// Privacy mode, bound from <c>CopilotScope:Privacy</c>.
///
/// CopilotScope's "not for performance reviews" promise (README) was a documentation
/// convention: nothing in the code stopped an operator from reading one developer's
/// transcripts. That is not something a works council can agree to. German BetrVG
/// §87(1)(6) grants co-determination over any technical system *capable* of monitoring
/// employee performance — the capability triggers the process, so "we don't look at it"
/// is not an answer. A Betriebsvereinbarung needs controls that are enforced by the
/// system and documented, which is what this turns on.
///
/// Off by default: a laptop-local run watching your own sessions has no data subject to
/// protect but you, and forcing an aggregation floor on a single-developer install would
/// suppress every view it has.
/// </summary>
public sealed class PrivacyOptions
{
    /// <summary>Master switch. When on, every control below is enforced by the collector
    /// rather than by convention.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Secret for the pseudonymization HMAC. Must be configured and kept stable, or
    /// pseudonyms change on every restart and history stops correlating. Left empty, the
    /// collector generates an ephemeral one and says so loudly at startup — usable for a
    /// trial, useless for a deployment.
    /// </summary>
    public string Salt { get; set; } = "";

    /// <summary>
    /// The k in k-anonymity: the smallest number of distinct subjects a view may cover.
    /// Below it, an "aggregate" is a report about one person wearing a group's clothes.
    /// Five is the conventional floor in workforce analytics agreements.
    /// </summary>
    public int MinimumGroupSize { get; set; } = 5;

    /// <summary>
    /// Suppress the per-session detail view entirely. A single session is a group of one,
    /// so drilling into it is exactly the individual-level inspection the aggregation floor
    /// exists to prevent. On by default with privacy mode; an operator whose works
    /// agreement permits individual review can turn it off and keep the rest.
    /// </summary>
    public bool SuppressSessionDetail { get; set; } = true;

    /// <summary>Record who read what. On by default with privacy mode — an access log is
    /// what makes the other controls auditable rather than merely asserted.</summary>
    public bool AuditLog { get; set; } = true;

    /// <summary>How many audit entries to keep in memory when there is no Postgres to
    /// write them to. Bounded, because the log is on the read path of a long-lived process.</summary>
    public int AuditBufferSize { get; set; } = 5_000;

    /// <summary>
    /// Raw OTLP forwarding relays the payload as it arrived, before redaction — by design,
    /// since the HMAC-free byte copy is what makes it a faithful relay. In privacy mode that
    /// is a hole straight through every control here, so forwarding is refused unless the
    /// operator states that the upstream backend is in scope of the same agreement.
    /// </summary>
    public bool AllowRawForwarding { get; set; }

    /// <summary>
    /// Also pseudonymize the git branch. Off by default because branch names are how
    /// sessions link to pull-request outcomes (docs/ANALYSIS.md), and hashing them turns
    /// that off. Worth turning on wherever branch naming embeds people —
    /// <c>konrad/fix-login</c> identifies its author as surely as a username does.
    /// </summary>
    public bool PseudonymizeBranch { get; set; }

    /// <summary>Extra resource/attribute keys to pseudonymize, for emitters or in-house
    /// wrappers that carry identity somewhere this doesn't know to look.</summary>
    public List<string> ExtraIdentifyingAttributes { get; set; } = [];

    /// <summary>Human-readable summary for the startup banner and the /api/privacy report.</summary>
    public string Describe() => Enabled
        ? $"enforced (k={MinimumGroupSize}, transcripts off, detail {(SuppressSessionDetail ? "suppressed" : "allowed")})"
        : "off (documentation-only stance)";
}
