using System.Text.Json;

namespace SprintLauncher.CodexWatcher;

public sealed record SessionMeta(
    string Cwd,
    string Originator,
    string ThreadSource,
    bool HasSubagent)
{
    public static SessionMeta Empty { get; } = new("", "", "", false);

    internal static SessionMeta FromPayload(JsonElement payload)
    {
        return new SessionMeta(
            GetString(payload, "cwd"),
            GetString(payload, "originator"),
            GetString(payload, "thread_source"),
            HasProperty(payload, "source", "subagent"));
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool HasProperty(JsonElement element, string parent, string child) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(parent, out var parentValue) &&
        parentValue.ValueKind == JsonValueKind.Object &&
        parentValue.TryGetProperty(child, out _);
}

public sealed record CompletedTurn(string TurnId, string LastAgentMessage, DateTimeOffset? CompletedAt);

/// <summary>A tolerant representation of one JSONL rollout line.</summary>
public sealed record RolloutEvent(string Type, string PayloadType, JsonElement Payload)
{
    public static bool TryParse(string? line, out RolloutEvent? rolloutEvent)
    {
        rolloutEvent = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var type = GetString(root, "type");
            var payload = root.TryGetProperty("payload", out var candidate) &&
                          candidate.ValueKind == JsonValueKind.Object
                ? candidate.Clone()
                : default;
            var payloadType = payload.ValueKind == JsonValueKind.Object ? GetString(payload, "type") : "";
            rolloutEvent = new RolloutEvent(type, payloadType, payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsApprovalRequest(string? line) => false;

    internal static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

public sealed record RolloutParseResult(SessionMeta? SessionMeta, IReadOnlyList<CompletedTurn> CompletedTurns);
