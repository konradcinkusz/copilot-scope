using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CopilotScope.Local;

internal enum PortState
{
    Free,

    /// <summary>A CopilotScope collector answers there — most likely the Docker stack.</summary>
    CopilotScope,

    /// <summary>Some other program holds the port.</summary>
    Other
}

/// <summary>
/// Whether a port can be used, decided by connecting to it rather than by trying to bind it. On
/// macOS and Windows a bind to 127.0.0.1 can succeed while another program holds 0.0.0.0 on the
/// same port, and the two then split the traffic between them: half of an assistant's telemetry
/// would go to a collector nobody is looking at, and nothing would say so.
/// </summary>
internal static class Ports
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    public static async Task<PortState> ProbeAsync(int port, CancellationToken ct = default)
    {
        if (!await AcceptsAsync(IPAddress.Loopback, port, ct) && !await AcceptsAsync(IPAddress.IPv6Loopback, port, ct))
            return PortState.Free;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var health = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/health", ct));
            return health.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok"
                   && health.RootElement.TryGetProperty("hostlessSignals", out _)
                ? PortState.CopilotScope
                : PortState.Other;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or InvalidOperationException)
        {
            return PortState.Other;
        }
    }

    /// <summary>The first free port from <paramref name="preferred"/> onwards, trying ten; null if
    /// none is free.</summary>
    public static async Task<int?> FirstFreeAsync(int preferred, int avoid, CancellationToken ct = default)
    {
        for (var port = preferred; port < preferred + 10 && port < 65536; port++)
            if (port != avoid && await ProbeAsync(port, ct) == PortState.Free) return port;
        return null;
    }

    private static async Task<bool> AcceptsAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            // Created inside the try: on a machine without IPv6 the socket itself cannot exist.
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Refused, timed out, or no IPv6 on this machine: nothing is listening there.
            return false;
        }
    }
}
