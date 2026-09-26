using CopilotScope.Dashboard.Components;
using CopilotScope.Dashboard.Functions;
using CopilotScope.Dashboard.Services;
using CopilotScope.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace CopilotScope.Dashboard;

/// <summary>
/// The dashboard as a buildable application rather than a program, for the same reason as
/// <c>CollectorApp</c>: the native <c>copilotscope</c> host (ADR-004) runs it inside its own
/// process, beside the collector. It still reads the collector over HTTP — the privacy guard,
/// the k-anonymity floor and the access audit all live on that path.
/// </summary>
public static class DashboardApp
{
    /// <summary>The dashboard's own <c>appsettings.json</c>, compiled in.</summary>
    internal const string DefaultsResource = "CopilotScope.Dashboard.appsettings.json";

    /// <summary>Builds the dashboard. The caller runs it.</summary>
    /// <param name="options">Passed to <see cref="WebApplication.CreateBuilder(WebApplicationOptions)"/>;
    /// a host sets the web root here, which is where <c>_framework/blazor.web.js</c> is served
    /// from.</param>
    /// <param name="configure">Runs before anything reads configuration.</param>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        builder.AddEmbeddedDefaults(typeof(DashboardApp).Assembly, DefaultsResource);
        configure?.Invoke(builder);

        // Shared kernel: OTel, health (/health + /alive — the dashboard had none), discovery,
        // resilience on the collector HttpClient (P2/P15).
        builder.AddServiceDefaults();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Dashboard sign-in. Off unless a password is configured, so a laptop-local run is
        // unchanged; with one set, the UI stops being an unauthenticated window onto every
        // captured transcript.
        var authOptions = new DashboardAuthOptions();
        builder.Configuration.GetSection("CopilotScope:Dashboard:Auth").Bind(authOptions);
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddCascadingAuthenticationState();

