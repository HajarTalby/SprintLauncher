using System.Text;
using System.Text.Json;
using SprintLauncher.Jira;
using SprintLauncher.Memory;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;

namespace SprintLauncher.Dialogue;

/// <summary>
/// Une prise de parole dans une discussion : acteur IA ou intervention de l'approbatrice.
/// Round = numéro d'aller-retour (1..MaxRounds) ; les interventions portent le round en cours.
/// </summary>
public sealed record DialogueTurn(
    string Speaker,
    string Content,
    DateTimeOffset At,
    int Round,
    bool IsIntervention);

public enum DialogueEndReason
{
    Converged,       // un acteur a émis le marqueur de convergence
    ForcedSynthesis, // plafond de tours atteint (ou clôture demandée) → tour de synthèse forcé
    Stopped,         // arrêt demandé (Ctrl+C, ARRÊT, 'n') — reprise possible via --resume
    ActorFailed,     // un acteur a échoué — la discussion ne peut pas continuer
}

public sealed record DialogueOutcome(
    IReadOnlyList<DialogueTurn> Turns,
    DialogueEndReason EndReason,
    ActorRole? FailedRole)
{
    public bool Success => EndReason is DialogueEndReason.Converged or DialogueEndReason.ForcedSynthesis;

    /// <summary>Contenu du dernier tour acteur — la synthèse/décision finale de la discussion.</summary>
    public string? FinalContribution => Turns.LastOrDefault(t => !t.IsIntervention)?.Content;
}

public enum InterventionKind
{
    Continue, // poursuivre la discussion sans intervenir
    Stop,     // arrêter le run (reprise via --resume)
    Conclude, // sauter directement au tour de synthèse finale
    Message,  // injecter un message de l'approbatrice dans la discussion
}

public sealed record DialogueIntervention(InterventionKind Kind, string? Text = null);

/// <summary>
/// Moteur de discussion multi-tours : les participants alternent (A → B → A → B …),
/// chacun lisant le transcript complet, jusqu'à convergence explicite ou plafond de tours.
/// Entre chaque round, l'approbatrice peut intervenir (message injecté avec autorité),
/// demander la clôture, ou arrêter. Le transcript est persisté après chaque tour
/// (JSON pour la reprise --resume, markdown pour la lecture humaine).
/// </summary>
public sealed class DialogueEngine
{
    public const string ConsensusMarker = "[CONSENSUS]";
    public const string FinalDecisionMarker = "[DECISION FINALE]";

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly int _maxRounds;
    private readonly string _approverLabel;
    private readonly bool _interventionEveryTurn;

    public DialogueEngine(int maxRounds, string approverName, bool interventionEveryTurn = false)
    {
        _maxRounds = maxRounds > 0 ? maxRounds : 3;
        // Le nom seul, sans titre ni qualificatif (demande explicite de Hajar).
        _approverLabel = approverName;
        // true : checkpoint après CHAQUE prise de parole (pas seulement entre rounds).
        _interventionEveryTurn = interventionEveryTurn;
    }

    public static bool HasConvergenceMarker(string content) =>
        content.Contains(ConsensusMarker, StringComparison.OrdinalIgnoreCase) ||
        content.Contains(FinalDecisionMarker, StringComparison.OrdinalIgnoreCase);

