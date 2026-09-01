using CopilotScope.Collector.Domain;

namespace CopilotScope.Seeder;

/// <summary>Orchestrates persona picks into the two seed profiles: a small deterministic
/// <c>quick</c> set for local first-run sanity checks, and a large multi-day <c>demo</c> set
/// — generated in daily chunks so the dashboard's 14-day charts show real day-to-day shape
/// instead of a flat line — for presentations.</summary>
public static class SessionGenerator
{
    private static readonly string[] QuickSlugs =
        ["showcase", "golden", "error-prone", "laggy", "rejected-edits", "repair-loop", "internal-title"];

    public static List<CopilotSession> BuildQuickSet(Random rng)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = new List<CopilotSession>();

        for (var i = 0; i < QuickSlugs.Length; i++)
        {
            var persona = PersonaCatalog.Get(QuickSlugs[i]);
            var id = $"seed-quick-{i:D2}-{persona.Slug}";
            sessions.Add(SessionFactory.Build(id, persona, now.AddMinutes(-i * 20), rng));
        }

        // The curated long conversations — real 30+ turn sessions the transcript view is built for.
        for (var i = 0; i < ScriptedSessionCatalog.All.Length; i++)
        {
            var script = ScriptedSessionCatalog.All[i];
            var startedHoursAgo = 2 + i * 3;
            sessions.Add(ScriptedSessionFactory.Build(
                $"seed-quick-chat-{script.Slug}", script, now.AddHours(-startedHoursAgo), rng));
        }

        return sessions;
    }

    public static List<CopilotSession> BuildDemoSet(Random rng, int days)
    {
        var sessions = new List<CopilotSession>();
        var today = DateTimeOffset.UtcNow.Date;
        var showcase = PersonaCatalog.Get("showcase");

        for (var day = 0; day < days; day++)
        {
            var dayStart = new DateTimeOffset(today.AddDays(-day), TimeSpan.Zero).AddHours(8);
            var chunkSize = rng.Next(4, 8); // 4-7 sessions in this day's chunk

            for (var i = 0; i < chunkSize; i++)
            {
                var persona = PersonaCatalog.PickWeighted(rng);
                var start = dayStart.AddHours(rng.Next(0, 10)).AddMinutes(rng.Next(0, 60));
                var id = $"seed-demo-{day:D2}-{i:D2}-{persona.Slug}";
                sessions.Add(SessionFactory.Build(id, persona, start, rng));
            }

            // Guarantee a headline 30+ turn "everything" session every few days, on top of any
            // the weighted picker happens to roll — presentations should never miss the one
            // session that demonstrates every panel at once.
            if (day % 4 == 0)
            {
                var start = dayStart.AddHours(rng.Next(0, 8)).AddMinutes(rng.Next(0, 60));
                sessions.Add(SessionFactory.Build($"seed-demo-{day:D2}-showcase", showcase, start, rng));
            }
        }

        // Sprinkle the curated long conversations across the most recent days so the demo always
        // includes real, coherent 30+ turn chats (not just statistically-shaped ones).
        for (var i = 0; i < ScriptedSessionCatalog.All.Length; i++)
        {
            var script = ScriptedSessionCatalog.All[i];
            var day = i % Math.Max(1, days);
            var dayStart = new DateTimeOffset(today.AddDays(-day), TimeSpan.Zero).AddHours(9);
            var start = dayStart.AddHours(rng.Next(0, 7)).AddMinutes(rng.Next(0, 60));
            sessions.Add(ScriptedSessionFactory.Build($"seed-demo-chat-{script.Slug}", script, start, rng));
        }

        return sessions;
    }
}
