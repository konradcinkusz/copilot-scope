namespace CopilotScope.Collector.Alerting;

/// <summary>
/// Outbound alerts and the weekly digest, bound from <c>CopilotScope:Alerts</c>.
///
/// <para>A dashboard that must be visited gets abandoned; an output that triggers a decision
/// gets renewed. The decision worth triggering here is a quality regression after a model bump
/// or an assistant rollout — and session scoring is the only thing that can raise it, because a
/// vendor usage dashboard cannot alert on quality it does not measure.</para>
///
/// <para>Off by default and outbound: this is the first thing in the collector that sends data
/// somewhere. That deserves an explicit decision, not a default — the payload leaves the
/// deployment's trust boundary, so an operator running privacy mode has to choose it knowingly.</para>
/// </summary>
public sealed class AlertOptions
{
    /// <summary>Master switch. Nothing is sent anywhere until this and a URL are set.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where to POST. HTTPS in anything but a local test — the payload names your
    /// repositories and your spend.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary><c>json</c> (a plain document) or <c>slack</c> (a <c>text</c> field Slack and
    /// most chat webhooks render). Anything else is treated as json.</summary>
    public string Format { get; set; } = "json";

    /// <summary>How often to evaluate. Hourly is frequent enough to catch a bad rollout the
    /// day it happens and slow enough that a noisy cohort cannot page anyone.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Length of the window compared against the one immediately before it.</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>Composite points a cohort's mean has to fall before it is a regression. Five
    /// points on a 0–100 composite is roughly the width of a grade band.</summary>
    public double ScoreDropPoints { get; set; } = 5;

    /// <summary>Sessions each window needs before its mean is treated as a measurement rather
    /// than as an anecdote with a decimal point.</summary>
    public int MinSessionsPerWindow { get; set; } = 10;

    /// <summary>
    /// Confidence drop that makes a score change unattributable to quality.
    ///
    /// The composite renormalizes over the components that have data, so a cohort that stops
    /// reporting feedback or edit decisions is measured differently, not worse. Alerting on
    /// that as a "quality regression" would send a team hunting a change that never happened —
    /// so a co-occurring confidence fall past this is reported as a changed measurement basis
    /// instead.
    /// </summary>
    public double ConfidenceDropTolerance { get; set; } = 0.15;

    /// <summary>How long the same cohort stays quiet after firing. Without it, an hourly check
    /// on a week-long window re-sends the same regression 168 times.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Send the weekly digest as well as regressions.</summary>
    public bool Digest { get; set; }

    /// <summary>Day the digest goes out (UTC). Monday by default — the artefact a lead
    /// forwards at the start of a week, rather than a dashboard link.</summary>
    public DayOfWeek DigestDay { get; set; } = DayOfWeek.Monday;

    /// <summary>Hour of that day, UTC.</summary>
    public int DigestHourUtc { get; set; } = 8;

    /// <summary>True when there is somewhere to send to and permission to send.</summary>
    public bool Active => Enabled && !string.IsNullOrWhiteSpace(WebhookUrl);

    public string Describe() => Active
        ? $"webhook ({(IsSlack ? "slack" : "json")}), regression drop >= {ScoreDropPoints} pts over {WindowDays}d" +
          (Digest ? $", weekly digest {DigestDay} {DigestHourUtc:00}:00 UTC" : "")
        : Enabled ? "enabled but no CopilotScope:Alerts:WebhookUrl set" : "off";

    public bool IsSlack => string.Equals(Format, "slack", StringComparison.OrdinalIgnoreCase);
}