    /// <param name="participants">Ordre de prise de parole ; le dernier assume la synthèse forcée.</param>
    /// <param name="buildPrompt">Construit le prompt d'un tour (rôle, transcript, round, synthèse?).</param>
    /// <param name="runTurn">Exécute un tour acteur (subprocess, timer, artefacts) — fourni par l'appelant.</param>
    /// <param name="requestIntervention">Checkpoint entre rounds (null = jamais d'intervention).</param>
    /// <param name="resumedTurns">Transcript rechargé depuis les artefacts pour --resume.</param>
    /// <param name="validateConclusion">Garde de complétude : reçoit la conclusion proposée, retourne null
    /// si acceptable, sinon la raison du refus — la discussion continue avec un tour de rattrapage
    /// (max <paramref name="maxRescueTurns"/>) au lieu de converger sur un résultat incomplet.</param>
    public async Task<DialogueOutcome> RunAsync(
        IReadOnlyList<ActorRole> participants,
        Func<ActorRole, IReadOnlyList<DialogueTurn>, int, bool, ActorPrompt> buildPrompt,
        Func<ActorRole, ActorPrompt, CancellationToken, Task<ActorRunResult>> runTurn,
        Func<int, Task<DialogueIntervention>>? requestIntervention,
        string transcriptBasePath,
        IReadOnlyList<DialogueTurn>? resumedTurns = null,
        Func<string, string?>? validateConclusion = null,
        int maxRescueTurns = 2,
        CancellationToken ct = default)
    {
        if (participants.Count == 0)
            throw new ArgumentException("Une discussion nécessite au moins un participant.", nameof(participants));

        var turns = new List<DialogueTurn>(resumedTurns ?? []);
        // Un transcript repris peut déjà contenir la convergence (interruption juste avant publication).
        if (turns.Count > 0 && !turns[^1].IsIntervention && HasConvergenceMarker(turns[^1].Content)
            && validateConclusion?.Invoke(turns[^1].Content) is null)
            return new DialogueOutcome(turns, DialogueEndReason.Converged, null);

        bool concludeRequested = false;
        int rescuesUsed = 0;

        // Refuse une conclusion incomplète : injecte la raison comme directive et
        // prolonge la discussion d'un tour de rattrapage. Budget épuisé → on accepte
        // avec le signalement dans le transcript (jamais de boucle infinie).
        async Task<bool> ConclusionRejectedAsync(string content, int round)
        {
            var issue = validateConclusion?.Invoke(content);
            if (issue is null || rescuesUsed >= maxRescueTurns) return false;
            rescuesUsed++;
            turns.Add(new DialogueTurn("Sprint Launcher (garde de complétude)",
                $"Conclusion refusée : {issue} Complète la discussion avant de conclure à nouveau.",
                DateTimeOffset.UtcNow, round, IsIntervention: true));
            await PersistAsync(turns, transcriptBasePath, ct);
            return true;
        }

        while (true)
        {
            if (ct.IsCancellationRequested)
                return new DialogueOutcome(turns, DialogueEndReason.Stopped, null);

            int actorTurns = turns.Count(t => !t.IsIntervention);
            int round = actorTurns / participants.Count + 1;
            bool atCheckpoint = actorTurns > 0 &&
                (_interventionEveryTurn || actorTurns % participants.Count == 0);

            // ── Checkpoint intervention (entre rounds, ou à chaque tour) ──────
            // Boucle : l'approbatrice peut enchaîner PLUSIEURS interventions au
            // même checkpoint — la discussion ne repart que sur GO/Conclure.
            while (atCheckpoint && !concludeRequested && requestIntervention is not null)
            {
                var intervention = await requestIntervention(round);
                if (intervention.Kind == InterventionKind.Stop)
                    return new DialogueOutcome(turns, DialogueEndReason.Stopped, null);
                if (intervention.Kind == InterventionKind.Conclude)
                {
                    concludeRequested = true;
                    break;
                }
                if (intervention.Kind == InterventionKind.Message && !string.IsNullOrWhiteSpace(intervention.Text))
                {
                    turns.Add(new DialogueTurn(_approverLabel, intervention.Text.Trim(),
                        DateTimeOffset.UtcNow, round, IsIntervention: true));
                    await PersistAsync(turns, transcriptBasePath, ct);
                    continue; // re-propose le checkpoint : intervention suivante possible
                }
                break; // Continue → la discussion repart
            }

            // ── Plafond atteint ou clôture demandée → tour de synthèse forcé ──
            if (concludeRequested || round > _maxRounds)
            {
                var synthesist = participants[^1];
                var synthPrompt = buildPrompt(synthesist, turns, Math.Min(round, _maxRounds), true);
                var synthResult = await runTurn(synthesist, synthPrompt, ct);
                if (!synthResult.Success)
                {
                    return new DialogueOutcome(turns,
                        ct.IsCancellationRequested ? DialogueEndReason.Stopped : DialogueEndReason.ActorFailed,
                        synthesist);
                }
                turns.Add(new DialogueTurn(synthesist.ToString(), synthResult.Output.Trim(),
                    DateTimeOffset.UtcNow, Math.Min(round, _maxRounds), IsIntervention: false));
                await PersistAsync(turns, transcriptBasePath, ct);

                if (await ConclusionRejectedAsync(synthResult.Output, Math.Min(round, _maxRounds)))
                    continue; // nouveau tour de synthèse avec la directive de complétude

                return new DialogueOutcome(turns, DialogueEndReason.ForcedSynthesis, null);
            }

            // ── Tour normal ────────────────────────────────────────────────────
            var role = participants[actorTurns % participants.Count];
            var prompt = buildPrompt(role, turns, round, false);
            var result = await runTurn(role, prompt, ct);

            if (!result.Success)
            {
                return new DialogueOutcome(turns,
                    ct.IsCancellationRequested ? DialogueEndReason.Stopped : DialogueEndReason.ActorFailed,
                    role);
            }

            turns.Add(new DialogueTurn(role.ToString(), result.Output.Trim(),
                DateTimeOffset.UtcNow, round, IsIntervention: false));
            await PersistAsync(turns, transcriptBasePath, ct);

            if (HasConvergenceMarker(result.Output))
            {
                if (await ConclusionRejectedAsync(result.Output, round))
                    continue; // convergence refusée — la discussion se poursuit

                return new DialogueOutcome(turns, DialogueEndReason.Converged, null);
            }
        }
    }

