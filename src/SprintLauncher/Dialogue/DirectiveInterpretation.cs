using System.Text.Json;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;

namespace SprintLauncher.Dialogue;

public sealed class DirectiveInterpretation
{
    public string Intent { get; init; } = "directive";
    public List<ActorRole> TargetActors { get; init; } = [];
    public List<ActorGroup> TargetGroups { get; init; } = [];
    public PendingPhaseOrder? PhaseOrder { get; init; }

    public bool HasTargets => TargetActors.Count > 0 || TargetGroups.Count > 0;

    public IReadOnlyList<DirectiveAddress> ToAddresses(string text)
    {
        var result = new List<DirectiveAddress>();
        result.AddRange(TargetActors.Distinct().Select(a => new DirectiveAddress(a, null, text)));
        result.AddRange(TargetGroups.Distinct().Select(g => new DirectiveAddress(null, g, text)));
        return result;
    }
}

public static class DirectiveInterpretationParser
{
    public static DirectiveInterpretation? TryParse(string? raw, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJsonObject(raw);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var actors = ReadTargets<ActorRole>(root, "actors", "actor", "targetActors", "target_actors", "acteurs");
            var groups = ReadTargets<ActorGroup>(root, "groups", "group", "targetGroups", "target_groups", "groupes");
            ReadNestedTargets(root, actors, groups);

            return new DirectiveInterpretation
            {
                Intent = FirstString(root, "intent", "intention") ?? "directive",
                TargetActors = actors,
                TargetGroups = groups,
                PhaseOrder = TryReadPhaseOrder(root, sourceText),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ReadNestedTargets(JsonElement root, List<ActorRole> actors, List<ActorGroup> groups)
    {
        foreach (var name in new[] { "targets", "target", "cibles", "cible" })
        {
            if (!TryGetProperty(root, name, out var target)) continue;
            if (target.ValueKind == JsonValueKind.Object)
            {
                actors.AddRange(ReadTargets<ActorRole>(target, "actors", "actor", "acteurs", "roles", "roles_acteurs"));
                groups.AddRange(ReadTargets<ActorGroup>(target, "groups", "group", "groupes"));
            }
            else
            {
                ReadGenericTargets(target, actors, groups);
            }
        }
    }

    private static PendingPhaseOrder? TryReadPhaseOrder(JsonElement root, string sourceText)
    {
        JsonElement phaseRoot = root;
        foreach (var name in new[] { "phase_order", "phaseOrder", "ordre_phase", "phase" })
        {
            if (TryGetProperty(root, name, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                phaseRoot = nested;
                break;
            }
        }

        var action = FirstString(phaseRoot, "action", "kind", "type", "ordre", "order");
        var phase = FirstString(phaseRoot, "phase", "target_phase", "targetPhase", "target_group", "targetGroup", "target", "group", "groupe", "cible");
        if (action is null || phase is null) return null;

        var kind = PhaseOrderQueue.ParseKind(action);
        var group = PhaseOrderQueue.ParsePhase(phase);
        if (kind is null || group is null) return null;

        return new PendingPhaseOrder
        {
            Kind = kind.Value,
            TargetGroup = group.Value.ToString(),
            SourceText = sourceText,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static List<TEnum> ReadTargets<TEnum>(JsonElement root, params string[] names)
        where TEnum : struct, Enum
    {
        var result = new List<TEnum>();
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var el)) continue;
            foreach (var token in ReadStrings(el))
            {
                if (TryParseTarget<TEnum>(token) is { } parsed && !result.Contains(parsed))
                    result.Add(parsed);
            }
        }
        return result;
    }

    private static void ReadGenericTargets(JsonElement el, List<ActorRole> actors, List<ActorGroup> groups)
    {
        foreach (var token in ReadStrings(el))
        {
            if (TryParseTarget<ActorRole>(token) is { } actor && !actors.Contains(actor))
                actors.Add(actor);
            if (TryParseTarget<ActorGroup>(token) is { } group && !groups.Contains(group))
                groups.Add(group);
        }
    }

    private static TEnum? TryParseTarget<TEnum>(string token) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(token, ignoreCase: true, out var exact))
            return exact;

        var parsed = DirectiveAddressing.Parse("@" + token + " x");
        if (typeof(TEnum) == typeof(ActorRole) && parsed.Actor is { } actor)
            return (TEnum)(object)actor;
        if (typeof(TEnum) == typeof(ActorGroup) && parsed.Group is { } group)
            return (TEnum)(object)group;
        return null;
    }

    private static IEnumerable<string> ReadStrings(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) yield return s;
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    yield return item.GetString()!;
        }
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(root, name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var value = el.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        return null;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value)) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }
}
