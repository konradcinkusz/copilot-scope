using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CopilotScope.Collector.Alerting;

/// <summary>
/// Posts an alert or a digest to the configured webhook.
///
/// <para>Two payload shapes, because there are two audiences. <c>slack</c> sends a single
/// <c>text</c> field, which Slack, Mattermost, Discord-with-a-shim and most chat webhooks
/// render as a message — that is where a regression actually gets seen. <c>json</c> sends the
/// full document, for anything that will parse it.</para>
///
/// <para>Failures are logged and dropped, never retried into a queue: an alert about last
/// hour's quality is not worth delivering an hour late, and a growing retry buffer in a
/// collector whose job is ingest is a worse failure than a missed message. The next check
/// re-evaluates the same window anyway.</para>
/// </summary>
public sealed class AlertDispatcher(HttpClient http, AlertOptions options, ILogger<AlertDispatcher> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> SendAsync(string kind, object payload, string text, CancellationToken ct)
    {
        if (!options.Active) return false;

        try
        {
            using HttpContent content = options.IsSlack
                ? new StringContent(JsonSerializer.Serialize(new { text }, Json), Encoding.UTF8, "application/json")
                : JsonContent.Create(new { kind, generatedAt = DateTimeOffset.UtcNow, payload }, options: Json);

            using var response = await http.PostAsync(options.WebhookUrl, content, ct);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Sent {Kind} alert to the configured webhook.", kind);
                return true;
            }

            // Body deliberately not logged: a webhook's error response can echo the URL, and
            // the URL is the secret in a Slack-style incoming-webhook setup.
            logger.LogWarning("Webhook rejected the {Kind} alert with {Status}.", kind, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not deliver the {Kind} alert; it will not be retried.", kind);
            return false;
        }
    }
}
