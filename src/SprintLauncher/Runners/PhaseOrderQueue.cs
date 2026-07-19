using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public enum PhaseOrderKind
{
    Replay,
    Insert,
    SkipTo,
}

public sealed class PendingPhaseOrder
{
    public PhaseOrderKind Kind { get; set; }
    public string TargetGroup { get; set; } = "";
    public string SourceText { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Applied { get; set; }
}

public sealed record PhaseOrderApplication(ActorGroup Target, PhaseOrderKind Kind, int NextIndex);

public static class PhaseOrderQueue
{
    public static PhaseOrderKind? ParseKind(string raw)
    {
        var token = Normalize(raw);
        return token switch
        {
            "replay" or "rejouer" or "relancer" or "restart" or "rerun" => PhaseOrderKind.Replay,
            "insert" or "inserer" or "insere" or "ajouter" or "add" => PhaseOrderKind.Insert,
            "skipto" or "sauter" or "aller" or "goto" or "go" => PhaseOrderKind.SkipTo,
            _ => null,
        };
    }

    public static ActorGroup? ParsePhase(string raw)
    {
        var token = Normalize(raw);
        if (Enum.TryParse<ActorGroup>(raw, ignoreCase: true, out var exact))
            return exact;

        return token switch
        {
            "analyse" or "analysis" => ActorGroup.Analysis,
            "claude" or "familleclaude" or "familyclaude" => ActorGroup.FamilyClaude,
            "gpt" or "codex" or "famillegpt" or "familygpt" => ActorGroup.FamilyGpt,
            "pilotage" or "comitepilotage" or "committeepilotage" => ActorGroup.CommitteePilotage,
            "arbitrage" or "comitearbitrage" or "committeearbitrage" => ActorGroup.CommitteeArbitrage,
            "qa" or "quality" or "qualite" => ActorGroup.Qa,
            "retro" or "retrospective" => ActorGroup.Retrospective,
            _ => null,
        };
    }

    public static PhaseOrderApplication? ApplyNext(
        IList<ActorGroup> phaseOrder,
        IList<PendingPhaseOrder> pendingOrders,
        int currentIndex,
        Action<ActorGroup> clearCompletion)
    {
        var order = pendingOrders.FirstOrDefault(o => !o.Applied);
        if (order is null) return null;
        if (!Enum.TryParse<ActorGroup>(order.TargetGroup, ignoreCase: true, out var target))
        {
            order.Applied = true;
            return null;
        }

        switch (order.Kind)
        {
            case PhaseOrderKind.Replay:
            case PhaseOrderKind.Insert:
                clearCompletion(target);
                phaseOrder.Insert(Math.Min(currentIndex + 1, phaseOrder.Count), target);
                order.Applied = true;
                return new PhaseOrderApplication(target, order.Kind, currentIndex + 1);

            case PhaseOrderKind.SkipTo:
                var existing = -1;
                for (var i = currentIndex + 1; i < phaseOrder.Count; i++)
                    if (phaseOrder[i] == target) { existing = i; break; }
                if (existing < 0)
                {
                    phaseOrder.Insert(Math.Min(currentIndex + 1, phaseOrder.Count), target);
                    existing = currentIndex + 1;
                }
                order.Applied = true;
                return new PhaseOrderApplication(target, order.Kind, existing);

            default:
                order.Applied = true;
                return null;
        }
    }

    private static string Normalize(string raw) =>
        new([.. (raw ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
