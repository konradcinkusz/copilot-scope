using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CopilotScope.Local.Capturing;

/// <summary>
/// Turns someone's chat history into a file that shows its shape and nothing else, for a parser
/// to be built against (ADR-004, decision 5: no parser without a captured real file).
///
/// Kept: the JSON structure, property names that read as identifiers, numbers, booleans, and
/// the values of a short list of structural fields (a record's type, a message's role, a model
/// id, a version) when they are short plain tokens. Changed, consistently across every file of
/// one capture, so references between records still line up:
///   - ids (UUIDs, <c>msg_…</c>-style ids, long hex) become other ids of the same form;
///   - timestamps move by one random offset, so every duration between them is exact;
///   - paths, URLs and e-mail addresses become numbered placeholders.
/// Every other string becomes <c>&lt;text:length&gt;</c>. A property name that is not an identifier —
/// a path or a URI used as a key — becomes a placeholder too.
/// </summary>
internal sealed partial class FixtureRedactor(TimeSpan shift, int? seed = null)
{
    private readonly Random _random = seed is { } s ? new Random(s) : new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _placeholderCounts = new(StringComparer.Ordinal);

    /// <summary>Fields whose values are kept when they are short plain tokens: they say what a
    /// record is, not what anyone wrote in it.</summary>
    private static readonly HashSet<string> StructuralFields = new(StringComparer.Ordinal)
    {
        "type", "kind", "role", "subtype", "level", "mode", "location", "agentMode",
        "model", "modelId", "modelFamily", "version", "schemaVersion",
        "stop_reason", "stopReason", "stop_sequence", "service_tier", "finish_reason", "finishReason",
        "userType", "permissionMode", "language", "languageId", "state", "status", "result", "vote"
    };

