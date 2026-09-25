using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotScope.Collector.Import;
using CopilotScope.Local;
using CopilotScope.Local.Capturing;
using CopilotScope.Local.Connecting;
using CopilotScope.Local.Scanning;

// The native `copilotscope` binary (ADR-004): one command that runs the collector and the
// dashboard on this machine, with history kept in ~/.copilotscope/data.

// A Windows console defaults to a legacy code page, which has no ✓, → or —.
if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) Console.OutputEncoding = System.Text.Encoding.UTF8;

var (options, error) = CommandLine.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(error);
    return 2;
}

try
{
    return options.Command switch
    {
        "help" => Help(),
        "version" => Version(),
        "status" => await Commands.StatusAsync(),
        "stop" => await Commands.StopAsync(),
        "open" => await Commands.OpenAsync(),
        "url" => await Commands.UrlAsync(),
        "scan" => options.Report ? new HistoryReport(Machine.Current(), new Say(Console.Out)).Run() : await Commands.ScanAsync(),
        "capture-fixture" => Commands.CaptureFixture(options),
        "connect" => await Commands.ConnectAsync(options),
        "disconnect" => Commands.Disconnect(options),
        "setup" => await Commands.SetupAsync(options),
        "doctor" => await Commands.DoctorAsync(options),
        _ => await Commands.StartAsync(options)
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                               or HttpRequestException)
{
    Console.Error.WriteLine($"copilotscope: {ex.Message}");
    return 1;
}
catch (TaskCanceledException)
{
    Console.Error.WriteLine("copilotscope: the running instance did not answer in time.");
    return 1;
}

static int Help()
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

static int Version()
{
    Console.WriteLine($"copilotscope {Commands.Version}");
    return 0;
}

namespace CopilotScope.Local
{
    internal static class Commands
    {
        public static string Version { get; } =
            typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Commands).Assembly.GetName().Version?.ToString() ?? "unknown";

        public static async Task<int> StartAsync(LocalOptions options)
        {
            var paths = LocalPaths.Resolve();
            paths.CreatePrivate(paths.Home);
            paths.CreatePrivate(paths.Run);

            // Already running: say where, and open it — the command a person types to "get
            // CopilotScope" should get them CopilotScope, not an error about a port.
            if (await RunningAsync(paths) is { } running)
            {
                Console.WriteLine($"CopilotScope is already running: {running.DashboardUrl}");
                OpenBrowser(running.DashboardUrl, options.NoBrowser);
                return 0;
            }

            switch (await Ports.ProbeAsync(options.OtlpPort))
            {
                case PortState.CopilotScope:
                    Console.Error.WriteLine(
                        $"A CopilotScope collector is already answering on port {options.OtlpPort} — most likely the Docker " +
                        "stack. Use that one, or stop it first (`copilotscope down` with the Docker control script, or " +
                        "`docker compose -p copilotscope down`; its data volume is kept).");
                    return 1;
                case PortState.Other:
                    Console.Error.WriteLine(
                        $"Port {options.OtlpPort} is in use by another program. Free it, or pass --otlp-port <port> and point " +
                        "your assistants' OTLP endpoint at that port instead.");
                    return 1;
            }

            // The dashboard may move to the next free port; the telemetry port never does, because
            // every assistant is configured with it and would silently send into nothing.
            var dashboardPort = await Ports.FirstFreeAsync(options.DashboardPort, avoid: options.OtlpPort);
            if (dashboardPort is null)
            {
                Console.Error.WriteLine($"Ports {options.DashboardPort}–{options.DashboardPort + 9} are all in use. Pass --dashboard-port <port>.");
                return 1;
            }

            var webRoot = WebRoot.Resolve(options.WebRoot);
            if (!WebRoot.IsComplete(webRoot))
                Console.Error.WriteLine(
                    $"warning: the dashboard's files are missing from {webRoot}. Telemetry is still collected, but the " +
                    "dashboard cannot work until you reinstall or pass --webroot <directory>.");

            var dataDirectory = options.Memory ? null : Path.GetFullPath(options.DataDirectory ?? paths.Data);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            IReadOnlyList<IScanSource> sources = options.NoScan ? [] : [new ClaudeCodeSource()];

            using var shutdown = new CancellationTokenSource();
            using var signals = Signals.Register(shutdown);

            LocalHost host;
            try
            {
                host = await LocalHost.StartAsync(new LocalHostOptions(
                    options.OtlpPort, dashboardPort.Value, paths.Home, dataDirectory, webRoot, token, options.Verbose,
                    sources), shutdown.Token);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"copilotscope: could not start: {ex.Message}");
                return 1;
            }
            catch (OperationCanceledException)
            {
                return 130;
            }

            await using (host)
            {
                var instance = new Instance(Environment.ProcessId, options.OtlpPort, dashboardPort.Value, token, Version,
                    DateTimeOffset.UtcNow, dataDirectory ?? "memory");
                instance.Write(paths.InstanceFile);
                try
                {
                    Console.WriteLine($"""

                        CopilotScope {Version} is running.
                          Dashboard   {instance.DashboardUrl}
                          Telemetry   {instance.CollectorUrl}   (point your assistant's OTLP/HTTP exporter here)
                          Sessions    {(dataDirectory is null ? "in memory only — gone when this stops" : dataDirectory)}
                          History     {DescribeScanning(sources)}
                          Assistants  {DescribeAssistants(instance.CollectorUrl)}

                        Press Ctrl+C to stop.
                        """);
                    OpenBrowser(instance.DashboardUrl, options.NoBrowser);

                    await Task.WhenAny(host.Stopped, Task.Delay(Timeout.Infinite, shutdown.Token));
                    Console.WriteLine("Stopping…");
                }
                finally
                {
                    Instance.Delete(paths.InstanceFile, token);
                }
            }
            return 0;
        }

        /// <summary>What local history is read, and from where: nobody should have to guess which
        /// of their files a program is reading.</summary>
        private static string DescribeScanning(IReadOnlyList<IScanSource> sources)
        {
            if (sources.Count == 0) return "not read (--no-scan)";
            return string.Join("; ", sources.Select(source =>
                source.Roots.Where(Directory.Exists).ToList() is { Count: > 0 } found
                    ? $"{source.DisplayName}, read from {string.Join(" and ", found)} (never changed)"
                    : $"none from {source.DisplayName} yet — read from {string.Join(" or ", source.Roots)} once it appears"));
        }

        /// <summary>Which assistants already send telemetry here, and which could: said at every
        /// start, so nobody wonders why a session they just had is not on the dashboard.</summary>
        private static string DescribeAssistants(string endpoint)
        {
            try
            {
                var machine = Machine.Current();
                var found = new[]
                {
                    Assistants.ClaudeCodeStatus(machine, endpoint),
                    Assistants.VsCodeStatus(machine, endpoint),
                    Assistants.CopilotCliStatus(machine, endpoint, UserEnvironment())
                }.Where(s => s.Connection != Connection.NotInstalled).ToList();
                if (found.Count == 0) return "none found; the history on disk is read either way";

                var connected = found.Where(s => s.Connection == Connection.Connected).Select(s => Assistants.ShortName(s.Name)).ToList();
                var waiting = found.Where(s => s.Connection != Connection.Connected).Select(s => Assistants.ShortName(s.Name)).ToList();
                var sending = connected.Count > 0 ? $"{string.Join(", ", connected)} send telemetry here" : "";
                if (waiting.Count == 0) return sending;
                return (sending.Length > 0 ? sending + "; " : "") +
                       $"{string.Join(", ", waiting)} could too: `copilotscope setup` (it asks first)";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "could not be checked: `copilotscope doctor` says why";
            }
        }

        public static int CaptureFixture(LocalOptions options)
        {
            var machine = Machine.Current();
            return new FixtureCapture(machine, new Say(Console.Out), Identity.Current(machine.Home))
                .Run(options.Target!, Path.GetFullPath(options.Out), options.Limit, DateTimeOffset.UtcNow);
        }

        public static async Task<int> ConnectAsync(LocalOptions options) =>
            new Connector(Machine.Current(), new Say(Console.Out), UserEnvironment())
                .Connect(options.Target!, await ConnectSettingsAsync(options), options.Print);

        public static int Disconnect(LocalOptions options) =>
            new Connector(Machine.Current(), new Say(Console.Out), UserEnvironment()).Disconnect(options.Target ?? "all");

        public static async Task<int> SetupAsync(LocalOptions options)
        {
            // A yes is typed or given up front; with no terminal to ask in, nothing is written.
            Func<string, bool>? ask = Console.IsInputRedirected ? null : question =>
            {
                Console.Write(question);
                var answer = Console.ReadLine()?.Trim();
                return answer is not null && (answer.Length == 0 || answer.StartsWith('y') || answer.StartsWith('Y'));
            };
            return new Connector(Machine.Current(), new Say(Console.Out), UserEnvironment())
                .Setup(await ConnectSettingsAsync(options), options.Yes, ask, options.Print);
        }

        public static async Task<int> DoctorAsync(LocalOptions options)
        {
            var running = await RunningAsync(LocalPaths.Resolve());
            using var http = running is null ? null : Client(running, TimeSpan.FromSeconds(10));
            var endpoint = options.Endpoint ?? running?.CollectorUrl ?? $"http://localhost:{options.OtlpPort}";
            return await new Doctor(Machine.Current(), new Say(Console.Out), UserEnvironment())
                .RunAsync(running, http, endpoint, options.OtlpPort, WebRoot.Resolve(options.WebRoot),
                    ClaudeCodeFiles.DefaultRoots());
        }

        /// <summary>Where connecting points assistants: an explicit endpoint, the one the control
        /// script honours, the running instance, or where this machine's instance would listen.</summary>
        private static async Task<ConnectSettings> ConnectSettingsAsync(LocalOptions options)
        {
            var endpoint = options.Endpoint
                ?? (Environment.GetEnvironmentVariable("COPILOTSCOPE_ENDPOINT") is { Length: > 0 } configured
                    ? configured.TrimEnd('/')
                    : (await RunningAsync(LocalPaths.Resolve()))?.CollectorUrl ?? $"http://localhost:{options.OtlpPort}");
            var key = Environment.GetEnvironmentVariable("COPILOTSCOPE_API_KEY") is { Length: > 0 } k ? k : null;
            return new ConnectSettings(endpoint, key, options.Capture, options.Traces);
        }

        private static IUserEnvironment? UserEnvironment() => OperatingSystem.IsWindows() ? new WindowsUserEnvironment() : null;

        public static async Task<int> ScanAsync()
        {
            if (await RunningAsync(LocalPaths.Resolve()) is not { } running)
            {
                Console.Error.WriteLine("CopilotScope is not running. Start it with `copilotscope`: it reads local history as it starts.");
                return 1;
            }

            // A first scan over a long history reads every file once; give it time.
            using var http = Client(running, TimeSpan.FromMinutes(10));
            var request = new HttpRequestMessage(HttpMethod.Post, "/_copilotscope/scan");
            request.Headers.Add("X-CopilotScope-Token", running.Token);
            using var response = await http.SendAsync(request);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Conflict:
                    Console.Error.WriteLine("The running CopilotScope was started with --no-scan, so it reads no local history.");
                    return 1;
                case HttpStatusCode.NotFound:
                    Console.Error.WriteLine($"The running CopilotScope ({running.Version}) cannot scan on request. " +
                                            "Restart it: `copilotscope stop`, then `copilotscope`.");
                    return 1;
            }
            response.EnsureSuccessStatusCode();

            var report = await response.Content.ReadFromJsonAsync<ScanReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("The running instance sent an empty scan report.");
            foreach (var source in report.Sources) Console.WriteLine(source.Describe(report.SettleMinutes));
            return 0;
        }

        public static async Task<int> StatusAsync()
        {
            var paths = LocalPaths.Resolve();
            if (await RunningAsync(paths) is not { } running)
            {
                Console.WriteLine("CopilotScope is not running. Start it with `copilotscope`.");
                return 1;
            }

            Console.WriteLine($"CopilotScope {running.Version} is running (pid {running.ProcessId}).");
            Console.WriteLine($"  Dashboard   {running.DashboardUrl}");
            Console.WriteLine($"  Telemetry   {running.CollectorUrl}");
            Console.WriteLine($"  Sessions    {running.Storage}");
            try
            {
                using var http = Client(running);
                var health = await http.GetFromJsonAsync<JsonElement>("/api/health");
                Console.WriteLine($"  Held        {health.GetProperty("sessions").GetInt32()} session(s) in memory");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                           or KeyNotFoundException or InvalidOperationException) { }
            return 0;
        }

        public static async Task<int> StopAsync()
        {
            var paths = LocalPaths.Resolve();
            if (await RunningAsync(paths) is not { } running)
            {
                Console.WriteLine("CopilotScope is not running.");
                return 0;
            }

            using (var http = Client(running))
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/_copilotscope/stop");
                request.Headers.Add("X-CopilotScope-Token", running.Token);
                using var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }

            // Wait for it to be gone, so a script can start another straight after.
            for (var i = 0; i < 60; i++)
            {
                await Task.Delay(250);
                if (!await AnswersAsync(running)) { Console.WriteLine("CopilotScope stopped."); return 0; }
            }
            Console.Error.WriteLine($"CopilotScope (pid {running.ProcessId}) was asked to stop but is still running.");
            return 1;
        }

        public static async Task<int> OpenAsync()
        {
            if (await RunningAsync(LocalPaths.Resolve()) is not { } running)
            {
                Console.Error.WriteLine("CopilotScope is not running. Start it with `copilotscope`.");
                return 1;
            }
            if (!Browser.TryOpen(running.DashboardUrl)) Console.WriteLine($"Open {running.DashboardUrl} in your browser.");
            return 0;
        }

        public static async Task<int> UrlAsync()
        {
            if (await RunningAsync(LocalPaths.Resolve()) is not { } running)
            {
                Console.Error.WriteLine("CopilotScope is not running.");
                return 1;
            }
            Console.WriteLine(running.DashboardUrl);
            return 0;
        }

        /// <summary>The instance the file describes, if it is really still running — checked by
        /// asking it, with its token, rather than by process id: a process id is reused, a port
        /// is taken over, and a token is neither. A stale file is removed.</summary>
        internal static async Task<Instance?> RunningAsync(LocalPaths paths)
        {
            if (Instance.Read(paths.InstanceFile) is not { } instance) return null;
            if (await AnswersAsync(instance)) return instance;
            Instance.Delete(paths.InstanceFile, instance.Token);
            return null;
        }

        private static async Task<bool> AnswersAsync(Instance instance)
        {
            try
            {
                using var http = Client(instance);
                var request = new HttpRequestMessage(HttpMethod.Get, "/_copilotscope/ping");
                request.Headers.Add("X-CopilotScope-Token", instance.Token);
                using var response = await http.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }

        private static HttpClient Client(Instance instance, TimeSpan? timeout = null) => new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{instance.OtlpPort}"),
            Timeout = timeout ?? TimeSpan.FromSeconds(3)
        };

        private static void OpenBrowser(string url, bool suppressed)
        {
            if (suppressed || !Browser.Available()) return;
            if (!Browser.TryOpen(url)) Console.WriteLine($"Open {url} in your browser.");
        }
    }

    /// <summary>
    /// Ctrl+C, SIGTERM, and a closed terminal (SIGHUP; on Windows, closing the console window)
    /// start an orderly stop: the dashboard first, then the collector with its final flush.
    /// Without SIGHUP, closing the terminal CopilotScope runs in would end it mid-write. A second
    /// signal is not intercepted, so someone who presses Ctrl+C twice gets the process ended at
    /// once.
    /// </summary>
    internal sealed class Signals : IDisposable
    {
        private readonly PosixSignalRegistration[] _registrations;

        private Signals(CancellationTokenSource shutdown)
        {
            void Handle(PosixSignalContext context)
            {
                if (shutdown.IsCancellationRequested) return;
                context.Cancel = true;
                shutdown.Cancel();
            }

            _registrations =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
                PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Handle),
                PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handle)
            ];
        }

        public static Signals Register(CancellationTokenSource shutdown) => new(shutdown);

        public void Dispose()
        {
            foreach (var registration in _registrations) registration.Dispose();
        }
    }
}
