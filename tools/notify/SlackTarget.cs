namespace SprintLauncher.Notify;

/// <summary>Cible d'envoi Slack résolue depuis le .env : un bot token + un canal.</summary>
public sealed record SlackTarget(string Token, string Channel);

/// <summary>
/// Résolution bot token + canal par acteur (SERZENIA-146, bascule webhooks → bot token
/// le 2026-07-23 : Hajar installe l'app une fois, l'agent crée les canaux et poste lui-même).
///
/// .env : <c>SLACK_BOT_TOKEN=xoxb-…</c> (unique) et, en option, un canal par acteur
/// <c>SLACK_CHANNEL_CCODE / _AG / _CODEX / _SL</c> (id ou nom). Par défaut le canal vaut le
/// nom de l'acteur (ccode/ag/codex/sl).
/// </summary>
public static class SlackTargetResolver
{
    public static SlackTarget? Resolve(IReadOnlyDictionary<string, string> values, string actor)
    {
        var token = GetNonEmpty(values, "SLACK_BOT_TOKEN");
        if (token is null) return null;

        var channel = GetNonEmpty(values, $"SLACK_CHANNEL_{actor.ToUpperInvariant()}")
            ?? actor.ToLowerInvariant();
        return new SlackTarget(token, channel);
    }

    /// <summary>Masque le token pour l'affichage : jamais le secret en clair.</summary>
    public static string MaskToken(string token) =>
        token.StartsWith("xoxb-", StringComparison.OrdinalIgnoreCase) ? "xoxb-***" : "***";

    private static string? GetNonEmpty(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