    // ── Persistance : JSON (reprise --resume) + markdown (lecture humaine) ────
    private static async Task PersistAsync(List<DialogueTurn> turns, string basePath, CancellationToken ct)
    {
        // Persistance best-effort même en cours d'annulation — le transcript est la donnée de reprise.
        await File.WriteAllTextAsync(basePath + ".json", JsonSerializer.Serialize(turns, _json), CancellationToken.None);
        await File.WriteAllTextAsync(basePath + ".md", FormatMarkdown(turns), CancellationToken.None);
    }

    public static async Task<List<DialogueTurn>?> TryLoadTranscriptAsync(string basePath, CancellationToken ct = default)
    {
        var jsonPath = basePath + ".json";
        if (!File.Exists(jsonPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath, ct);
            return JsonSerializer.Deserialize<List<DialogueTurn>>(json, _json);
        }
        catch (JsonException)
        {
            return null; // transcript corrompu → la discussion repart de zéro
        }
    }

    public static string FormatMarkdown(IReadOnlyList<DialogueTurn> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Transcript de discussion");
        sb.AppendLine();
        foreach (var t in turns)
        {
            var header = t.IsIntervention
                ? $"## ⚑ Intervention — {t.Speaker} (round {t.Round})"
                : $"## {t.Speaker} — round {t.Round}";
            sb.AppendLine(header);
            sb.AppendLine($"_{t.At:yyyy-MM-dd HH:mm} UTC_");
            sb.AppendLine();
            sb.AppendLine(t.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Formate le transcript pour injection dans le prompt du tour suivant.
    /// Les interventions de l'approbatrice sont marquées comme ayant autorité.
    /// </summary>
    public static string FormatForPrompt(IReadOnlyList<DialogueTurn> turns)
    {
        var sb = new StringBuilder();
        foreach (var t in turns)
        {
            sb.AppendLine(t.IsIntervention
                ? $"### Intervention de {t.Speaker} — directive à respecter, elle tranche la discussion"
                : $"### {t.Speaker} (round {t.Round})");
            sb.AppendLine(t.Content);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
