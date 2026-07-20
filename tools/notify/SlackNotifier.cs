using System.Text;
using System.Text.Json;

namespace SprintLauncher.Notify;

public sealed class SlackNotifier
{
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
        var firstLine = $"[{options.ActorKey}] {options.Level} \u2014 {options.Text}";
        return options.Context is null ? firstLine : $"{firstLine}\ncontext: {options.Context}";
    }

    public static StringContent CreatePayload(string text) =>
        new(JsonSerializer.Serialize(new { text }, JsonOptions), Encoding.UTF8, "application/json");

    public async Task SendAsync(Uri webhook, NotifyOptions options, CancellationToken cancellationToken)
    {
        var payloadText = FormatText(options);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var content = CreatePayload(payloadText);
                using var response = await _httpClient.PostAsync(webhook, content, cancellationToken);
                response.EnsureSuccessStatusCode();
                return;
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
