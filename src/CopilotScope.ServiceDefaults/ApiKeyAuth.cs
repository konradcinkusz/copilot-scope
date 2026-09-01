using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace CopilotScope.ServiceDefaults;

/// <summary>
/// Shared-secret request authentication, used by every service that gates an endpoint on a
/// configured key.
///
/// This lives in the shared kernel because the alternative was three near-identical copies
/// that had already drifted: the Collector was hardened to a constant-time comparison while
/// JudgeAgent and AgentForge kept comparing with <c>==</c>, which short-circuits on the first
/// differing byte and leaks the key a character at a time under timing analysis. Cross-cutting
/// plumbing with one correct implementation is exactly what P2 asks the kernel to hold.
/// </summary>
public static class ApiKeyAuth
{
    /// <summary>The header a client presents, and the Bearer form accepted alongside it.</summary>
    public const string HeaderName = "x-api-key";

    /// <summary>
    /// True when the request carries <paramref name="expectedKey"/>. An empty expected key is
    /// dev/open mode and authorizes everything — the same convention the Collector uses, so a
    /// bare `dotnet run` needs no configuration.
    /// </summary>
    public static bool Authorized(HttpRequest request, string? expectedKey)
    {
        if (string.IsNullOrEmpty(expectedKey)) return true;

        var provided = request.Headers[HeaderName].FirstOrDefault()
                    ?? request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        return Matches(provided, expectedKey);
    }

    /// <summary>Constant-time comparison of two secrets; false when either is absent.</summary>
    public static bool Matches(string? provided, string? expected) =>
        !string.IsNullOrEmpty(provided)
        && !string.IsNullOrEmpty(expected)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