    /// <summary>Tool names are what a parser pairs calls and results by. Kept when they look like
    /// one: a known built-in, or a namespaced name — never a free-form word that could be a name.</summary>
    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal)
    {
        "Bash", "Read", "Write", "Edit", "MultiEdit", "Glob", "Grep", "LS", "Task", "Agent", "Skill", "WebFetch",
        "WebSearch", "TodoWrite", "NotebookEdit", "EnterPlanMode", "ExitPlanMode", "BashOutput", "KillShell",
        "ToolSearch", "AskUserQuestion", "SlashCommand", "TaskCreate", "TaskUpdate", "TaskGet", "TaskList",
        "TaskStop", "SendMessage", "Monitor", "EnterWorktree", "ExitWorktree", "ListMcpResourcesTool",
        "ReadMcpResourceTool"
    };

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$.\-]{0,79}$")]
    private static partial Regex IdentifierKey();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.:/+\-]{0,63}$")]
    private static partial Regex PlainToken();

    [GeneratedRegex(@"^(copilot_|vscode_)[A-Za-z0-9_\-]{1,80}$|^[a-z][a-z0-9]*(_[a-z0-9]+)+$")]
    private static partial Regex ToolName();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Uuid();

    // A digit or a capital in the part after the prefix: msg_01Xy…, toolu_…, req_… are ids;
    // in_progress and compact_boundary are words.
    [GeneratedRegex(@"^([a-z]{2,12}_)(?=[A-Za-z0-9]*[0-9A-Z])([A-Za-z0-9]{8,})$")]
    private static partial Regex PrefixedId();

    [GeneratedRegex(@"^[0-9a-fA-F]{16,}$")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")]
    private static partial Regex Email();

    public JsonNode? Redact(JsonNode? node, string? field = null) => node switch
    {
        JsonObject o => RedactObject(o),
        JsonArray a => new JsonArray(a.Select(item => Redact(item, field)).ToArray()),
        JsonValue v => RedactValue(v, field),
        _ => null
    };

    private JsonObject RedactObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source)
        {
            var name = Key(key);
            // Two keys that redact alike keep both values rather than one overwriting the other.
            while (result.ContainsKey(name)) name += "'";
            result[name] = Redact(value, key);
        }
        return result;
    }

    private string Key(string key)
    {
        if (IsId(key)) return Id(key);
        return IdentifierKey().IsMatch(key) && !Email().IsMatch(key) ? key : Placeholder("key", key);
    }

    private JsonNode? RedactValue(JsonValue value, string? field)
    {
        if (value.TryGetValue<string>(out var text)) return JsonValue.Create(RedactString(text, field));
        if (value.TryGetValue<long>(out var number) && ShiftedEpoch(number, field) is { } shifted) return JsonValue.Create(shifted);
        return value.DeepClone();
    }

    public string RedactString(string text, string? field)
    {
        if (text.Length == 0) return text;
        if (IsoTimestamp().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return Shift(at, text);
        // Structural first: modelId and languageId are named like ids and are not.
        if (field is not null && StructuralFields.Contains(field) && PlainToken().IsMatch(text) && !Uuid().IsMatch(text))
            return text;
        if (IsId(text) || IsIdField(field)) return Id(text);
        if (field == "name" && (KnownTools.Contains(text) || ToolName().IsMatch(text))) return text;
        // An MCP tool's name carries its server's, which can name something internal. Only
        // CopilotScope's own are kept: they are the ones a reader must recognise, to leave them
        // out of the score (Domain/SelfObservation.cs).
        if (field == "name" && text.StartsWith("mcp__", StringComparison.Ordinal))
            return text.StartsWith("mcp__copilotscope__", StringComparison.Ordinal) ? text : Placeholder("mcp-tool", text);
        if (LooksLikePath(text)) return Placeholder("path", text);
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" or "vscode-remote")
            return Placeholder("url", text);
        if (Email().IsMatch(text)) return Placeholder("email", text);
        return $"<text:{text.Length}>";
    }

    private static bool IsId(string text) =>
        Uuid().IsMatch(text) || PrefixedId().IsMatch(text) || LongHex().IsMatch(text);

    /// <summary>A field that holds an id, whatever the id looks like. Its values must stay
    /// distinct and keep matching wherever they recur — a parser pairs records by them, and
    /// deduplicates by them — so they are never collapsed into a length, as text is.</summary>
    private static bool IsIdField(string? field) =>
        field is not null && (field is "id" or "uuid" or "ids" || field.EndsWith("Id", StringComparison.Ordinal)
            || field.EndsWith("_id", StringComparison.Ordinal) || field.EndsWith("Uuid", StringComparison.Ordinal)
            || field.EndsWith("Ids", StringComparison.Ordinal));

    /// <summary>The same id in, the same stand-in out, of the same form, across every file.</summary>
    private string Id(string id)
    {
        if (_ids.TryGetValue(id, out var known)) return known;
        string replacement;
        if (Uuid().IsMatch(id)) replacement = NewGuid().ToString();
        else if (PrefixedId().Match(id) is { Success: true } prefixed)
            replacement = prefixed.Groups[1].Value + Characters(prefixed.Groups[2].Length, "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789");
        else if (LongHex().IsMatch(id)) replacement = Characters(id.Length, "0123456789abcdef");
        else replacement = Placeholder("id", id); // any other shape: numbered, so never two alike
        _ids[id] = replacement;
        return replacement;
    }

    private Guid NewGuid()
    {
        var bytes = new byte[16];
        _random.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant
        return new Guid(bytes);
    }

    private string Characters(int length, string alphabet) =>
        new(Enumerable.Range(0, length).Select(_ => alphabet[_random.Next(alphabet.Length)]).ToArray());

    private string Placeholder(string kind, string original)
    {
        var key = kind + "\n" + original;
        if (_placeholders.TryGetValue(key, out var placeholder)) return placeholder;
        var number = _placeholderCounts[kind] = _placeholderCounts.GetValueOrDefault(kind) + 1;
        return _placeholders[key] = $"<{kind}:{number}>";
    }

    private string Shift(DateTimeOffset at, string original)
    {
        var shifted = at + shift;
        var format = original.EndsWith('Z')
            ? (original.Contains('.') ? "yyyy-MM-dd'T'HH:mm:ss.fff'Z'" : "yyyy-MM-dd'T'HH:mm:ss'Z'")
            : "O";
        return original.EndsWith('Z')
            ? shifted.UtcDateTime.ToString(format, CultureInfo.InvariantCulture)
            : shifted.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>Epoch milliseconds anywhere, and epoch seconds under a field named for a time,
    /// move with the timestamps: a duration computed between the two kinds stays exact.</summary>
    private long? ShiftedEpoch(long number, string? field)
    {
        if (number is >= 1_400_000_000_000 and <= 2_100_000_000_000) return number + (long)shift.TotalMilliseconds;
        if (number is >= 1_400_000_000 and <= 2_100_000_000 && field is not null
            && (field.Contains("time", StringComparison.OrdinalIgnoreCase) || field.Contains("date", StringComparison.OrdinalIgnoreCase)
                || field.EndsWith("At", StringComparison.Ordinal)))
            return number + (long)shift.TotalSeconds;
        return null;
    }

    private static bool LooksLikePath(string text) =>
        text.StartsWith('/') || text.StartsWith('~') || text.StartsWith(@"\\", StringComparison.Ordinal)
        || (text.Length > 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/');
}
