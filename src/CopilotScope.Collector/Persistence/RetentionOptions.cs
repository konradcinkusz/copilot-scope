namespace CopilotScope.Collector.Persistence;

/// <summary>
/// Knobs for history retention and the database-backed read path, bound from the
/// <c>CopilotScope:History</c> configuration section.
/// </summary>
public sealed class HistoryOptions
{
    /// <summary>
    /// Delete sessions last seen more than this many days ago. Zero (the default) keeps
    /// everything: a retention policy silently deleting a team's history would be a worse
    /// surprise than a growing table, so it only runs when an operator asks for it.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>How often the retention sweep runs. Daily is plenty for a day-granular policy.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Largest page /api/sessions will return, whatever the caller asks for.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>Default page size when the caller does not specify one.</summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>
    /// Window the percentile baseline is computed over. Fixed rather than "whatever is in
    /// memory", so a session's rank means the same thing from one day to the next.
    /// </summary>
    public int BaselineDays { get; set; } = 30;

    /// <summary>Ceiling on scores pulled for the baseline, so the query stays bounded.</summary>
    public int BaselineMaxSamples { get; set; } = 5_000;
}
