using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace CopilotScope.Dashboard.Services;

/// <summary>
/// Dashboard sign-in, bound from <c>CopilotScope:Dashboard:Auth</c>.
///
/// The dashboard held the collector's API key server-side and attached it to its own
/// requests, so the key gated the API but not the UI: anyone who could reach the port read
/// every captured prompt and response and could delete sessions. With content capture on
/// that is source code and pasted credentials behind no credential at all.
///
/// Off by default, because a laptop-local run should not grow a login screen. Setting a
/// password turns it on.
/// </summary>
public sealed class DashboardAuthOptions
{
    /// <summary>Password for read-only access: no transcripts, no delete.</summary>
    public string ViewerPassword { get; set; } = "";

    /// <summary>Password for full access, including transcripts and deletion.</summary>
    public string AdminPassword { get; set; } = "";

    /// <summary>How long a sign-in lasts.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Auth is on as soon as either password is set.</summary>
    public bool Enabled => !string.IsNullOrEmpty(ViewerPassword) || !string.IsNullOrEmpty(AdminPassword);

    /// <summary>
    /// Resolves a submitted password to a role, in constant time. Admin is checked first so
    /// that configuring both passwords to the same value grants the stronger role rather
    /// than the weaker one.
    /// </summary>
    public string? RoleFor(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var admin = Matches(password, AdminPassword);
        var viewer = Matches(password, ViewerPassword);
        return admin ? DashboardRoles.Admin : viewer ? DashboardRoles.Viewer : null;
    }

    private static bool Matches(string provided, string configured) =>
        !string.IsNullOrEmpty(configured)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(configured));

    /// <summary>
    /// Transcripts are admin-only. Transcript text is the sensitive payload — the actual
    /// conversation, including whatever was pasted into it — while a viewer's questions
    /// ("where is our tooling wasting time") are answered by scores, turns and aggregates.
    /// </summary>
    public bool CanReadTranscripts(ClaimsPrincipal? user) => IsAdmin(user);

    /// <summary>Deletion is irreversible, so it stays with the stronger role.</summary>
    public bool CanDelete(ClaimsPrincipal? user) => IsAdmin(user);

    /// <summary>
    /// With auth disabled the dashboard behaves exactly as it did before this existed:
    /// a local run has no login and no restrictions. With auth enabled the role must be
    /// present and correct — an unauthenticated principal is never treated as permitted.
    /// </summary>
    private bool IsAdmin(ClaimsPrincipal? user) =>
        !Enabled || user?.IsInRole(DashboardRoles.Admin) == true;
}

public static class DashboardRoles
{
    public const string Viewer = "viewer";
    public const string Admin = "admin";
}
