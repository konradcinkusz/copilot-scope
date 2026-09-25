using System.Net;
using System.Security.Cryptography;
using System.Text;
using CopilotScope.Collector;
using CopilotScope.Dashboard;
using CopilotScope.Local.Scanning;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CopilotScope.Local;

/// <summary>What one run of the host needs, resolved from the command line.</summary>
internal sealed record LocalHostOptions(
    int OtlpPort,
    int DashboardPort,
    string ContentRoot,
    string? DataDirectory,
    string WebRoot,
    string Token,
    bool Verbose = false,
    IReadOnlyList<IScanSource>? ScanSources = null,
    ScannerOptions? Scan = null);

/// <summary>
/// The collector and the dashboard, running side by side in this process (ADR-004).
///
/// Two applications on two ports rather than one pipeline: the dashboard keeps reading the
/// collector over HTTP, exactly as it does across containers, because the privacy guard, the
/// k-anonymity floor and the access audit all live on that path (CLAUDE.md). One pipeline would
/// also have both applications claiming <c>/</c>, <c>/health</c> and <c>/alive</c>.
/// </summary>
internal sealed class LocalHost : IAsyncDisposable
{
    /// <summary>The only names these applications answer to. A browser page on any other site
    /// can make a browser send requests here, and DNS rebinding can make such a site resolve to
    /// 127.0.0.1; checking the Host header is what stops that page reading the transcripts.</summary>
    public const string LoopbackHosts = "localhost;127.0.0.1;[::1]";

    // The applications' real assembly names: ASP.NET Core resolves an application's parts and
    // hosting-startup assemblies by this name, and anything else is an assembly it cannot load.
    private const string CollectorName = "CopilotScope.Collector";
    private const string DashboardName = "CopilotScope.Dashboard";

    /// <summary>
    /// Settings are read once, at start. Reloading appsettings.json on change would watch the
    /// content root — ~/.copilotscope, sessions and all — recursively, once per application, so
    /// every session file written woke two watchers looking for a settings file. On Linux each
    /// watcher also holds one of the user's inotify instances (128 by default, shared with every
    /// editor and .NET tool they run), and a user at that limit could not start copilotscope.
    /// </summary>
    private static readonly string[] ApplicationArgs = ["--hostBuilder:reloadConfigOnChange=false"];

    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HttpClient? _scanClient;
    private bool _disposed;

    private LocalHost(WebApplication collector, WebApplication dashboard, string collectorUrl, string dashboardUrl)
    {
        Collector = collector;
        Dashboard = dashboard;
        CollectorUrl = collectorUrl;
        DashboardUrl = dashboardUrl;
    }

    public WebApplication Collector { get; }
    public WebApplication Dashboard { get; }
    public string CollectorUrl { get; }
    public string DashboardUrl { get; }

    /// <summary>Reading assistants' local history into the collector; null when scanning is off.</summary>
    public Scanner? Scanner { get; private set; }

    /// <summary>Completes when something asked this host to stop: <c>copilotscope stop</c>, or one
    /// of the two applications shutting itself down (a background service that failed).</summary>
    public Task Stopped => _stopRequested.Task;

