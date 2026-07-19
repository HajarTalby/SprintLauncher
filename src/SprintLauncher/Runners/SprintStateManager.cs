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
    // Revues croisées dues — PERSISTÉES : elles survivent aux interruptions/relances
    // (constat sprint 6 : file en mémoire → 1 seule revue faite sur 9 US).
    public List<PendingReview> PendingReviews { get; set; } = [];
    // Cycles de remédiation déjà joués (bornés par MAX_REMEDIATION_CYCLES).
    public int RemediationCycles { get; set; }
    // Pause douce demandée : le run termine l'acteur en cours, puis attend une reprise
    // avant de lancer le tour suivant. Persisté pour survivre à --resume.
    public bool PauseRequested { get; set; }
    public DateTimeOffset? PauseRequestedAt { get; set; }
    // Directives données par Hajar mais PAS encore publiées sur Jira : stockées
    // (onglet DÉCISIONS + registre injecté aux acteurs) et publiées uniquement quand
    // un acteur commente sur l'US liée au sujet (choix de Hajar, 2026-07). Persistées
    // pour survivre aux interruptions/--resume.
    public List<PendingDirective> PendingDirectives { get; set; } = [];
    public DateTimeOffset? LastCompletedAt { get; set; }
}

/// <summary>Revue croisée due sur une US implémentée (réviseur = l'autre moteur).</summary>
public sealed record PendingReview(string Key, string Implementer, string? ReliefFrom);

/// <summary>
/// Directive de Hajar en attente : liée à un sujet (US), stockée sans écriture Jira ;
/// publiée seulement quand un acteur commente ce sujet. Classe mutable (Published change).
/// </summary>
public sealed class PendingDirective
{
    public string SubjectKey { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Published { get; set; }

    // Adressage (2026-07-16) : nom d'ActorRole ou d'ActorGroup ; null = tous les acteurs.
    // Une directive adressée attend SON destinataire au lieu d'être appliquée au
    // premier acteur venu.
    public string? TargetActor { get; set; }
    public string? TargetGroup { get; set; }

    // Injectée au moins une fois dans le prompt de sa cible. Distinct de Published,
    // qui concerne l'écriture Jira : une directive peut être appliquée sans être
    // encore publiée, et le run peut mourir entre les deux.
    public bool Delivered { get; set; }

    // Pièces jointes (SERZENIA-144 Lot 3) : chemins SOURCES (poste de Hajar), pas
    // encore copiés. La copie vers le dossier du run se fait à la livraison
    // (DirectivesForActor), une fois la cible réelle connue.
    public List<string> AttachmentSourcePaths { get; set; } = [];
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
