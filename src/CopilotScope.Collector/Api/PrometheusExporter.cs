using System.Globalization;
using System.Text;
using CopilotScope.Collector.Domain;
using CopilotScope.Collector.Quality;

namespace CopilotScope.Collector.Api;

/// <summary>
/// Knobs for the /metrics endpoint, bound from the <c>CopilotScope:Prometheus</c>
/// configuration section.
/// </summary>
public sealed class PrometheusOptions
{
    /// <summary>Serve /metrics at all. Disable to keep the surface closed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Emit one series per session (label <c>session</c>) on top of the aggregates.
    /// Off by default: session ids are unbounded, and one busy team would otherwise
    /// hand Prometheus a new time series for every conversation ever held.
    /// </summary>
    public bool PerSession { get; set; }

    /// <summary>Hard ceiling on per-session series; the most recently active win.</summary>
    public int MaxSessionSeries { get; set; } = 200;

    /// <summary>Cap on distinct <c>type</c> values emitted for error counters.</summary>
    public int MaxErrorTypes { get; set; } = 30;
}

/// <summary>
/// Renders the collector's derived signals in the Prometheus text exposition
/// format (version 0.0.4) — hand-written, so the collector keeps its single
/// NuGet dependency.
///
/// The point of difference: this exports the *computed quality*, not just usage.
/// Anyone can count tokens; <c>copilotscope_quality_*</c> carries the composite
/// score, its weighted components and edit survival into whatever Prometheus or
/// Grafana stack a team already runs.
///
/// Scores are exported as _sum/_count pairs rather than pre-averaged gauges, so
/// PromQL does the aggregation and a rollup over any label set stays correct.
/// </summary>
public sealed class PrometheusExporter(
    SessionStore store,
    QualityEngine quality,
    PricingOptions pricing,
    PrometheusOptions options)
{
    private static readonly string[] ComponentNames =
        ["reliability", "acceptance", "friction", "latency", "feedback", "efficiency"];

    public string Render()
    {
        var rows = Collect();
        var sb = new StringBuilder(8 * 1024);

        RenderSessionCounts(sb, rows);
        RenderQuality(sb, rows);
        RenderComponents(sb, rows);
        RenderSurvival(sb, rows);
        RenderTokens(sb, rows);
        RenderCalls(sb, rows);
        RenderEditsAndFeedback(sb, rows);
        RenderLatency(sb, rows);
        RenderCost(sb, rows);
        RenderErrorTypes(sb, rows);
        RenderIngestHealth(sb);

        if (options.PerSession) RenderPerSession(sb, rows);

        return sb.ToString();
    }

    /// <summary>
    /// Ingest-side attribution health. A non-zero value on a shared collector means
    /// identity-less signals arrived with no way to tell the sending machines apart,
    /// so they share one fingerprint scope — configure host.name on the emitters.
    /// </summary>
    private void RenderIngestHealth(StringBuilder sb)
    {
        Header(sb, "copilotscope_hostless_signals_total", "counter",
            "Identity-less signals fingerprinted without a host discriminator (see docs/TUTORIAL.md team mode).");
        Write(sb, "copilotscope_hostless_signals_total", store.HostlessSignals);
    }

    // ------------------------------------------------------------------ collect

    /// <summary>
    /// Flattened, lock-free view of one session. Everything the exporter needs is
    /// pulled inside a single <see cref="CopilotSession.Snapshot{T}"/> so rendering
    /// never touches a live aggregate.
    /// </summary>
    private sealed record Row(
        string Id,
        string Emitter,
        string Grade,
        DateTimeOffset LastSeen,
        double Score,
        double Confidence,
        IReadOnlyList<QualityComponent> Components,
        double? Survival,
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheCreationTokens,
        int ChatCalls,
        int ChatErrors,
        int ToolCalls,
        int ToolErrors,
        int Turns,
        int EditsAccepted,
        int EditsRejected,
        int ThumbsUp,
        int ThumbsDown,
        double TtftP50Ms,
        double TtftP95Ms,
        IReadOnlyList<(string Model, double Cost)> CostByModel,
        IReadOnlyList<(string Type, int Count)> ErrorTypes);

    private List<Row> Collect()
    {
        var rows = new List<Row>();

        foreach (var session in store.All)
        {
            // Internal Copilot helper calls (title generation, summarization) are
            // machine chatter, not developer sessions — same exclusion the session
            // list API applies, so /metrics and the dashboard agree on the denominator.
            if (SessionClassifier.IsInternal(session.Kind)) continue;

            var report = quality.Evaluate(session);

            var row = session.Snapshot(s =>
            {
                var costByModel = new List<(string, double)>();
                foreach (var (model, usage) in s.ModelUsage)
                {
                    var price = pricing.Resolve(model);
                    costByModel.Add((model,
                        usage.InputTokens / 1e6 * price.Input
                        + usage.OutputTokens / 1e6 * price.Output
                        + usage.CacheReadTokens / 1e6 * price.CacheRead));
                }

                double? survival = s.SurvivalScores.Count > 0 ? s.SurvivalScores.Average() : null;

                return new Row(
                    s.Id,
                    EmitterLabel(s.EmitterKind),
                    report.Grade,
                    s.LastSeen,
                    report.Score,
                    report.Confidence,
                    report.Components,
                    survival,
                    s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheCreationTokens,
                    s.ChatCalls, s.ChatErrors, s.ToolCalls, s.ToolErrors, s.Turns,
                    s.EditsAccepted, s.EditsRejected, s.ThumbsUp, s.ThumbsDown,
                    CopilotSession.Percentile(s.TtftMs, 0.50),
                    CopilotSession.Percentile(s.TtftMs, 0.95),
                    costByModel,
                    s.ErrorTypes.Select(kv => (kv.Key, kv.Value)).ToList());
            });

            rows.Add(row);
        }

        return rows;
    }

    private static string EmitterLabel(EmitterKind kind) => kind switch
    {
        EmitterKind.VSCode => "vscode",
        EmitterKind.CLI => "cli",
        EmitterKind.ClaudeCode => "claude_code",
        EmitterKind.Cowork => "cowork",
        EmitterKind.Cursor => "cursor",
        _ => "unknown"
    };

    // ------------------------------------------------------------------ sections

    private static void RenderSessionCounts(StringBuilder sb, List<Row> rows)
    {
        Header(sb, "copilotscope_sessions", "gauge", "Sessions currently held by the collector.");
        foreach (var group in rows.GroupBy(r => (r.Emitter, r.Grade)))
            Write(sb, "copilotscope_sessions", group.Count(),
                ("emitter", group.Key.Emitter), ("grade", group.Key.Grade));
    }

    private static void RenderQuality(StringBuilder sb, List<Row> rows)
    {
        Header(sb, "copilotscope_quality_score_sum", "gauge",
            "Sum of composite session quality scores (0-100). Divide by _count for the mean.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
            Write(sb, "copilotscope_quality_score_sum", g.Sum(r => r.Score), ("emitter", g.Key));

        Header(sb, "copilotscope_quality_score_count", "gauge",
            "Number of sessions contributing to copilotscope_quality_score_sum.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
            Write(sb, "copilotscope_quality_score_count", g.Count(), ("emitter", g.Key));

        Header(sb, "copilotscope_quality_confidence_sum", "gauge",
            "Sum of score confidence (0-1) = data coverage x sample ramp. Divide by quality_score_count.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
            Write(sb, "copilotscope_quality_confidence_sum", g.Sum(r => r.Confidence), ("emitter", g.Key));
    }

    private static void RenderComponents(StringBuilder sb, List<Row> rows)
    {
        // (emitter, component) -> the per-session values that actually carried data.
        var buckets = new List<(string Emitter, string Component, List<double> Values)>();
        foreach (var component in ComponentNames)
        {
            foreach (var g in rows.GroupBy(r => r.Emitter))
            {
                var values = g.Select(r => r.Components.FirstOrDefault(c => c.Name == component))
                    .Where(c => c is not null)
                    .Select(c => c!.Value)
                    .ToList();
                if (values.Count > 0) buckets.Add((g.Key, component, values));
            }
        }
        if (buckets.Count == 0) return;

        // The text format requires every sample of a metric family to be contiguous,
        // so _sum and _count are emitted as two passes rather than interleaved.
        Header(sb, "copilotscope_quality_component_sum", "gauge",
            "Sum of weighted quality component values (0-1). Divide by _count for the mean.");
        foreach (var (emitter, component, values) in buckets)
            Write(sb, "copilotscope_quality_component_sum", values.Sum(),
                ("emitter", emitter), ("component", component));

        Header(sb, "copilotscope_quality_component_count", "gauge",
            "Number of sessions contributing to each quality component.");
        foreach (var (emitter, component, values) in buckets)
            Write(sb, "copilotscope_quality_component_count", values.Count,
                ("emitter", emitter), ("component", component));
    }

    private static void RenderSurvival(StringBuilder sb, List<Row> rows)
    {
        var withSurvival = rows.Where(r => r.Survival is not null).ToList();
        Header(sb, "copilotscope_edit_survival_sum", "gauge",
            "Sum of per-session edit survival ratios (0-1): did accepted AI edits stay in the file.");
        foreach (var g in withSurvival.GroupBy(r => r.Emitter))
            Write(sb, "copilotscope_edit_survival_sum", g.Sum(r => r.Survival!.Value), ("emitter", g.Key));

        Header(sb, "copilotscope_edit_survival_count", "gauge",
            "Number of sessions with edit survival telemetry.");
        foreach (var g in withSurvival.GroupBy(r => r.Emitter))
            Write(sb, "copilotscope_edit_survival_count", g.Count(), ("emitter", g.Key));
    }

    private static void RenderTokens(StringBuilder sb, List<Row> rows)
    {
        Header(sb, "copilotscope_tokens_total", "counter", "Tokens observed, by direction and cache role.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
        {
            Write(sb, "copilotscope_tokens_total", g.Sum(r => r.InputTokens), ("emitter", g.Key), ("type", "input"));
            Write(sb, "copilotscope_tokens_total", g.Sum(r => r.OutputTokens), ("emitter", g.Key), ("type", "output"));
            Write(sb, "copilotscope_tokens_total", g.Sum(r => r.CacheReadTokens), ("emitter", g.Key), ("type", "cache_read"));
            Write(sb, "copilotscope_tokens_total", g.Sum(r => r.CacheCreationTokens), ("emitter", g.Key), ("type", "cache_creation"));
        }
    }

    private static void RenderCalls(StringBuilder sb, List<Row> rows)
    {
        Header(sb, "copilotscope_calls_total", "counter", "LLM and tool calls observed.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
        {
            Write(sb, "copilotscope_calls_total", g.Sum(r => r.ChatCalls), ("emitter", g.Key), ("kind", "chat"));
            Write(sb, "copilotscope_calls_total", g.Sum(r => r.ToolCalls), ("emitter", g.Key), ("kind", "tool"));
        }

        Header(sb, "copilotscope_call_errors_total", "counter", "Failed LLM and tool calls.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
        {
            Write(sb, "copilotscope_call_errors_total", g.Sum(r => r.ChatErrors), ("emitter", g.Key), ("kind", "chat"));
            Write(sb, "copilotscope_call_errors_total", g.Sum(r => r.ToolErrors), ("emitter", g.Key), ("kind", "tool"));
        }

        Header(sb, "copilotscope_turns_total", "counter", "Conversation turns (one invoke_agent trace each).");
        foreach (var g in rows.GroupBy(r => r.Emitter))
            Write(sb, "copilotscope_turns_total", g.Sum(r => r.Turns), ("emitter", g.Key));
    }

    private static void RenderEditsAndFeedback(StringBuilder sb, List<Row> rows)
    {
        Header(sb, "copilotscope_edits_total", "counter", "AI-proposed edits, by outcome.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
        {
            Write(sb, "copilotscope_edits_total", g.Sum(r => r.EditsAccepted), ("emitter", g.Key), ("outcome", "accepted"));
            Write(sb, "copilotscope_edits_total", g.Sum(r => r.EditsRejected), ("emitter", g.Key), ("outcome", "rejected"));
        }

        Header(sb, "copilotscope_feedback_total", "counter", "Explicit thumbs up/down votes.");
        foreach (var g in rows.GroupBy(r => r.Emitter))
        {
            Write(sb, "copilotscope_feedback_total", g.Sum(r => r.ThumbsUp), ("emitter", g.Key), ("vote", "up"));
            Write(sb, "copilotscope_feedback_total", g.Sum(r => r.ThumbsDown), ("emitter", g.Key), ("vote", "down"));
        }
    }

    private static void RenderLatency(StringBuilder sb, List<Row> rows)
    {
        var withTtft = rows.Where(r => r.TtftP50Ms > 0).ToList();
        if (withTtft.Count == 0) return;

        // Not a Prometheus summary: these are medians *of per-session percentiles*,
        // which is the number a human reads on the dashboard. Quantiles of quantiles
        // don't compose, so this is deliberately labelled as an aggregate, not a quantile.
        Header(sb, "copilotscope_ttft_seconds", "gauge",
            "Median across sessions of each session's time-to-first-token percentile.");
        foreach (var g in withTtft.GroupBy(r => r.Emitter))
        {
            Write(sb, "copilotscope_ttft_seconds",
                Median(g.Select(r => r.TtftP50Ms).ToList()) / 1000.0, ("emitter", g.Key), ("aggregate", "p50"));
            Write(sb, "copilotscope_ttft_seconds",
                Median(g.Select(r => r.TtftP95Ms).ToList()) / 1000.0, ("emitter", g.Key), ("aggregate", "p95"));
        }
    }

    private static void RenderCost(StringBuilder sb, List<Row> rows)
    {
        Header(sb, "copilotscope_cost_usd_total", "counter",
            "Estimated spend from the configurable price sheet (CopilotScope:Pricing). List-price estimate, not billing.");

        var byModel = rows
            .SelectMany(r => r.CostByModel.Select(c => (r.Emitter, c.Model, c.Cost)))
            .GroupBy(x => (x.Emitter, x.Model));

        foreach (var g in byModel)
            Write(sb, "copilotscope_cost_usd_total", g.Sum(x => x.Cost),
                ("emitter", g.Key.Emitter), ("model", g.Key.Model));
    }

    private void RenderErrorTypes(StringBuilder sb, List<Row> rows)
    {
        var byType = rows
            .SelectMany(r => r.ErrorTypes)
            .GroupBy(e => e.Type)
            .Select(g => (Type: g.Key, Count: g.Sum(e => e.Count)))
            .OrderByDescending(x => x.Count)
            .Take(Math.Max(0, options.MaxErrorTypes))
            .ToList();
        if (byType.Count == 0) return;

        Header(sb, "copilotscope_errors_by_type_total", "counter",
            $"Errors by reported error.type (top {options.MaxErrorTypes} by volume).");
        foreach (var (type, count) in byType)
            Write(sb, "copilotscope_errors_by_type_total", count, ("type", type));
    }

    private void RenderPerSession(StringBuilder sb, List<Row> rows)
    {
        // Opt-in and capped: session ids are unbounded, so the newest sessions win
        // and everything past the ceiling is simply not exported.
        var recent = rows
            .OrderByDescending(r => r.LastSeen)
            .Take(Math.Max(0, options.MaxSessionSeries))
            .ToList();
        if (recent.Count == 0) return;

        Header(sb, "copilotscope_session_quality_score", "gauge",
            "Composite quality score (0-100) for a single session. Opt-in, capped series.");
        foreach (var r in recent)
            Write(sb, "copilotscope_session_quality_score", r.Score,
                ("session", r.Id), ("emitter", r.Emitter), ("grade", r.Grade));

        Header(sb, "copilotscope_session_cost_usd", "gauge", "Estimated spend for a single session.");
        foreach (var r in recent)
            Write(sb, "copilotscope_session_cost_usd", r.CostByModel.Sum(c => c.Cost),
                ("session", r.Id), ("emitter", r.Emitter));

        Header(sb, "copilotscope_session_series_limit", "gauge",
            "Configured ceiling on per-session series (CopilotScope:Prometheus:MaxSessionSeries).");
        Write(sb, "copilotscope_session_series_limit", options.MaxSessionSeries);

        Header(sb, "copilotscope_session_series_dropped", "gauge",
            "Sessions not exported per-session because the ceiling was reached.");
        Write(sb, "copilotscope_session_series_dropped", Math.Max(0, rows.Count - recent.Count));
    }

    // ------------------------------------------------------------ text format

    private static void Header(StringBuilder sb, string name, string type, string help)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void Write(StringBuilder sb, string name, double value, params (string Key, string Value)[] labels)
    {
        sb.Append(name);
        if (labels.Length > 0)
        {
            sb.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(labels[i].Key).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
            }
            sb.Append('}');
        }
        sb.Append(' ').Append(Format(value)).Append('\n');
    }

    /// <summary>
    /// Prometheus wants Go-style float syntax: dots for decimals regardless of the
    /// host locale, and the literal tokens +Inf/-Inf/NaN.
    /// </summary>
    private static string Format(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>Label values escape backslash, double quote and newline (text format §"escaping").</summary>
    private static string EscapeLabel(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n");

    /// <summary>HELP text escapes backslash and newline only — quotes are literal there.</summary>
    private static string EscapeHelp(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\n", "\\n");

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}
