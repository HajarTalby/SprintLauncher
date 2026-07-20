using System.Net;
using System.Text.Json;
using SprintLauncher.Notify;
using Xunit;

namespace SprintLauncher.Tests;

public class NotifyTests
{
    [Fact]
    public void Resolves_actor_specific_webhook()
    {
        var values = new Dictionary<string, string>
        {
            ["SLACK_WEBHOOK_CCODE"] = "https://example.invalid/actor",
            ["SLACK_WEBHOOK_DEFAULT"] = "https://example.invalid/default"
        };

        var resolved = WebhookResolver.Resolve(values, "ccode");

        Assert.Equal("https://example.invalid/actor", resolved?.ToString());
    }

    [Fact]
    public void Falls_back_to_default_webhook()
    {
        var values = new Dictionary<string, string>
        {
            ["SLACK_WEBHOOK_DEFAULT"] = "https://example.invalid/default"
        };

        var resolved = WebhookResolver.Resolve(values, "codex");

        Assert.Equal("https://example.invalid/default", resolved?.ToString());
    }

    [Fact]
    public void Missing_config_returns_null()
    {
        var resolved = WebhookResolver.Resolve(new Dictionary<string, string>(), "sl");

        Assert.Null(resolved);
    }

    [Fact]
    public void Masks_slack_webhook_secret()
    {
        var masked = WebhookResolver.Mask(new Uri("https://hooks.slack.com/services/"));

        Assert.Equal("https://hooks.slack.com/services/***", masked);
    }

    [Fact]
    public async Task Posts_expected_json_payload()
    {
        var handler = new RecordingHandler();
        var notifier = new SlackNotifier(handler, (_, _) => Task.CompletedTask);
        var options = new NotifyOptions("codex", "warn", "outil echoue", "SERZENIA-146");

        await notifier.SendAsync(new Uri("https://example.invalid/webhook"), options, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("application/json", handler.Request?.Content?.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", handler.Request?.Content?.Headers.ContentType?.CharSet?.ToLowerInvariant());

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("[CODEX] warn \u2014 outil echoue\ncontext: SERZENIA-146", json.RootElement.GetProperty("text").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
