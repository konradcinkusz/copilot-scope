namespace CopilotScope.JudgeAgent.Domain;

/// <summary>One transcript turn as sent to the judge model. Deliberately narrower than the
/// Collector's own <c>TranscriptEntry</c> — the judge only needs the turn index, model and
/// prompt/response text, not the wall-clock timestamp.</summary>
public sealed record JudgeTranscriptTurn(int Turn, string Model, string? Prompt, string? Response);

public sealed record JudgeToolStat(string Name, int Calls, int Errors);

/// <summary>One of the session's already-computed local quality components (reliability,
/// acceptance, friction, latency, feedback, efficiency), passed to the judge as prior context —
/// never as ground truth it should simply agree with.</summary>
public sealed record JudgeLocalComponent(string Name, double Value, int Samples, string Detail);

/// <summary>The full per-session payload assembled for one judge call. Serialized to JSON and
/// sent as the user message described by JudgeSystemPromptTemplate.txt's "What you receive"
/// section — the system prompt carries the rubric, this carries the evidence.</summary>
public sealed record SessionJudgeContext(
    string SessionId,
    List<JudgeTranscriptTurn> Transcript,
    List<JudgeToolStat> Tools,
    Dictionary<string, int> ErrorTypes,
    List<JudgeLocalComponent> LocalComponents,
    List<string>? CompletionSignals = null,
    List<string>? RetrievalContext = null);
