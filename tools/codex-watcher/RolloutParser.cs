using System.Globalization;

namespace SprintLauncher.CodexWatcher;

public sealed class RolloutParser
{
    /// <summary>
    /// Parses complete JSONL content. The first session_meta wins; later metadata is deliberately ignored.
    /// </summary>
    public RolloutParseResult Parse(string content, SessionMeta? knownSessionMeta = null) =>
        ParseLines((content ?? "").Split('\n'), knownSessionMeta);

    /// <summary>
    /// Parses complete lines read from an append-only rollout. A prior meta is supplied for incremental reads.
    /// </summary>
    public RolloutParseResult ParseLines(IEnumerable<string> lines, SessionMeta? knownSessionMeta = null)
    {
        var sessionMeta = knownSessionMeta;
        var completedTurns = new List<CompletedTurn>();

        foreach (var line in lines)
        {
            if (!RolloutEvent.TryParse(line, out var rolloutEvent) || rolloutEvent is null) continue;

            if (rolloutEvent.Type == "session_meta")
            {
                sessionMeta ??= SessionMeta.FromPayload(rolloutEvent.Payload);
                continue;
            }

            if (rolloutEvent.Type != "event_msg" || rolloutEvent.PayloadType != "task_complete") continue;

            var turnId = RolloutEvent.GetString(rolloutEvent.Payload, "turn_id");
            if (string.IsNullOrWhiteSpace(turnId)) continue;

            completedTurns.Add(new CompletedTurn(
                turnId,
                RolloutEvent.GetString(rolloutEvent.Payload, "last_agent_message"),
                ParseCompletedAt(rolloutEvent.Payload)));
        }

        return new RolloutParseResult(sessionMeta, completedTurns);
    }

    private static DateTimeOffset? ParseCompletedAt(System.Text.Json.JsonElement payload)
    {
        var completedAt = RolloutEvent.GetString(payload, "completed_at");
        if (DateTimeOffset.TryParse(completedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            return timestamp;

        if (payload.TryGetProperty("completed_at", out var unixValue) && unixValue.TryGetInt64(out var unixSeconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { }
        }

        return null;
    }
}
