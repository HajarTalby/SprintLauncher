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
    private readonly bool _blindFirstRound;

    public DialogueEngine(int maxRounds, string approverName, bool interventionEveryTurn = false,
        bool blindFirstRound = false)
    {
        _maxRounds = maxRounds > 0 ? maxRounds : 3;
        // Le nom seul, sans titre ni qualificatif (demande explicite de Hajar).
        _approverLabel = approverName;
        // true : checkpoint après CHAQUE prise de parole (pas seulement entre rounds).
        _interventionEveryTurn = interventionEveryTurn;
        // true : au round 1, chaque membre travaille SANS voir ses pairs — il produit une
        // analyse propre. Les tours suivants voient tout et croisent (retour de Hajar,
        // 2026-07-16 : les acteurs GPT se contentaient de valider la lecture de Claude
        // au lieu de produire leur propre analyse). Coût identique : mêmes tours, réordonnés.
        _blindFirstRound = blindFirstRound;
    }

    /// <summary>
    /// Transcript visible par l'acteur qui parle. Au round 1 en mode aveugle, les
    /// contributions des pairs sont masquées — mais JAMAIS les interventions de Hajar,
    /// qui ont autorité et doivent porter dès le premier tour.
    /// </summary>
    private IReadOnlyList<DialogueTurn> VisibleTranscript(IReadOnlyList<DialogueTurn> turns, int round, bool isFinal)
    {
        if (!_blindFirstRound || round > 1 || isFinal) return turns;
        return [.. turns.Where(t => t.IsIntervention)];
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

        // Membres à court de quota : ÉCARTÉS de la rotation, les autres prennent la relève
        // (retour de Hajar, 2026-07-16 : le membre Claude du comité de pilotage est sorti en
        // erreur de quota et le membre GPT n'a jamais pris la relève — la discussion mourait).
        // La relève ne vaut que pour l'épuisement de quota, pas pour un vrai échec d'acteur.
        var exhausted = new HashSet<ActorRole>();
        List<ActorRole> Live() => participants.Where(p => !exhausted.Contains(p)).ToList();

        // Écarte un membre épuisé et laisse une trace dans le transcript (visible SL/Jira,
        // et conservée pour la reprise) : on voit QUI a manqué et que les autres ont pris le relais.
        void MarkExhausted(ActorRole role)
        {
            exhausted.Add(role);
            var others = Live();
            var relief = others.Count > 0
                ? $"Relève assurée par : {string.Join(", ", others)}."
                : "Aucun membre disponible pour la relève.";
            turns.Add(new DialogueTurn("Sprint Launcher (quota)",
                $"{role} à court de quota — écarté de la discussion. {relief}",
                DateTimeOffset.UtcNow, turns.Count(t => !t.IsIntervention) / Math.Max(1, participants.Count) + 1,
                IsIntervention: true));
        }

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

            var live = Live();
            if (live.Count == 0) // tous les membres épuisés — la discussion ne peut plus avancer
                return new DialogueOutcome(turns, DialogueEndReason.ActorFailed, null);

            int actorTurns = turns.Count(t => !t.IsIntervention);
            int round = actorTurns / live.Count + 1;
            bool atCheckpoint = actorTurns > 0 &&
                (_interventionEveryTurn || actorTurns % live.Count == 0);

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
                // La synthèse finale voit TOUJOURS l'intégralité du transcript : c'est
                // elle qui est affichée dans le SL et postée sur Jira (exigence de Hajar).
                // Le synthétiseur est le dernier membre VIVANT (relève si le titulaire est épuisé).
                var synthesist = live[^1];
                var synthPrompt = buildPrompt(synthesist, turns, Math.Min(round, _maxRounds), true);
                var synthResult = await runTurn(synthesist, synthPrompt, ct);
                if (!synthResult.Success)
                {
                    if (ct.IsCancellationRequested)
                        return new DialogueOutcome(turns, DialogueEndReason.Stopped, synthesist);
                    // Épuisé sur la synthèse : on l'écarte et un autre membre vivant reprend.
                    if (synthResult.IsQuotaExhausted && Live().Count > 1)
                    {
                        MarkExhausted(synthesist);
                        await PersistAsync(turns, transcriptBasePath, ct);
                        continue;
                    }
                    return new DialogueOutcome(turns, DialogueEndReason.ActorFailed, synthesist);
                }
                turns.Add(new DialogueTurn(synthesist.ToString(), synthResult.Output.Trim(),
                    DateTimeOffset.UtcNow, Math.Min(round, _maxRounds), IsIntervention: false));
                await PersistAsync(turns, transcriptBasePath, ct);

                if (await ConclusionRejectedAsync(synthResult.Output, Math.Min(round, _maxRounds)))
                    continue; // nouveau tour de synthèse avec la directive de complétude

                return new DialogueOutcome(turns, DialogueEndReason.ForcedSynthesis, null);
            }

            // ── Tour normal ────────────────────────────────────────────────────
            var role = live[actorTurns % live.Count];
            var prompt = buildPrompt(role, VisibleTranscript(turns, round, isFinal: false), round, false);
            var result = await runTurn(role, prompt, ct);

            if (!result.Success)
            {
                if (ct.IsCancellationRequested)
                    return new DialogueOutcome(turns, DialogueEndReason.Stopped, role);

                // Relève sur quota : on écarte le membre épuisé, les autres poursuivent.
                // Un run ne meurt plus parce qu'un seul moteur est à court de crédits.
                if (result.IsQuotaExhausted && participants.Count > 1)
                {
                    MarkExhausted(role);
                    await PersistAsync(turns, transcriptBasePath, ct);
                    if (Live().Count == 0)
                        return new DialogueOutcome(turns, DialogueEndReason.ActorFailed, role);
                    // Dernier survivant : il porte directement la synthèse plutôt que de
                    // tourner en solo sur plusieurs rounds.
                    if (Live().Count == 1) concludeRequested = true;
                    continue;
                }
                return new DialogueOutcome(turns, DialogueEndReason.ActorFailed, role);
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
