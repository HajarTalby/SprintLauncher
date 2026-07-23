using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SprintLauncher.Notifications;

/// <summary>
/// Ecoute les messages Slack en Socket Mode et les range dans l'inbox de leur acteur.
/// La reception est volontairement independante du pipeline : le lot 2 fera le lien
/// entre ces inbox d'acteurs et les inbox de roles d'un run.
/// </summary>
public sealed class SlackSocketListener
{
    private const string ConnectionsOpenUrl = "https://slack.com/api/apps.connections.open";
    private readonly HttpClient _httpClient;

    public SlackSocketListener(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>Lance la boucle persistante Socket Mode jusqu'a annulation.</summary>
    public async Task RunAsync(string liveDir, CancellationToken ct)
    {
        var appToken = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN");
        if (string.IsNullOrWhiteSpace(appToken))
            throw new InvalidOperationException("SLACK_APP_TOKEN est requis pour le listener Slack.");

        var channelMap = new ChannelActorMap(new Dictionary<string, string>
        {
            ["SLACK_CHANNEL_CCODE"] = Environment.GetEnvironmentVariable("SLACK_CHANNEL_CCODE") ?? string.Empty,
            ["SLACK_CHANNEL_AG"] = Environment.GetEnvironmentVariable("SLACK_CHANNEL_AG") ?? string.Empty,
            ["SLACK_CHANNEL_CODEX"] = Environment.GetEnvironmentVariable("SLACK_CHANNEL_CODEX") ?? string.Empty,
            ["SLACK_CHANNEL_SL"] = Environment.GetEnvironmentVariable("SLACK_CHANNEL_SL") ?? string.Empty,
        });

        Console.WriteLine("[slack] Listener Socket Mode demarre.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var socketUrl = await OpenConnectionAsync(appToken, ct);
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(socketUrl), ct);
                Console.WriteLine("[slack] Connecte en Socket Mode.");

                await ReceiveLoopAsync(socket, channelMap, liveDir, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Les secrets et l'URL websocket ne doivent jamais etre affiches.
                Console.Error.WriteLine($"[slack] Connexion interrompue ({ex.GetType().Name}).");
            }

            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        Console.WriteLine("[slack] Listener arrete.");
    }

    /// <summary>Parse une enveloppe Socket Mode sans lever sur les champs facultatifs absents.</summary>
    public static ParsedEnvelope Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = GetString(root, "type");
            var envelopeId = GetString(root, "envelope_id");

            if (!string.Equals(type, "events_api", StringComparison.Ordinal))
                return new ParsedEnvelope(type, envelopeId, null, null, null, null, null, null, null);

            var payload = GetObject(root, "payload");
            var messageEvent = payload is { } payloadElement ? GetObject(payloadElement, "event") : null;
            if (messageEvent is not { } eventElement)
                return new ParsedEnvelope(type, envelopeId, null, null, null, null, null, null, null);

            return new ParsedEnvelope(
                type,
                envelopeId,
                GetString(eventElement, "type"),
                GetString(eventElement, "channel"),
                GetString(eventElement, "text"),
                GetString(eventElement, "subtype"),
                GetString(eventElement, "bot_id"),
                GetString(eventElement, "app_id"),
                GetString(eventElement, "user"));
        }
        catch (JsonException)
        {
            return new ParsedEnvelope(null, null, null, null, null, null, null, null, null);
        }
    }

    /// <summary>Construit l'accuse de reception attendu par Slack.</summary>
    public static string BuildAck(string envelopeId) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["envelope_id"] = envelopeId });

    /// <summary>Vrai pour les notifications de bot, qui ne doivent jamais revenir dans une inbox.</summary>
    public static bool IsFromBot(ParsedEnvelope envelope) =>
        string.Equals(envelope.Subtype, "bot_message", StringComparison.OrdinalIgnoreCase)
        || envelope.BotId is not null
        || envelope.AppId is not null;

    /// <summary>Ne retient que les messages sans subtype technique.</summary>
    public static bool IsHumanMessage(ParsedEnvelope envelope) =>
        string.Equals(envelope.EventType, "message", StringComparison.Ordinal)
        && string.IsNullOrWhiteSpace(envelope.Subtype);

    /// <summary>Ajoute une intervention a l'inbox de l'acteur, une ligne UTF-8 par message.</summary>
    public static void AppendToInbox(string liveDir, string actorKey, string text)
    {
        Directory.CreateDirectory(liveDir);
        var path = Path.Combine(liveDir, $"live-input-{actorKey}.txt");
        var oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(oneLine);
        writer.Write('\n');
    }

    private async Task<string> OpenConnectionAsync(string appToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ConnectionsOpenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        var url = GetString(root, "url");
        if (!ok || string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("apps.connections.open a echoue.");

        return url;
    }

    private static async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelActorMap channelMap,
        string liveDir,
        CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.MessageType == WebSocketMessageType.Text)
                    message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var envelope = Parse(Encoding.UTF8.GetString(message.ToArray()));
            if (string.Equals(envelope.Type, "disconnect", StringComparison.Ordinal))
            {
                Console.WriteLine("[slack] Reconnexion demandee par Slack.");
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Reconnect", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // La fermeture est deja en cours : le using reconnectera quand meme.
                }
                return;
            }

            if (!string.Equals(envelope.Type, "events_api", StringComparison.Ordinal))
                continue;

            if (envelope.EnvelopeId is not null)
                await SendTextAsync(socket, BuildAck(envelope.EnvelopeId), ct);

            if (IsFromBot(envelope) || !IsHumanMessage(envelope) || envelope.Channel is null)
                continue;

            var actorKey = channelMap.ResolveActor(envelope.Channel);
            if (actorKey is null)
                continue;

            var text = envelope.Text ?? string.Empty;
            AppendToInbox(liveDir, actorKey, text);
            Console.WriteLine($"[slack] #{actorKey} → \"{TruncateForLog(text)}\"");
        }
    }

    private static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static string TruncateForLog(string text)
    {
        var oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 160 ? oneLine : $"{oneLine[..160]}...";
    }

    private static JsonElement? GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

/// <summary>Representation testable d'une enveloppe Socket Mode.</summary>
public sealed record ParsedEnvelope(
    string? Type,
    string? EnvelopeId,
    string? EventType,
    string? Channel,
    string? Text,
    string? Subtype,
    string? BotId,
    string? AppId,
    string? User);

/// <summary>Mapping inverse entre les identifiants de canaux Slack et les cles d'acteurs.</summary>
public sealed class ChannelActorMap
{
    private static readonly (string EnvKey, string ActorKey)[] KnownChannels =
    [
        ("SLACK_CHANNEL_CCODE", "ccode"),
        ("SLACK_CHANNEL_AG", "ag"),
        ("SLACK_CHANNEL_CODEX", "codex"),
        ("SLACK_CHANNEL_SL", "sl"),
    ];

    private readonly Dictionary<string, string> _actorsByChannel = new(StringComparer.OrdinalIgnoreCase);

    public ChannelActorMap(IReadOnlyDictionary<string, string> environment)
    {
        foreach (var (envKey, actorKey) in KnownChannels)
        {
            var value = environment.FirstOrDefault(pair =>
                string.Equals(pair.Key, envKey, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(value))
                _actorsByChannel[value] = actorKey;
        }
    }

    public string? ResolveActor(string channelId) =>
        _actorsByChannel.TryGetValue(channelId, out var actorKey) ? actorKey : null;
}
