using CopilotScope.Collector.Domain;

namespace CopilotScope.Collector.Quality;

/// <summary>One metric row inside an insight report.</summary>
public sealed record InsightMetric(string Label, string Value);

/// <summary>
/// Uniform output of every analyzer: a name, a status, an optional 0–1 score,
/// metric rows and human-readable findings. The dashboard renders these
/// generically, so adding a new algorithm is one class + one registration —
/// no UI work.
/// </summary>
public sealed record InsightReport(
    string Name,
    string Algorithm,
    string Status,          // "ok" | "no-data"
    double? Score,          // 0–1 when the analyzer produces a headline number
    List<InsightMetric> Metrics,
    List<string> Findings);

public interface IInsightAnalyzer
{
    InsightReport Analyze(CopilotSession session);

    /// <summary>
    /// False when configuration has switched this analyzer off. Defaulted to true, so the
    /// analyzers that are always safe to run stay one class and one registration.
    ///
    /// Gating here rather than at registration is deliberate: options bound from the built
    /// container's IConfiguration are the only ones that see sources a host wrapper adds
    /// after CreateBuilder, and an analyzer that silently ran because a flag was read too
    /// early is the failure mode worth designing out — this one produces a report about
    /// people from their own prompt text.
    /// </summary>
    bool Enabled => true;
}

/// <summary>Runs every registered analyzer against a session.</summary>
public sealed class InsightPipeline(IEnumerable<IInsightAnalyzer> analyzers)
{
    private readonly List<IInsightAnalyzer> _analyzers = analyzers.ToList();

    public List<InsightReport> Analyze(CopilotSession session)
    {
        var reports = new List<InsightReport>(_analyzers.Count);
        foreach (var analyzer in _analyzers)
        {
            // A disabled analyzer produces nothing at all — not an empty report, not a
            // "disabled" placeholder. The dashboard renders these generically, so a
            // placeholder would still put the feature's name on screen in a deployment
            // whose works agreement does not mention it.
            if (!analyzer.Enabled) continue;

            try { reports.Add(analyzer.Analyze(session)); }
            catch (Exception ex)
            {
                reports.Add(new InsightReport(analyzer.GetType().Name, "-", "no-data", null, [],
                    [$"Analyzer failed: {ex.Message}"]));
            }
        }
        return reports;
    }
}
