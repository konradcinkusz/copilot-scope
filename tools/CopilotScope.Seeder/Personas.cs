namespace CopilotScope.Seeder;

/// <summary>Generation parameters for one kind of Copilot session. Each persona maps to a
/// recognizable story on the dashboard (a clean run, an error storm, a laggy backend, a
/// a user stuck in a repair loop, an internal helper call CopilotScope should filter out, ...) so a seeded
/// dataset actually demonstrates what the quality engine and insight analyzers do.</summary>
public sealed record Persona(
    string Slug,
    int Weight,
    int MinTurns, int MaxTurns,
    long MinInputTokens, long MaxInputTokens,
    long MinOutputTokens, long MaxOutputTokens,
    double MinTtftMs, double MaxTtftMs,
    double ChatErrorRate,
    double ToolErrorRate,
    double EditAcceptRatio,
    double ThumbsUpRatio,
    double MinSurvivalNoRevert, double MaxSurvivalNoRevert,
    double MinSurvivalFourGram, double MaxSurvivalFourGram,
    bool RepairHeavy = false,
    string? InternalPromptPrefix = null,
    bool Showcase = false);

public static class PersonaCatalog
{
    public static readonly Persona[] All =
    [
        new(Slug: "golden", Weight: 4, MinTurns: 3, MaxTurns: 6,
            MinInputTokens: 1200, MaxInputTokens: 3000, MinOutputTokens: 200, MaxOutputTokens: 550,
            MinTtftMs: 250, MaxTtftMs: 700,
            ChatErrorRate: 0.0, ToolErrorRate: 0.02,
            EditAcceptRatio: 0.95, ThumbsUpRatio: 0.95,
            MinSurvivalNoRevert: 0.85, MaxSurvivalNoRevert: 1.0,
            MinSurvivalFourGram: 0.6, MaxSurvivalFourGram: 0.9),

        new(Slug: "balanced", Weight: 4, MinTurns: 3, MaxTurns: 6,
            MinInputTokens: 1000, MaxInputTokens: 2500, MinOutputTokens: 150, MaxOutputTokens: 450,
            MinTtftMs: 400, MaxTtftMs: 1200,
            ChatErrorRate: 0.05, ToolErrorRate: 0.08,
            EditAcceptRatio: 0.7, ThumbsUpRatio: 0.6,
            MinSurvivalNoRevert: 0.55, MaxSurvivalNoRevert: 0.85,
            MinSurvivalFourGram: 0.4, MaxSurvivalFourGram: 0.7),

        new(Slug: "error-prone", Weight: 3, MinTurns: 3, MaxTurns: 7,
            MinInputTokens: 1500, MaxInputTokens: 4000, MinOutputTokens: 100, MaxOutputTokens: 350,
            MinTtftMs: 600, MaxTtftMs: 1800,
            ChatErrorRate: 0.30, ToolErrorRate: 0.35,
            EditAcceptRatio: 0.40, ThumbsUpRatio: 0.20,
            MinSurvivalNoRevert: 0.25, MaxSurvivalNoRevert: 0.55,
            MinSurvivalFourGram: 0.2, MaxSurvivalFourGram: 0.5),

        new(Slug: "laggy", Weight: 2, MinTurns: 3, MaxTurns: 5,
            MinInputTokens: 1200, MaxInputTokens: 2800, MinOutputTokens: 200, MaxOutputTokens: 500,
            // Deliberately all above the analyzer's 8s abandonment threshold, so this persona
            // reliably demonstrates the "abandonment risk" finding on every run, not just some.
            MinTtftMs: 8200, MaxTtftMs: 9800,
            ChatErrorRate: 0.05, ToolErrorRate: 0.05,
            EditAcceptRatio: 0.7, ThumbsUpRatio: 0.4,
            MinSurvivalNoRevert: 0.5, MaxSurvivalNoRevert: 0.8,
            MinSurvivalFourGram: 0.4, MaxSurvivalFourGram: 0.7),

        new(Slug: "rejected-edits", Weight: 2, MinTurns: 4, MaxTurns: 8,
            MinInputTokens: 1500, MaxInputTokens: 3500, MinOutputTokens: 250, MaxOutputTokens: 600,
            MinTtftMs: 500, MaxTtftMs: 1400,
            ChatErrorRate: 0.05, ToolErrorRate: 0.05,
            EditAcceptRatio: 0.15, ThumbsUpRatio: 0.30,
            MinSurvivalNoRevert: 0.3, MaxSurvivalNoRevert: 0.6,
            MinSurvivalFourGram: 0.2, MaxSurvivalFourGram: 0.5),

        new(Slug: "repair-loop", Weight: 2, MinTurns: 4, MaxTurns: 6,
            MinInputTokens: 1400, MaxInputTokens: 3200, MinOutputTokens: 150, MaxOutputTokens: 400,
            MinTtftMs: 800, MaxTtftMs: 2000,
            ChatErrorRate: 0.10, ToolErrorRate: 0.10,
            EditAcceptRatio: 0.35, ThumbsUpRatio: 0.15,
            MinSurvivalNoRevert: 0.15, MaxSurvivalNoRevert: 0.4,
            MinSurvivalFourGram: 0.1, MaxSurvivalFourGram: 0.35,
            RepairHeavy: true),

        new(Slug: "long-epic", Weight: 1, MinTurns: 14, MaxTurns: 26,
            MinInputTokens: 2500, MaxInputTokens: 6000, MinOutputTokens: 400, MaxOutputTokens: 900,
            MinTtftMs: 400, MaxTtftMs: 1500,
            ChatErrorRate: 0.08, ToolErrorRate: 0.10,
            EditAcceptRatio: 0.75, ThumbsUpRatio: 0.65,
            MinSurvivalNoRevert: 0.55, MaxSurvivalNoRevert: 0.85,
            MinSurvivalFourGram: 0.4, MaxSurvivalFourGram: 0.75),

        // The headline demo session: a single long chat (30–44 turns) engineered to exercise
        // every dashboard panel and every analyzer at once. SessionFactory gives it real
        // intra-session variety (per-turn moods), guaranteed editor signals (edits both
        // accepted and rejected, thumbs up and down, LOC ±), model switching, multi-role
        // captured content and a couple of repair turns. Token/survival ranges below are
        // the session-level envelope; per-turn latency and error rates come from ShowcaseMoods.
        new(Slug: "showcase", Weight: 1, MinTurns: 30, MaxTurns: 44,
            MinInputTokens: 1500, MaxInputTokens: 5200, MinOutputTokens: 250, MaxOutputTokens: 850,
            MinTtftMs: 300, MaxTtftMs: 1200, // overridden per-turn by ShowcaseMoods
            ChatErrorRate: 0.0, ToolErrorRate: 0.0, // overridden per-turn by ShowcaseMoods
            EditAcceptRatio: 0.62, ThumbsUpRatio: 0.6,
            MinSurvivalNoRevert: 0.35, MaxSurvivalNoRevert: 0.9,
            MinSurvivalFourGram: 0.25, MaxSurvivalFourGram: 0.8,
            Showcase: true),

        new(Slug: "internal-title", Weight: 2, MinTurns: 1, MaxTurns: 1,
            MinInputTokens: 150, MaxInputTokens: 400, MinOutputTokens: 10, MaxOutputTokens: 30,
            MinTtftMs: 150, MaxTtftMs: 400,
            ChatErrorRate: 0.0, ToolErrorRate: 0.0,
            EditAcceptRatio: 0.0, ThumbsUpRatio: 0.0,
            MinSurvivalNoRevert: 1.0, MaxSurvivalNoRevert: 1.0,
            MinSurvivalFourGram: 1.0, MaxSurvivalFourGram: 1.0,
            InternalPromptPrefix: "Please write a brief title for the following request:\n"),

        new(Slug: "internal-summary", Weight: 1, MinTurns: 1, MaxTurns: 1,
            MinInputTokens: 300, MaxInputTokens: 800, MinOutputTokens: 15, MaxOutputTokens: 40,
            MinTtftMs: 150, MaxTtftMs: 400,
            ChatErrorRate: 0.0, ToolErrorRate: 0.0,
            EditAcceptRatio: 0.0, ThumbsUpRatio: 0.0,
            MinSurvivalNoRevert: 1.0, MaxSurvivalNoRevert: 1.0,
            MinSurvivalFourGram: 1.0, MaxSurvivalFourGram: 1.0,
            InternalPromptPrefix: "Summarize the following content in a SINGLE sentence (under 10 words) using past tense: "),
    ];

    public static Persona Get(string slug) => All.First(p => p.Slug == slug);

    public static Persona PickWeighted(Random rng)
    {
        var total = All.Sum(p => p.Weight);
        var roll = rng.Next(total);
        foreach (var p in All)
        {
            if (roll < p.Weight) return p;
            roll -= p.Weight;
        }
        return All[^1];
    }
}
