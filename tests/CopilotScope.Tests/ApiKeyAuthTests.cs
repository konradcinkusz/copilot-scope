using CopilotScope.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CopilotScope.Tests;

/// <summary>
/// The shared-secret check every service gates its endpoints on. It lives in the shared
/// kernel because three copies had already drifted: the Collector was hardened to a
/// constant-time comparison while JudgeAgent and AgentForge kept using <c>==</c>.
/// </summary>
public class ApiKeyAuthTests
{
    private static HttpRequest Request(string? header = null, string? authorization = null)
    {
        var ctx = new DefaultHttpContext();
        if (header is not null) ctx.Request.Headers[ApiKeyAuth.HeaderName] = header;
        if (authorization is not null) ctx.Request.Headers.Authorization = authorization;
        return ctx.Request;
    }

    [Fact]
    public void TheRightKeyIsAuthorized() =>
        Assert.True(ApiKeyAuth.Authorized(Request(header: "secret"), "secret"));

    [Fact]
    public void ABearerTokenIsAcceptedToo() =>
        // Prometheus scrapes send the key this way, so both forms have to work.
        Assert.True(ApiKeyAuth.Authorized(Request(authorization: "Bearer secret"), "secret"));

    [Fact]
    public void TheWrongKeyIsRejected() =>
        Assert.False(ApiKeyAuth.Authorized(Request(header: "not-the-secret"), "secret"));

    [Fact]
    public void AMissingKeyIsRejected() =>
        Assert.False(ApiKeyAuth.Authorized(Request(), "secret"));

    [Fact]
    public void AnEmptyExpectedKeyIsOpenDevMode()
    {
        // Same convention as the Collector: no key configured means a bare `dotnet run`
        // needs no setup. Deliberate, and documented in SECURITY.md.
        Assert.True(ApiKeyAuth.Authorized(Request(), null));
        Assert.True(ApiKeyAuth.Authorized(Request(), ""));
    }

    [Fact]
    public void AKeyThatIsAPrefixOfTheRealOneIsRejected() =>
        // A length-only or prefix comparison would accept this.
        Assert.False(ApiKeyAuth.Authorized(Request(header: "sec"), "secret"));

    [Fact]
    public void AKeyThatOnlyDiffersInTheLastByteIsRejected() =>
        Assert.False(ApiKeyAuth.Authorized(Request(header: "secreT"), "secret"));

    [Fact]
    public void WhitespaceOnlyKeysAreStillCompared()
    {
        // A strange key is still a key. Treating it as "unset" would silently open the
        // service — the same fail-open shape that had to be fixed in the Collector.
        Assert.True(ApiKeyAuth.Authorized(Request(header: "   "), "   "));
        Assert.False(ApiKeyAuth.Authorized(Request(header: "x"), "   "));
    }

    [Fact]
    public void MatchesRejectsAbsentSecretsOnBothSides()
    {
        Assert.False(ApiKeyAuth.Matches(null, "secret"));
        Assert.False(ApiKeyAuth.Matches("secret", null));
        Assert.False(ApiKeyAuth.Matches("", ""));
    }
}
