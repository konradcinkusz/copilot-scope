using Microsoft.AspNetCore.Http;

namespace CopilotScope.Dashboard.Services;

/// <summary>
/// Puts the signed-in dashboard user on every collector request.
///
/// The dashboard holds the collector's API key and calls on the viewer's behalf, so without
/// this the collector's access log records one actor for everybody — "the dashboard read
/// 400 sessions", which answers none of the questions an access log exists to answer. The
/// header carries the role the person signed in as; the collector trusts it only from a
/// caller that already holds a read credential, so forging it needs the key that would have
/// granted the read anyway.
///
/// When sign-in is off there is no user to name and the header is omitted, which the
/// collector records as the credential fingerprint instead.
/// </summary>
public sealed class ActorForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    public const string HeaderName = "X-CopilotScope-Actor";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Blazor Server keeps a circuit alive after the HTTP request that opened it has
        // ended, so the accessor is null on background refreshes. The claim captured at
        // sign-in is what identifies the person either way; when neither is available, say
        // nothing rather than guessing.
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true && user.Identity.Name is { Length: > 0 } name)
            request.Headers.TryAddWithoutValidation(HeaderName, name);

        return base.SendAsync(request, cancellationToken);
    }
}
