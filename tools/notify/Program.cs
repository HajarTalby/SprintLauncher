using SprintLauncher.Notify;

if (!NotifyOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

var envPath = EnvFile.FindRepoEnvFile(Directory.GetCurrentDirectory());
if (envPath is null)
{
    return 0;
}

var values = EnvFile.Load(envPath);
var webhook = WebhookResolver.Resolve(values, options!.Actor);
if (webhook is null)
{
    return 0;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var notifier = new SlackNotifier(httpClient);
    await notifier.SendAsync(webhook, options, timeout.Token);
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SlackNotifyException)
{
    Console.Error.WriteLine($"Slack notification failed for {WebhookResolver.Mask(webhook)}: {ex.Message}");
}

return 0;
