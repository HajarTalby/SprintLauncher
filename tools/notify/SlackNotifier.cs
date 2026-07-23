using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SprintLauncher.Notify;

public sealed class SlackNotifier
{
    private const string PostMessageUrl = "https://slack.com/api/chat.postMessage";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Func<int, CancellationToken, Task> _delay;

    public SlackNotifier(HttpMessageHandler handler, Func<int, CancellationToken, Task>? delay = null)
        : this(new HttpClient(handler), delay)
    {
    }

    public SlackNotifier(HttpClient httpClient, Func<int, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _delay = delay ?? ((attempt, cancellationToken) =>
            Task.Delay(TimeSpan.FromMilliseconds(attempt == 1 ? 500 : 1500), cancellationToken));
    }

    public static string FormatText(NotifyOptions options)
    {
        var firstLine = $"[{options.ActorKey}] {options.Level} — {options.Text}";
        return options.Context is null ? firstLine : $"{firstLine}\ncontext: {options.Context}";
    }

    public static StringContent CreatePayload(string channel, string text) =>
        new(JsonSerializer.Serialize(new { channel, text }, JsonOptions), Encoding.UTF8, "application/json");

    public async Task SendAsync(SlackTarget target, NotifyOptions options, CancellationToken cancellationToken)
    {
        var payloadText = FormatText(options);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, PostMessageUrl)
                {
                    Content = CreatePayload(target.Channel, payloadText),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                // L'API Slack renvoie HTTP 200 même sur erreur applicative : le vrai statut
                // est le champ JSON "ok". Une erreur applicative (channel_not_found,
                // invalid_auth, not_in_channel) ne se corrige pas en réessayant → on la
                // remonte immédiatement, sans retry.
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    return;
                }

                var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                throw new SlackNotifyException($"Slack API a répondu ok=false: {error}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
                if (attempt == 3) break;
                await _delay(attempt, cancellationToken);
            }
        }

        throw new SlackNotifyException($"Slack notification failed after retries: {lastException?.Message}", lastException);
    }
}
