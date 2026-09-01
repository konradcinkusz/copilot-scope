namespace CopilotScope.Collector.Domain;

/// <summary>
/// What a session actually represents. VS Code Copilot Chat issues internal helper
/// calls (title generation, summarization) under their own gen_ai.conversation.id,
/// so one user-visible prompt can spawn several CopilotSession entries — only one
/// of which is the real conversation.
/// </summary>
/// <summary>
/// Where a session's data came from. A string rather than an enum: an importer for a format
/// nobody has written yet should be able to name itself without a collector release, and the
/// value is only ever displayed and compared.
/// </summary>
public static class SessionOrigin
{
    /// <summary>Received over OTLP from a live emitter. The default.</summary>
    public const string Otel = "otel";

    /// <summary>Reconstructed from an assistant's own local transcript files.</summary>
    public const string LogImport = "log-import";
}

public enum SessionKind
{
    UserChat,
    InternalTitleGeneration,
    InternalSummary,
    InternalHelper,
    Unattributed
}

/// <summary>
/// Best-effort classifier that tells internal Copilot helper calls (title generation,
/// summarization) apart from real user-visible chat sessions, using the same signals
/// VS Code's own prompts leave behind in captured transcript content.
/// </summary>
public static class SessionClassifier
{
    private const string TitlePrefix = "Please write a brief title for the following request";
    private const string SummaryPrefix = "Summarize the following content in a SINGLE sentence";

    /// <summary>Must be called while holding the session lock (i.e. inside Snapshot/Apply).</summary>
    public static SessionKind Classify(CopilotSession s)
    {
        foreach (var entry in s.Transcript)
        {
            var prompt = entry.Prompt?.TrimStart();
            if (prompt is null) continue;
            if (prompt.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase)) return SessionKind.InternalTitleGeneration;
            if (prompt.StartsWith(SummaryPrefix, StringComparison.OrdinalIgnoreCase)) return SessionKind.InternalSummary;
        }

        if (s.Id.StartsWith("unattributed", StringComparison.Ordinal)) return SessionKind.Unattributed;

        return SessionKind.UserChat;
    }

    public static bool IsInternal(SessionKind kind) => kind is
        SessionKind.InternalTitleGeneration or SessionKind.InternalSummary or SessionKind.InternalHelper;
}
