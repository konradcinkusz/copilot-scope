using System.Security.Cryptography;
using System.Text;

namespace CopilotScope.Collector.Api;

/// <summary>What a credential is allowed to do.</summary>
public enum ApiScope
{
    /// <summary>Write telemetry: POST /v1/traces|metrics|logs. Nothing else.</summary>
    Ingest,

    /// <summary>Read the query API and /metrics — which includes captured transcripts.</summary>
    Read,

    /// <summary>Destructive and administrative: DELETE, /api/admin/seed. Implies Read.</summary>
    Admin
}

/// <summary>
/// Scoped API keys, bound from <c>CopilotScope:Keys</c>.
///
/// One shared secret used to authorize ingest, transcript reads, destructive deletes and
/// seeding alike means the credential handed to every developer's editor is the same one
/// that can wipe the team's history. Splitting it lets an emitter hold a key that can only
/// write, and lets any of them be rotated without re-keying the others.
/// </summary>
public sealed class ApiKeyOptions
{
    /// <summary>Keys that may write telemetry.</summary>
    public List<string> Ingest { get; set; } = [];

    /// <summary>Keys that may read the query API and /metrics.</summary>
    public List<string> Read { get; set; } = [];

    /// <summary>Keys that may delete and seed. Also grants read.</summary>
    public List<string> Admin { get; set; } = [];
}

/// <summary>
/// Resolves a request's credential to the scopes it holds.
///
/// Backwards compatibility is the load-bearing requirement: an existing deployment sets
/// only <c>CopilotScope:Ingest:ApiKey</c>, and that key must keep working everywhere it
/// worked before. It is therefore treated as an all-scopes key, and scoping only becomes
/// real once an operator populates <c>CopilotScope:Keys</c>. An empty configuration
/// altogether is dev/open mode, exactly as before.
/// </summary>
public sealed class ApiKeyRegistry
{
    private readonly List<(byte[] Key, ApiScope Scope)> _keys = [];

    public ApiKeyRegistry(string? legacyKey, ApiKeyOptions? scoped)
    {
        // The legacy single key grants everything — it did before this existed, and
        // silently narrowing it on upgrade would lock a running deployment out of itself.
        if (!string.IsNullOrEmpty(legacyKey))
            foreach (var scope in Enum.GetValues<ApiScope>())
                Add(legacyKey, scope);

        foreach (var k in scoped?.Ingest ?? []) Add(k, ApiScope.Ingest);
        foreach (var k in scoped?.Read ?? []) Add(k, ApiScope.Read);
        foreach (var k in scoped?.Admin ?? []) Add(k, ApiScope.Admin);
    }

    /// <summary>True when no key is configured at all: dev/open mode, everything allowed.</summary>
    public bool Open => _keys.Count == 0;

    /// <summary>True when at least one scoped key is configured (i.e. not just the legacy one).</summary>
    public bool Scoped { get; private set; }

    private void Add(string key, ApiScope scope)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _keys.Add((Encoding.UTF8.GetBytes(key), scope));
    }

    /// <summary>
    /// Does the request carry a credential holding <paramref name="required"/>?
    /// Every candidate is compared in constant time, and all candidates are checked even
    /// after a match, so neither the key's value nor which scope matched leaks through timing.
    /// </summary>
    public bool Authorized(HttpRequest request, ApiScope required)
    {
        if (Open) return true;

        var provided = request.Headers["x-api-key"].FirstOrDefault()
                    ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        if (string.IsNullOrEmpty(provided)) return false;

        var bytes = Encoding.UTF8.GetBytes(provided);
        var granted = false;
        foreach (var (key, scope) in _keys)
            if (CryptographicOperations.FixedTimeEquals(bytes, key) && Grants(scope, required))
                granted = true;
        return granted;
    }

    /// <summary>Admin is a superset of Read; Ingest is orthogonal — a write-only emitter key
    /// must not become a read key just because it is valid.</summary>
    private static bool Grants(ApiScope held, ApiScope required) =>
        held == required || (held == ApiScope.Admin && required == ApiScope.Read);

    /// <summary>One-line description for the startup banner.</summary>
    public string Describe() => Open
        ? "disabled (dev)"
        : Scoped
            ? "scoped keys (ingest / read / admin)"
            : "x-api-key required (single key, all scopes)";

    public static ApiKeyRegistry Build(string? legacyKey, ApiKeyOptions? scoped)
    {
        var registry = new ApiKeyRegistry(legacyKey, scoped);
        registry.Scoped = (scoped?.Ingest.Count ?? 0) + (scoped?.Read.Count ?? 0) + (scoped?.Admin.Count ?? 0) > 0;
        return registry;
    }
}
