namespace SprintLauncher.Notify;

public static class WebhookResolver
{
    public static Uri? Resolve(IReadOnlyDictionary<string, string> values, string actor)
    {
        var actorKey = $"SLACK_WEBHOOK_{actor.ToUpperInvariant()}";
        var raw = GetNonEmpty(values, actorKey) ?? GetNonEmpty(values, "SLACK_WEBHOOK_DEFAULT");
        if (raw is null) return null;

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
    }

    public static string Mask(Uri uri)
    {
        if (uri.Host.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://hooks.slack.com/services/***";
        }

        return $"{uri.Scheme}://{uri.Host}/***";
    }

    private static string? GetNonEmpty(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
