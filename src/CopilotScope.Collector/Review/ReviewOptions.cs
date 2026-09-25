namespace CopilotScope.Collector.Review;

/// <summary>
/// The review pack and its readiness rule, bound from <c>CopilotScope:Review</c>.
///
/// <para>The pack is the deterministic half of a session review: everything an outside reader —
/// the user's own coding assistant, most likely — needs in order to say where sessions went
/// wrong and what recurred, computed by the collector from scores it already holds. The pack
/// never contains prompt, response or tool-argument text, and it never names a person; what a
/// model does with it is that model's output, not this collector's.</para>
/// </summary>
public sealed class ReviewOptions
{
    /// <summary>
    /// Serve the review endpoints at all. On by default: the pack is built from the same data the
    /// cohort and digest views already serve, under the same aggregation floor and the same audit
    /// log. An operator whose works agreement does not mention a review switches it off here, and
    /// <c>GET /api/privacy</c> then reports it as off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Eligible sessions since the last review before the base is worth reviewing. A pattern has to
    /// recur before it is a pattern, and the contrasts in the pack need ten sessions on each side;
    /// twenty-five is the point at which a review has something to say rather than restating the
    /// dashboard.
    /// </summary>
    public int MinSessions { get; set; } = 25;

    /// <summary>
    /// How far back a review looks. Thirty days is the window the dashboard's percentile rank is
    /// computed over (<c>CopilotScope:History:BaselineDays</c>), so the pack's population is the one
    /// the reader has already been shown.
    /// </summary>
    public int WindowDays { get; set; } = 30;

    /// <summary>
    /// Most session rows the sessions tier carries. The rows exist so a reader can look up a cited
    /// session; a reader that wants every row wants the API.
    /// </summary>
    public int MaxSessionRows { get; set; } = 100;

    /// <summary>Best and worst sessions per stratum in the sessions tier.</summary>
    public int ExemplarsPerSide { get; set; } = 2;
}