        if (authOptions.Enabled)
        {
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/login";
                    options.AccessDeniedPath = "/login";
                    options.ExpireTimeSpan = authOptions.SessionLifetime;
                    options.SlidingExpiration = true;
                    options.Cookie.Name = "copilotscope.auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    // Secure whenever the browser is on https; forcing it unconditionally would
                    // break the documented plain-http compose deployment.
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                });
            builder.Services.AddAuthorization();
        }

        // Aspire's WithReference(collector) injects services__collector__http__0; the config
        // system maps "__" to ":". Fallbacks keep the app runnable without the AppHost.
        var collectorBase = builder.Configuration["services:collector:http:0"];
        if (string.IsNullOrWhiteSpace(collectorBase))
            collectorBase = builder.Configuration["Collector:BaseUrl"];
        if (string.IsNullOrWhiteSpace(collectorBase))
            collectorBase = "http://localhost:4318";

        // When the collector is deployed with an ingest key, its /api group is gated, so the
        // dashboard must present the same key. In local/dev mode the key is empty and the
        // header is simply omitted.
        var ingestApiKey = builder.Configuration["CopilotScope:Ingest:ApiKey"];

        // Named the signed-in viewer on every collector call, so the collector's access audit log
        // (privacy mode) records a person rather than "the dashboard".
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTransient<ActorForwardingHandler>();

        builder.Services.AddHttpClient<CollectorClient>(client =>
        {
            client.BaseAddress = new Uri(collectorBase);
            client.Timeout = TimeSpan.FromSeconds(5);
            if (!string.IsNullOrEmpty(ingestApiKey))
                client.DefaultRequestHeaders.Add("x-api-key", ingestApiKey);
        }).AddHttpMessageHandler<ActorForwardingHandler>();

        var app = builder.Build();

        app.MapDefaultEndpoints(); // /health + /alive

        app.UseStaticFiles();

        if (authOptions.Enabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseAntiforgery();

        if (authOptions.Enabled)
        {
            // Minimal-API sign-in/out rather than Blazor form handling: the cookie has to be
            // written to the HTTP response, which an interactive circuit no longer owns.
            app.MapPost("/login", async (HttpContext ctx, DashboardAuthOptions auth) =>
            {
                var form = await ctx.Request.ReadFormAsync();
                if (auth.RoleFor(form["password"].ToString()) is not { } role)
                    return Results.Redirect("/login?error=1");

                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role)],
                    CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));
                return Results.Redirect("/");
            }).DisableAntiforgery();

            app.MapPost("/logout", async (HttpContext ctx) =>
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login");
            }).DisableAntiforgery();
        }

        // CSV export proxy.
        //
        // The browser cannot call the collector directly — the API key lives here and never reaches
        // the page — so the download has to be served from this origin and forwarded server-side.
        // Deliberately a proxy rather than a re-implementation: the CSV is generated in one place, so
        // the file a lead mails to their director is the same numbers the dashboard rendered.
        var export = app.MapGet("/export/{report}.csv", async (string report, HttpRequest request,
            CollectorClient collector, CancellationToken ct) =>
        {
            if (report is not ("cohorts" or "compare")) return Results.NotFound();

            // Forward the caller's filters verbatim, plus format=csv. Whitelisted by name so a
            // crafted query string cannot reach an endpoint or parameter this route does not intend.
            string[] allowed = ["days", "since", "until", "repository", "emitter", "model", "kind", "grade",
                                "baselineSince", "baselineUntil"];
            var query = string.Concat(allowed
                .Where(name => request.Query.ContainsKey(name))
                .Select(name => $"&{name}={Uri.EscapeDataString(request.Query[name].ToString())}"));

            using var response = await collector.GetRawAsync($"/api/{report}?format=csv{query}", ct);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode((int)response.StatusCode);

            var csv = await response.Content.ReadAsStringAsync(ct);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm");
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv",
                $"copilotscope-{report}-{stamp}.csv");
        });
        // The export carries the same aggregates the pages show, so it needs the same sign-in.
        if (authOptions.Enabled) export.RequireAuthorization();

        // Function kits (docs/FUNCTIONS.md): a function's task, agents and review pack as one zip, for an
        // assistant this process cannot start — VS Code Copilot Chat, or any assistant at all behind a
        // Compose deployment, which runs none (ADR-005, decision 8). The pack is fetched over the API like
        // every other read, so the privacy guard, the aggregation floor and the audit log all see it. The
        // sessions tier carries session ids, so only a viewer who may read transcripts asks for it; the
        // collector still decides whether the dashboard's key may have it, and the kit says which it got.
        var kit = app.MapGet("/functions/{id}/kit.zip", async (string id, int? days, HttpContext context,
            CollectorClient collector, CancellationToken ct) =>
        {
            if (FunctionCatalog.Find(id) is not { } function) return Results.NotFound();
            var window = Math.Clamp(days ?? function.DefaultDays, 1, 365);

            var pack = await collector.GetReviewPackAsync(window, authOptions.CanReadTranscripts(context.User), ct);
            if (!pack.Ok)
                return Results.Text($"No review pack to put in the kit: {pack.Problem}", "text/plain; charset=utf-8",
                    statusCode: StatusCodes.Status409Conflict);

            var folder = FunctionKit.FolderName(function, DateTimeOffset.UtcNow);
            var files = FunctionWorkspace.Files(new WorkspaceInput(function, folder, window, pack.Tier,
                pack.Markdown!, pack.Json!));
            return Results.File(FunctionKit.Zip(folder, files), "application/zip", folder + ".zip");
        });
        if (authOptions.Enabled) kit.RequireAuthorization();

        var components = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        // Deny-by-default once auth is on: every page needs a signed-in user, and /login opts
        // back out explicitly. Listing protected pages instead would fail open on the next one added.
        if (authOptions.Enabled) components.RequireAuthorization();

        app.Logger.LogInformation(
            "CopilotScope dashboard started — collector at {Collector}, sign-in {Auth}",
            collectorBase, authOptions.Enabled ? "required" : "disabled (open)");

        return app;
    }
}
