namespace CopilotScope.Mcp;

/// <summary>One GET against the collector, reduced to what a tool result needs.</summary>
/// <param name="Status">HTTP status, or 0 when the request never got an answer.</param>
/// <param name="Ok">Whether the collector answered 2xx.</param>
/// <param name="Body">The response body, or the transport error when <paramref name="Status"/> is 0.</param>
public readonly record struct CollectorResponse(int Status, bool Ok, string Body);

/// <summary>The collector read surface the tools are built on. An interface so the
/// protocol tests can exercise the server without a running collector.</summary>
public interface ICollectorReader
{
    /// <summary>Where reads are being sent, for error messages a human has to act on.</summary>
    string Endpoint { get; }

    Task<CollectorResponse> GetAsync(string path, CancellationToken ct);
}

/// <summary>
/// Reads the collector's HTTP API.
///
/// Transport failures are returned rather than thrown: an assistant that asked for a
/// score and got "the collector is not running" can say so and carry on, whereas an
/// exception out of a tool call ends the turn with a stack trace.
/// </summary>
public sealed class CollectorClient(HttpClient http) : ICollectorReader
{
    public string Endpoint => http.BaseAddress?.ToString() ?? "(unset)";

    public async Task<CollectorResponse> GetAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return new CollectorResponse((int)response.StatusCode, response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return new CollectorResponse(0, false, ex.Message);
        }
    }
}
