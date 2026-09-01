using System.Security.Claims;
using CopilotScope.Dashboard.Components;
using CopilotScope.Dashboard.Services;
using CopilotScope.ServiceDefaults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

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

var components = app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
// Deny-by-default once auth is on: every page needs a signed-in user, and /login opts
// back out explicitly. Listing protected pages instead would fail open on the next one added.
if (authOptions.Enabled) components.RequireAuthorization();

app.Logger.LogInformation(
    "CopilotScope dashboard started — collector at {Collector}, sign-in {Auth}",
    collectorBase, authOptions.Enabled ? "required" : "disabled (open)");

app.Run();
