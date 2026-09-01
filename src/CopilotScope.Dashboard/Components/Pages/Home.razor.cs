using System.Text.Json;
using CopilotScope.Dashboard.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace CopilotScope.Dashboard.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    [Inject] public required CollectorClient Collector { get; set; }
    [Inject] public required IJSRuntime JS { get; set; }
    [Inject] public required DashboardAuthOptions Auth { get; set; }

    /// <summary>Supplied by AddCascadingAuthenticationState; null-safe when auth is off.</summary>
    [CascadingParameter] public Task<AuthenticationState>? AuthState { get; set; }

    // Resolved once per render from the signed-in principal. With sign-in disabled both are
    // true, so a local run behaves exactly as it did before roles existed.
    private bool _canReadTranscripts = true;
    private bool _canDelete = true;

    /// <summary>Sessions held in the rail. History beyond this lives in Postgres and is
    /// reachable by id; the rail is a working set, not the archive.</summary>
    private const int RailPageSize = 200;

    private static readonly (string Label, int Days)[] Ranges =
        [("7d", 7), ("30d", 30), ("90d", 90), ("All", 0)];

    /// <summary>Session id from the /sessions/{id} route. Set once, on navigation, and then
    /// left alone: clicking a row afterwards changes the selection without a page load, which
    /// is what a rail is for.</summary>
    [Parameter] public string? SessionId { get; set; }

    private int _days;
    private string? _repository;
    private string? _emitter;
    private FacetsDto? _facets;

    private CohortQuery Cohort => new(_repository, _emitter);

    // ------------------------------------------------------------------ labelling
    // Off unless the collector says so. The controls are a write surface on a page that is
    // otherwise read-only, and most deployments are not running a labelling study.
    private RubricsDto? _rubrics;

    /// <summary>The rater's own handle. Free text and deliberately not tied to telemetry
    /// identity: it names who did the rating, which is what inter-rater agreement is computed
    /// over, and it is not the developer whose session is being rated.</summary>
    private string _rater = "";

    /// <summary>Rubric → the band this rater picked, or null for "skip". Skip is a real answer:
    /// "this session has no retrieval context to judge RAGAS on" is information, and forcing a
    /// number would be worse than recording nothing.</summary>
    private readonly Dictionary<string, int?> _labelDraft = new(StringComparer.Ordinal);
    private string _labelNote = "";
    private string? _labelStatus;

    private bool LabellingAvailable => _rubrics?.Enabled == true;

    private List<SessionSummaryDto>? _sessions;

    /// <summary>Total sessions matching the current query, which may exceed the rail page.
    /// Whether that history is durable is already on the health chip (Postgres / in-memory).</summary>
    private int _sessionTotal;

    /// <summary>What the collector's privacy mode enforces; null when it is off or unreachable.</summary>
    private PrivacyDto? _privacy;

    /// <summary>Set when the k-anonymity floor withheld the session list. Rendered instead of
    /// "no sessions yet" — an empty rail with no explanation reads as a broken collector.</summary>
    private string? _suppressed;

    private bool DetailSuppressed => _privacy?.SessionDetailSuppressed == true;
    // Stable display order for the rail. The collector returns sessions newest-first,
    // which reshuffles the rail under the cursor every 2 s poll while a session is
    // live. We freeze the order a user has seen — existing rows keep their position,
    // genuinely new sessions join at the top — while each row's data still refreshes.
    private List<string> _sessionOrder = [];
    private SessionDetailDto? _detail;
    private HealthDto? _health;
    private string? _selectedId;
    private bool _confirmDelete;
    private bool _showChat;
    private bool _showInternal;
    private bool _showAllTurns;
    private bool _chatWasOpen;
    private bool _repoNormalization = true;
    private string _filter = string.Empty;
    private ElementReference _chatScrollRef;
    private ElementReference _chatWindowRef;

    private enum ViewMode { Basic, Advanced, Full }
    // Basic is the default: the first screen answers "was it good, what do I fix" in a
    // few lines instead of opening on the full firehose. A returning user's saved
    // preference (localStorage) still wins once the circuit is live.
    private ViewMode _viewMode = ViewMode.Basic;

    /// <summary>Sessions after the rail's free-text filter (id, repo or branch, case-insensitive).</summary>
    private List<SessionSummaryDto> FilteredSessions =>
        _sessions is null ? []
        : string.IsNullOrWhiteSpace(_filter) ? _sessions
        : _sessions.Where(s =>
              s.Id.Contains(_filter, StringComparison.OrdinalIgnoreCase)
              || (s.Repository?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
              || (s.Branch?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))
          .ToList();

    private bool ShowTile(string key, SessionSummaryDto s) => _viewMode switch
    {
        ViewMode.Basic => false,
        ViewMode.Full  => key is not ("edits" or "thumbs" or "loc") || !IsCli(s) || HasEditorSignal(key, s),
        _ => key switch
        {
            "tokens_in"   => s.InputTokens > 0,
            "tokens_out"  => s.OutputTokens > 0,
            "cache_read"  => s.CacheReadTokens > 0,
            "net_compute" => s.InputTokens > 0,
            "ttft_p50"    => s.TtftP50Ms > 0,
            "ttft_p95"    => s.TtftP95Ms > 0,
            "llm_calls"   => s.ChatCalls > 0,
            "tool_calls"  => true,
            "turns"       => true,
            "edits"       => s.EditsAccepted + s.EditsRejected > 0,
            "thumbs"      => !IsCli(s) && s.ThumbsUp + s.ThumbsDown > 0,
            "loc"         => s.LinesAdded > 0 || s.LinesRemoved > 0,
            _             => true
        }
    };

    private bool ShowInsight(InsightReportDto r) => _viewMode switch
    {
        ViewMode.Basic    => false,
        ViewMode.Advanced => r.Status != "no-data",
        _                 => true
    };

    private readonly CancellationTokenSource _cts = new();

    /// <summary>Parses one transcript entry into renderable chat messages (prompt side then response side).</summary>
    private static IEnumerable<ChatMessage> Messages(TranscriptEntryDto entry)
    {
        foreach (var m in ChatMessageParser.Parse(entry.Prompt, "user")) yield return m;
        foreach (var m in ChatMessageParser.Parse(entry.Response, "assistant")) yield return m;
    }

    private static string RoleClass(string role) => role switch
    {
        "user" => "user",
        "assistant" or "model" => "assistant",
        "system" or "developer" => "system",
        "tool" or "function" => "tool",
        _ => "assistant"
    };

    protected override async Task OnInitializedAsync()
    {
        // Resolve the caller's permissions before the first render, so a viewer never sees
        // a transcript button flash into existence and then disappear.
        if (AuthState is not null)
        {
            var user = (await AuthState).User;
            _canReadTranscripts = Auth.CanReadTranscripts(user);
            _canDelete = Auth.CanDelete(user);
        }

        // Deployment configuration, not per-refresh state: read once so the UI can explain a
        // withheld view rather than rendering an empty one that looks like a failure.
        _privacy = await Collector.GetPrivacyAsync(_cts.Token);

        // A linked session may be older than the default window, so a deep link opens on the
        // full history rather than 404-ing inside a 30-day filter the visitor never chose.
        if (SessionId is { Length: > 0 } linked)
        {
            _selectedId = Uri.UnescapeDataString(linked);
            _days = 0;
        }

        _facets = await Collector.GetFacetsAsync(ct: _cts.Token);
        _rubrics = await Collector.GetRubricsAsync(_cts.Token);
        await RefreshAsync();
        _ = PollAsync(); // fire-and-forget refresh loop for the lifetime of the circuit
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // localStorage is only reachable once the circuit is live, so preferences load after
        // the first render rather than in OnInitializedAsync (which also runs during prerender).
        if (firstRender)
        {
            await LoadPrefsAsync();
            StateHasChanged();
        }

        if (_showChat && !_chatWasOpen)
        {
            _chatWasOpen = true;
            await JS.InvokeVoidAsync("scrollToBottom", _chatScrollRef);
            await JS.InvokeVoidAsync("focusElement", _chatWindowRef); // so Escape reaches the dialog
        }
        else if (!_showChat)
        {
            _chatWasOpen = false;
        }
    }

    private void OnChatKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") _showChat = false;
    }

    private sealed record Prefs(string ViewMode, bool ShowInternal, bool RepoNormalization);

    private static readonly JsonSerializerOptions PrefsJson = new(JsonSerializerDefaults.Web);

    private async Task LoadPrefsAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("scopePrefs.load");
            if (string.IsNullOrWhiteSpace(json)) return;

            var p = JsonSerializer.Deserialize<Prefs>(json, PrefsJson);
            if (p is null) return;

            if (Enum.TryParse<ViewMode>(p.ViewMode, out var mode)) _viewMode = mode;
            _repoNormalization = p.RepoNormalization;
            if (p.ShowInternal != _showInternal)
            {
                _showInternal = p.ShowInternal;
                await RefreshAsync();
            }
        }
        catch { /* corrupt or unavailable storage — keep defaults */ }
    }

    private async Task SavePrefsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new Prefs(_viewMode.ToString(), _showInternal, _repoNormalization), PrefsJson);
            await JS.InvokeVoidAsync("scopePrefs.save", json);
        }
        catch { /* storage unavailable — preferences just won't persist */ }
    }

    private async Task SetViewModeAsync(ViewMode mode)
    {
        _viewMode = mode;
        await SavePrefsAsync();
    }

    private async Task SetRepoNormalizationAsync(bool value)
    {
        _repoNormalization = value;
        await SavePrefsAsync();
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                await RefreshAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { /* circuit closed */ }
    }

    private async Task RefreshAsync()
    {
        try
        {
            _health = await Collector.GetHealthAsync(_cts.Token);
            // Explicit page size: the collector now serves history from Postgres, so an
            // unbounded request would pull a team's whole archive into the rail. The rail
            // shows the newest page; _sessionTotal reports what that page is a page of.
            var page = await Collector.GetSessionPageAsync(_showInternal,
                days: _days > 0 ? _days : null, limit: RailPageSize, cohort: Cohort, ct: _cts.Token);
            var fetched = page.Sessions;
            _sessionTotal = page.Total;
            _suppressed = page.SuppressedReason;

            // Freeze the rail order: keep known sessions where they are, prepend new
            // ones (server order = newest-first) at the top, drop ones that vanished.
            var byId = new Dictionary<string, SessionSummaryDto>(fetched.Count);
            foreach (var s in fetched) byId[s.Id] = s;
            var known = new HashSet<string>(_sessionOrder);
            var newIds = fetched.Where(s => !known.Contains(s.Id)).Select(s => s.Id);
            _sessionOrder = newIds.Concat(_sessionOrder.Where(byId.ContainsKey)).ToList();
            _sessions = _sessionOrder.Select(id => byId[id]).ToList();

            // Auto-focus the most recent session until the user picks one explicitly.
            _selectedId ??= _sessions.FirstOrDefault()?.Id;
            // Under privacy mode the collector refuses per-session detail; polling it every two
            // seconds would be a 403 loop that also fills the access log with refusals.
            if (_selectedId is not null && !DetailSuppressed)
                _detail = await Collector.GetSessionAsync(_selectedId, _cts.Token) ?? _detail;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _health = null; // collector unreachable — keep the last known data on screen
        }
    }

    private async Task SetRangeAsync(int days)
    {
        _days = days;
        // The rail is rebuilt from a different window, so its frozen order no longer applies.
        _sessionOrder.Clear();
        await RefreshAsync();
    }

    private async Task SetRepositoryAsync(string? value)
    {
        _repository = string.IsNullOrWhiteSpace(value) ? null : value;
        _sessionOrder.Clear();
        await RefreshAsync();
    }

    private async Task SetEmitterAsync(string? value)
    {
        _emitter = string.IsNullOrWhiteSpace(value) ? null : value;
        _sessionOrder.Clear();
        await RefreshAsync();
    }

    private async Task ToggleShowInternalAsync(bool value)
    {
        _showInternal = value;
        await SavePrefsAsync();
        await RefreshAsync();
    }

    private async Task SelectAsync(string id)
    {
        _selectedId = id;
        _confirmDelete = false;
        _showChat = false;
        _showAllTurns = false;
        _labelStatus = null;
        if (DetailSuppressed) { _detail = null; return; }
        _detail = await Collector.GetSessionAsync(id, _cts.Token);
        if (LabellingAvailable) await LoadLabelsAsync(id);
    }

    private async Task DeleteAsync()
    {
        // The control is hidden for viewers; re-check anyway so the destructive path is
        // never guarded by markup alone.
        if (_selectedId is null || !_canDelete) return;
        var deleted = await Collector.DeleteSessionAsync(_selectedId, _cts.Token);
        if (deleted)
        {
            _sessions?.RemoveAll(s => s.Id == _selectedId);
            _selectedId = null;
            _detail = null;
            _showChat = false;
        }
        _confirmDelete = false;
        await RefreshAsync();
    }

    /// <summary>Loads what this rater already recorded for the session, so the form opens on
    /// their previous judgment instead of blank.</summary>
    private async Task LoadLabelsAsync(string sessionId)
    {
        _labelDraft.Clear();
        _labelNote = "";
        var existing = await Collector.GetLabelsAsync(sessionId, _cts.Token);
        foreach (var label in existing.Where(l => l.Rater == _rater))
        {
            _labelDraft[label.Algorithm] = label.Level;
            if (!string.IsNullOrEmpty(label.Note)) _labelNote = label.Note;
        }
    }

    private void SetBand(string algorithm, int? level) => _labelDraft[algorithm] = level;

    private int? BandFor(string algorithm) =>
        _labelDraft.TryGetValue(algorithm, out var level) ? level : null;

    private bool HasAnswered(string algorithm) => _labelDraft.ContainsKey(algorithm);

    private async Task SaveLabelsAsync()
    {
        if (_selectedId is null || _rubrics is null) return;
        if (string.IsNullOrWhiteSpace(_rater))
        {
            _labelStatus = "Enter a rater handle first — agreement is computed between raters, " +
                           "so an unnamed label cannot be used.";
            return;
        }

        // Every rubric is submitted, answered or skipped. A rubric left out of the payload would
        // be indistinguishable from one the rater never reached, and the difference matters:
        // one is "no opinion", the other is "not finished".
        var labels = _rubrics.Rubrics
            .Where(r => HasAnswered(r.Algorithm))
            .Select(r => new SessionLabelDto(_selectedId, _rater.Trim(), r.Algorithm,
                BandFor(r.Algorithm), string.IsNullOrWhiteSpace(_labelNote) ? null : _labelNote.Trim(),
                DateTimeOffset.UtcNow))
            .ToList();

        if (labels.Count == 0)
        {
            _labelStatus = "Nothing to save — rate at least one rubric, or mark it skipped.";
            return;
        }

        _labelStatus = await Collector.SaveLabelsAsync(labels, _cts.Token)
            ? $"Saved {labels.Count} judgment(s) as “{_rater.Trim()}”."
            : "The collector rejected the labels — check that labelling is enabled.";
    }

    private static string KindLabel(SessionKind kind) => kind switch
    {
        SessionKind.InternalTitleGeneration => "title-gen",
        SessionKind.InternalSummary => "summary",
        SessionKind.InternalHelper => "internal",
        SessionKind.Unattributed => "unattributed",
        _ => ""
    };

    private static string SegClass(int i) => i switch
    {
        < 16 => "seg-low",
        < 28 => "seg-mid",
        < 34 => "seg-high",
        _ => "seg-top"
    };

    private static string FmtTokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.0") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.0") + "k",
        _ => n.ToString()
    };

    private static string Pct(double v) =>
        (v * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Traffic-light class for one quality component. "unknown" (no samples yet) is
    /// distinct from "bad" — it means the signal hasn't arrived, not that it's failing.</summary>
    private static string TrafficClass(QualityComponentDto c) => c.Samples switch
    {
        0 => "unknown",
        _ when c.Value >= 0.7 => "good",
        _ when c.Value >= 0.4 => "warn",
        _ => "bad"
    };

    /// <summary>The component doing the most damage to the composite score right now, ranked by
    /// weighted deficit (weight × shortfall) rather than raw value — a low-weight component at 0
    /// matters less than a high-weight one at 0.5. Ignores components with no samples: you can't
    /// blame a factor that hasn't reported in yet.</summary>
    private static QualityComponentDto? WorstComponent(QualityReportDto q) =>
        q.Components.Where(c => c.Samples > 0)
                     .OrderByDescending(c => c.Weight * (1 - c.Value))
                     .FirstOrDefault(c => c.Weight * (1 - c.Value) > 0.01);

    /// <summary>Coarse session lifecycle read purely off FirstSeen/LastSeen — no explicit "session
    /// closed" signal exists in the telemetry, so this is a heuristic, not a fact from the client.</summary>
    private static (string Label, string Css) SessionStatus(SessionSummaryDto s)
    {
        var now = DateTimeOffset.UtcNow;
        var sinceStart = now - s.FirstSeen;
        var sinceActivity = now - s.LastSeen;

        if (sinceActivity > TimeSpan.FromMinutes(15)) return ("Ended", "ended");
        if (sinceActivity > TimeSpan.FromMinutes(1)) return ("Idle", "idle");
        if (sinceStart < TimeSpan.FromSeconds(20)) return ("Just started", "new");
        return ("Active", "active");
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        // Clock skew between emitter and collector can put LastSeen slightly in the future;
        // "-207s ago" is never useful, so anything not yet in the past reads as "just now".
        return d <= TimeSpan.Zero ? "just now"
             : d.TotalSeconds < 60 ? $"{d.TotalSeconds:0}s ago"
             : d.TotalMinutes < 60 ? $"{d.TotalMinutes:0}m ago"
             : d.TotalHours   < 24 ? $"{d.TotalHours:0}h ago"
             : $"{d.TotalDays:0}d ago";
    }

    /// <summary>
    /// True when the visible sessions come from assistants whose scores rest on different
    /// evidence. Only then is the comparability caveat worth the space — shown on every list it
    /// would become furniture, and furniture is not read.
    /// </summary>
    private bool MixedAssistants =>
        EmitterCoverage.NeedsComparabilityCaveat(FilteredSessions.Select(x => x.EmitterKind));

    /// <summary>Elapsed hours in the units a reviewer thinks in — minutes, hours, then days.</summary>
    private static string FormatHours(double hours) =>
        hours < 1 ? $"{hours * 60:0}m"
        : hours < 48 ? $"{hours:0.#}h"
        : $"{hours / 24:0.#}d";

    /// <summary>Wall-clock span the session covered, from first to last telemetry.</summary>
    private static string Duration(SessionSummaryDto s)
    {
        var d = s.LastSeen - s.FirstSeen;
        if (d <= TimeSpan.Zero) return "—";
        return d.TotalMinutes < 1 ? $"{d.TotalSeconds:0}s"
             : d.TotalHours   < 1 ? $"{d.TotalMinutes:0}m"
             : $"{(int)d.TotalHours}h {d.Minutes}m";
    }

    /// <summary>Plain-language read of the score for the Basic view — the "so what" a number alone
    /// doesn't give you. Flags low confidence, because a grade off two data points isn't a verdict.</summary>
    private static string BasicVerdict(QualityReportDto q)
    {
        var verdict = q.Grade switch
        {
            "excellent" => "Smooth session — few errors, quick responses, little rework.",
            "good"      => "Healthy session with only minor friction.",
            "fair"      => "Usable, but friction was noticeable — worth a look at the weak factor below.",
            "poor"      => "Rough session: errors, retries or long waits took over.",
            _           => "This session struggled — most quality signals came back weak.",
        };
        return q.Confidence < 0.5
            ? verdict + " Confidence is low, so treat the score as provisional until more telemetry lands."
            : verdict;
    }

    /// <summary>Traffic-light dot for a grade label (Basic verdict line).</summary>
    private static string GradeDotClass(string grade) => grade switch
    {
        "excellent" or "good" => "good",
        "fair" => "warn",
        _ => "bad"
    };

    /// <summary>The component carrying the session — highest scoring one that actually reported in.
    /// Mirrors <see cref="WorstComponent"/> so Basic can show both sides of the story.</summary>
    private static QualityComponentDto? BestComponent(QualityReportDto q) =>
        q.Components.Where(c => c.Samples > 0)
                    .OrderByDescending(c => c.Value)
                    .FirstOrDefault(c => c.Value >= 0.7);

    // CLI-like = no editor signals (edit acceptance, thumbs, LOC) in telemetry
    private static bool IsCli(SessionSummaryDto s) =>
        s.EmitterKind is EmitterKind.CLI or EmitterKind.ClaudeCode or EmitterKind.Cursor or EmitterKind.Cowork;

    /// <summary>
    /// The Claude surfaces are CLI-shaped but do report edit decisions and lines of code
    /// (claude_code.tool_decision / claude_code.lines_of_code.count), so those two tiles are
    /// gated on the data actually being there rather than on the emitter.
    /// </summary>
    private static bool HasEditorSignal(string key, SessionSummaryDto s) => key switch
    {
        "edits" => s.EditsAccepted + s.EditsRejected > 0,
        "loc" => s.LinesAdded > 0 || s.LinesRemoved > 0,
        _ => false
    };

    private static string EmitterLabel(EmitterKind k) => k switch
    {
        EmitterKind.VSCode => "VS Code",
        EmitterKind.CLI => "Copilot CLI",
        EmitterKind.ClaudeCode => "Claude Code",
        EmitterKind.Cowork => "Cowork",
        EmitterKind.Cursor => "Cursor",
        _ => ""
    };

    private static (string Label, string Css) SessionMaturity(int turns) => turns switch
    {
        0 or 1 => ("Early", "maturity-early"),
        <= 4    => ("Growing", "maturity-growing"),
        _       => ("Established", "maturity-established")
    };

    /// <summary>Maps a model name to a short display label for the timeline.</summary>
    private static string ModelShortLabel(string? model)
    {
        if (model is null) return "?";
        if (model.Contains("opus",    StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (model.Contains("sonnet",  StringComparison.OrdinalIgnoreCase)) return "Sonnet";
        if (model.Contains("haiku",   StringComparison.OrdinalIgnoreCase)) return "Haiku";
        if (model.Contains("gpt-4",   StringComparison.OrdinalIgnoreCase)) return "GPT-4";
        if (model.Contains("gpt-3",   StringComparison.OrdinalIgnoreCase)) return "GPT-3";
        if (model.Contains("fable",   StringComparison.OrdinalIgnoreCase)) return "Fable";
        // Trim to first 8 chars for unknowns
        return model.Length > 8 ? model[..8] : model;
    }

    /// <summary>CSS class for a model segment in the timeline strip.</summary>
    private static string ModelSegCss(string? model)
    {
        if (model is null) return "model-unknown";
        if (model.Contains("opus",   StringComparison.OrdinalIgnoreCase)) return "model-opus";
        if (model.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return "model-sonnet";
        if (model.Contains("haiku",  StringComparison.OrdinalIgnoreCase)) return "model-haiku";
        if (model.Contains("gpt-4",  StringComparison.OrdinalIgnoreCase)) return "model-gpt4";
        if (model.Contains("fable",  StringComparison.OrdinalIgnoreCase)) return "model-fable";
        return "model-other";
    }

    /// <summary>CSS traffic-light class for an insight score (applied to the numeric label). Higher is better.</summary>
    private static string InsightTrafficClass(double? score) => score switch
    {
        >= 0.7 => "insight-traffic-good",
        >= 0.4 => "insight-traffic-warn",
        not null => "insight-traffic-bad",
        _ => ""
    };

    /// <summary>Returns the dot color name for an insight traffic light (feeds into dot-{class}).</summary>
    private static string InsightDotClass(double? score) => score switch
    {
        >= 0.7 => "good",
        >= 0.4 => "warn",
        not null => "bad",
        _ => "unknown"
    };

    /// <summary>Deterministic 0–7 index from repo name for the 8-hue accent palette. Returns -1 when repo is null.</summary>
    private static int RepoColorIndex(string? repo)
    {
        if (repo is null) return -1;
        var h = 0;
        foreach (var c in repo) h = (h * 31 + c) & 0x7fffffff;
        return h % 8;
    }

    /// <summary>Last path segment of a repo URL/path — the short human-readable name.</summary>
    private static string? RepoShortName(string? repo)
    {
        if (repo is null) return null;
        var s = repo.TrimEnd('/', '\\');
        var slash = s.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

    /// <summary>CSS class for quality score color — uses percentile rank when normalization is on and history is available, absolute grade otherwise.</summary>
    // Score colour is ALWAYS the absolute grade (the same bands the VU-meter and
    // /docs use), so a score can no longer read green-by-percentile while sitting on
    // a red-by-value bar. The relative/percentile context lives in the subtitle text
    // and the percentile bar instead, and the "Normalize by repo" toggle now only
    // controls whether that context is shown — never what a colour means.
    private static string AbsoluteGradeClass(QualityReportDto q) => $"grade-{q.Grade}";

    /// <summary>Subtitle line for quality score — shows percentile context when normalization is on and history is available.
    /// When <paramref name="repo"/> is non-null the peer pool is repo-scoped.</summary>
    private static string QualitySubtitle(QualityReportDto q, string? repo = null, bool normalize = true)
    {
        if (!normalize || q.PercentileRank is not { } pct || q.HistoryCount is not { } n || n < 3)
            return $"/ 100 · {q.Grade} · confidence {(q.Confidence * 100):0}%";

        var pctLabel = $"{(int)(pct * 100)}th percentile";
        var zLabel = q.ZScore is { } z ? $" ({(z >= 0 ? "+" : "")}{z:0.0}σ)" : "";
        var meanLabel = q.HistoryMean?.ToString("0.0") ?? "?";
        var context = repo is not null ? $"{n} repo sessions" : $"last {n} sessions";
        return $"/ 100 · {pctLabel}{zLabel} vs {context} (mean {meanLabel})";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