    public static async Task<LocalHost> StartAsync(LocalHostOptions options, CancellationToken ct = default)
    {
        var collector = await CollectorApp.BuildAsync(new WebApplicationOptions
        {
            ApplicationName = CollectorName,
            ContentRootPath = options.ContentRoot,
            EnvironmentName = Environments.Production,
            Args = ApplicationArgs
        }, b =>
        {
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Set explicitly rather than left to defaults, so nothing in the user's environment
                // — a ConnectionStrings__copilotdb exported for the Docker stack, say — can turn
                // this into a Postgres deployment or a network listener.
                ["CopilotScope:Storage:Mode"] = options.DataDirectory is null ? "memory" : "files",
                ["CopilotScope:Storage:Path"] = options.DataDirectory,
                ["ConnectionStrings:copilotdb"] = "",
                ["CopilotScope:Ingest:Bind"] = "127.0.0.1",
                ["CopilotScope:SelfTelemetry:Enabled"] = "false",
                ["AllowedHosts"] = LoopbackHosts
            });
            ConfigureShared(b, options.OtlpPort, options.Verbose);
        });

        LocalHost? host = null;
        collector.MapGet("/_copilotscope/ping", (HttpRequest request) =>
            TokenMatches(request, options.Token) ? Results.Ok(new { pid = Environment.ProcessId }) : Results.NotFound());
        collector.MapPost("/_copilotscope/stop", (HttpRequest request) =>
        {
            if (!TokenMatches(request, options.Token)) return Results.NotFound();
            host?._stopRequested.TrySetResult();
            return Results.Accepted();
        });
        // `copilotscope scan`: a pass now, answered with what it found. Token-bound like stop —
        // it reads every transcript on the machine — and served by the process that owns the
        // scan state, so no second process ever writes it.
        collector.MapPost("/_copilotscope/scan", async (HttpRequest request, CancellationToken requestAborted) =>
        {
            if (!TokenMatches(request, options.Token)) return Results.NotFound();
            if (host?.Scanner is not { } scanner) return Results.Conflict();
            return Results.Ok(await scanner.ScanNowAsync(requestAborted));
        });

        try
        {
            await collector.StartAsync(ct);
        }
        catch
        {
            await collector.DisposeAsync();
            throw;
        }

        var collectorUrl = BoundUrl(collector);
        WebApplication? dashboard = null;
        try
        {
            dashboard = DashboardApp.Build(new WebApplicationOptions
            {
                ApplicationName = DashboardName,
                ContentRootPath = options.ContentRoot,
                WebRootPath = options.WebRoot,
                EnvironmentName = Environments.Production,
                Args = ApplicationArgs
            }, b =>
            {
                b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["services:collector:http:0"] = collectorUrl,
                    ["Collector:BaseUrl"] = collectorUrl,
                    ["CopilotScope:SelfTelemetry:Enabled"] = "false",
                    ["AllowedHosts"] = LoopbackHosts
                });
                ConfigureShared(b, options.DashboardPort, options.Verbose);
            });

            if (!WebRoot.IsComplete(options.WebRoot))
            {
                // Say so at the one address a person opens, rather than serve pages that render
                // and then never respond.
                var page = WebRoot.MissingPage(options.WebRoot);
                dashboard.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/")
                    {
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.WriteAsync(page);
                        return;
                    }
                    await next();
                });
            }

            await dashboard.StartAsync(ct);
        }
        catch
        {
            if (dashboard is not null) await dashboard.DisposeAsync();
            await collector.StopAsync(CancellationToken.None);
            await collector.DisposeAsync();
            throw;
        }

        host = new LocalHost(collector, dashboard, collectorUrl, BoundUrl(dashboard));
        // A background service that fails stops its host; the other half must not run on alone,
        // collecting into a dashboard that is gone or serving one whose collector is.
        collector.Lifetime.ApplicationStopping.Register(() => host._stopRequested.TrySetResult());
        dashboard.Lifetime.ApplicationStopping.Register(() => host._stopRequested.TrySetResult());
        if (options.ScanSources is { Count: > 0 } sources) host.StartScanning(sources, options);
        return host;
    }

    /// <summary>
    /// Starts reading local history once the collector is listening: it arrives the way the
    /// importer's does, over HTTP to <c>/api/import</c>, and never into the store directly
    /// (CLAUDE.md). The scan state lives in the data directory it describes, or in memory when
    /// that is where the sessions live too.
    /// </summary>
    private void StartScanning(IReadOnlyList<IScanSource> sources, LocalHostOptions options)
    {
        var logger = Collector.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Scanner>();
        var state = options.DataDirectory is null
            ? ScanState.InMemory()
            : ScanState.Load(Path.Combine(options.DataDirectory, ScanState.FileName), logger);

        _scanClient = new HttpClient { BaseAddress = new Uri(CollectorUrl), Timeout = TimeSpan.FromMinutes(5) };
        // A key set in the environment gates imports too; the scanner presents it like any client.
        if (Collector.Configuration["CopilotScope:Ingest:ApiKey"] is { Length: > 0 } key)
            _scanClient.DefaultRequestHeaders.Add("x-api-key", key);

        Scanner = new Scanner(sources, state, _scanClient, options.Scan, logger: logger);
        Scanner.Start();
    }

    /// <summary>Stops the scanner first, so no import is cut off half-way through the collector's
    /// shutdown; then the dashboard, so nothing reads a collector that is going away; and the
    /// collector last, so its final flush writes everything that arrived.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested.TrySetResult();

        if (Scanner is not null) await Scanner.StopAsync();
        _scanClient?.Dispose();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await Dashboard.StopAsync(timeout.Token); }
        finally
        {
            await Dashboard.DisposeAsync();
            try { await Collector.StopAsync(timeout.Token); }
            finally { await Collector.DisposeAsync(); }
        }
    }

    private static void ConfigureShared(WebApplicationBuilder builder, int port, bool verbose)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Loopback only: this is one person's data on one person's machine. Port 0 (tests)
            // cannot use ListenLocalhost, which refuses a dynamic port.
            if (port == 0) kestrel.Listen(IPAddress.Loopback, 0);
            else kestrel.ListenLocalhost(port);
        });
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        // No hosting-startup assemblies: ASPNETCORE_HOSTINGSTARTUPASSEMBLIES in someone's
        // environment would otherwise load code into the process that holds their transcripts.
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        // This process owns Ctrl+C and SIGTERM, and stops the two applications in order. With
        // the default lifetime each would react to the signal on its own, in no particular order.
        builder.Services.AddSingleton<IHostLifetime, NoSignalLifetime>();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        var quiet = verbose ? LogLevel.Information : LogLevel.Warning;
        builder.Logging.AddFilter("Microsoft", quiet);
        builder.Logging.AddFilter("System", quiet);
        builder.Logging.AddFilter("Polly", quiet);
        // The applications' own start-up banners, logged under the bare application name, describe
        // a deployment this is not (containers, the Aspire AppHost); the host prints its own. The
        // longer rule keeps every class inside each application at Information: the most specific
        // matching rule wins.
        foreach (var application in new[] { CollectorName, DashboardName })
        {
            builder.Logging.AddFilter(application, quiet);
            builder.Logging.AddFilter(application + ".", LogLevel.Information);
        }
    }

    private static string BoundUrl(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault() ?? throw new InvalidOperationException("The server reported no address after starting.");
        // ListenLocalhost reports http://localhost:<port>; a dynamic bind reports 127.0.0.1.
        return address.TrimEnd('/');
    }

    private static bool TokenMatches(HttpRequest request, string token) =>
        request.Headers.TryGetValue("X-CopilotScope-Token", out var presented)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented.ToString()), Encoding.UTF8.GetBytes(token));
}

/// <summary>An application lifetime that ignores process signals, for applications whose
/// process stops them itself.</summary>
internal sealed class NoSignalLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
