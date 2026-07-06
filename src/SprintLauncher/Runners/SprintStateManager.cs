using System.Text;
using System.Text.Json;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed class SprintState
{
    public DateTimeOffset StartedAt { get; init; }
    public string[] Keys { get; init; } = [];
    public HashSet<string> CompletedRoles { get; init; } = [];
    // Actors that were running when the process was interrupted (Ctrl+C / ProcessExit).
    // --resume replays these from scratch (they did not complete successfully).
    public HashSet<string> InterruptedRoles { get; set; } = [];
    // Groupes collectifs dont la discussion multi-tours est terminée ET publiée (SERZENIA-143).
    // Un groupe absent d'ici avec un transcript dialogue-<groupe>.json existant reprend la discussion en cours.
    public HashSet<string> CompletedGroups { get; set; } = [];
    // Litige signalé par la session d'analyse ([LITIGE: ...]) — conditionne la convocation
    // du comité d'arbitrage, y compris après --resume.
    public bool LitigeDetected { get; set; }
    // Ordonnanceur d'implémentation (SERZENIA-143 lot 5) : dernier moteur utilisé
    // (alternance), moteurs à quota épuisé, US déjà implémentées (reprise per-US).
    public string? LastImplementer { get; set; }
    public HashSet<string> QuotaExhaustedEngines { get; set; } = [];
    public HashSet<string> CompletedUsImplementations { get; set; } = [];
    public DateTimeOffset? LastCompletedAt { get; set; }
}

public static class SprintStateManager
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static async Task SaveAsync(string stateFile, SprintState state, CancellationToken ct = default)
    {
        state.LastCompletedAt = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(state, _json), ct);
    }

    public static async Task<SprintState?> TryLoadAsync(string stateFile, CancellationToken ct = default)
    {
        if (!File.Exists(stateFile)) return null;
        var json = await File.ReadAllTextAsync(stateFile, ct);
        return JsonSerializer.Deserialize<SprintState>(json, _json);
    }

    public static async Task WriteHandoffAsync(string handoffFile, SprintState state, CancellationToken ct = default)
    {
        var allRoles = Enum.GetValues<ActorRole>().Select(r => r.ToString()).ToArray();
        var remaining = allRoles.Where(r => !state.CompletedRoles.Contains(r)).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("# Session handoff");
        sb.AppendLine();
        sb.AppendLine($"Date : {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Issues : {string.Join(", ", state.Keys)}");
        sb.AppendLine($"Démarré : {state.StartedAt:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("## Acteurs terminés");
        foreach (var r in state.CompletedRoles)
            sb.AppendLine($"- {r} ✓");
        sb.AppendLine();
        sb.AppendLine("## Acteurs restants");
        if (remaining.Length == 0)
            sb.AppendLine("Aucun — sprint complet.");
        else
            foreach (var r in remaining)
                sb.AppendLine($"- {r}");
        sb.AppendLine();
        sb.AppendLine("## Pour reprendre");
        sb.AppendLine("```powershell");
        sb.AppendLine($"dotnet run --project tools/sprint-launcher -- {string.Join(" ", state.Keys)} --resume");
        sb.AppendLine("```");

        await File.WriteAllTextAsync(handoffFile, sb.ToString(), ct);
    }
}
