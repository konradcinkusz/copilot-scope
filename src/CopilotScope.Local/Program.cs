using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotScope.Local;

// The native `copilotscope` binary (ADR-004): one command that runs the collector and the
// dashboard on this machine, with history kept in ~/.copilotscope/data.
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
        _ => await Commands.StartAsync(options)
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
{
    Console.Error.WriteLine($"copilotscope: {ex.Message}");
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

            using var shutdown = new CancellationTokenSource();
            using var signals = Signals.Register(shutdown);

            LocalHost host;
            try
            {
                host = await LocalHost.StartAsync(new LocalHostOptions(
                    options.OtlpPort, dashboardPort.Value, paths.Home, dataDirectory, webRoot, token, options.Verbose),
                    shutdown.Token);
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

        private static HttpClient Client(Instance instance) => new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{instance.OtlpPort}"),
            Timeout = TimeSpan.FromSeconds(3)
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
