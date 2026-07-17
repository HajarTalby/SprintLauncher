using SprintLauncher.Dialogue;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class DialogueEngineTests : IDisposable
{
    private static readonly ActorRole[] TwoParticipants =
        [ActorRole.CommitteePilotageClaudeChat, ActorRole.CommitteePilotageGptChat];

    private readonly string _tempDir;
    private string TranscriptBase => Path.Combine(_tempDir, "dialogue-test");

    public DialogueEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private static ActorPrompt FakePrompt(ActorRole role, IReadOnlyList<DialogueTurn> _, int __, bool ___) =>
        new(role, "sys", "user");

    private static ActorRunResult Ok(ActorRole role, string output) =>
        new(role, Success: true, Output: output, ErrorOutput: null, ExitCode: 0, IsSemiManual: false);

    private static ActorRunResult QuotaOut(ActorRole role) =>
        new(role, Success: false, Output: "", ErrorOutput: "quota", ExitCode: 1,
            IsSemiManual: false, IsQuotaExhausted: true);

    private static ActorRunResult HardFail(ActorRole role) =>
        new(role, Success: false, Output: "", ErrorOutput: "boom", ExitCode: 1, IsSemiManual: false);

    // ── Convergence ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Marker_stops_dialogue_early()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");
        int calls = 0;

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(Ok(role,
                ++calls == 2 ? "D'accord sur tout. [CONSENSUS]" : "Position initiale.")),
            requestIntervention: null,
            TranscriptBase);

        Assert.Equal(DialogueEndReason.Converged, outcome.EndReason);
        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Turns.Count);
        Assert.Contains("[CONSENSUS]", outcome.FinalContribution);
    }

    [Fact]
    public async Task Forced_synthesis_after_max_rounds()
    {
        var engine = new DialogueEngine(maxRounds: 2, approverName: "Hajar");
        bool sawFinalSynthesisPrompt = false;

        var outcome = await engine.RunAsync(
            TwoParticipants,
            (role, transcript, round, isFinal) =>
            {
                if (isFinal) sawFinalSynthesisPrompt = true;
                return new ActorPrompt(role, "sys", "user");
            },
            (role, _, _) => Task.FromResult(Ok(role, "Toujours en désaccord.")),
            requestIntervention: null,
            TranscriptBase);

        Assert.Equal(DialogueEndReason.ForcedSynthesis, outcome.EndReason);
        Assert.True(sawFinalSynthesisPrompt);
        // 2 rounds × 2 participants + 1 tour de synthèse forcée
        Assert.Equal(5, outcome.Turns.Count);
        // La synthèse est assumée par le dernier participant
        Assert.Equal(TwoParticipants[^1].ToString(), outcome.Turns[^1].Speaker);
    }

    // ── Relève sur quota (retour Hajar 2026-07-16) ─────────────────────────────

    // Le membre Claude du comité meurt de quota au 1er tour → le membre GPT prend la
    // relève et porte la synthèse. Sans ça, la discussion mourait (constat de Hajar).
    [Fact]
    public async Task Quota_exhausted_first_member_relieved_by_second()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");
        var seen = new List<ActorRole>();

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) =>
            {
                seen.Add(role);
                // Claude (1er membre) est à court de quota ; GPT répond normalement.
                return Task.FromResult(role == ActorRole.CommitteePilotageClaudeChat
                    ? QuotaOut(role)
                    : Ok(role, "Analyse GPT complète. [DECISION FINALE]"));
            },
            requestIntervention: null,
            TranscriptBase);

        Assert.NotEqual(DialogueEndReason.ActorFailed, outcome.EndReason);
        Assert.True(outcome.Success);
        // La synthèse finale est bien portée par GPT, le survivant.
        Assert.Equal(ActorRole.CommitteePilotageGptChat.ToString(), outcome.Turns[^1].Speaker);
        Assert.Contains(seen, r => r == ActorRole.CommitteePilotageGptChat);
    }

    // Les deux membres épuisés → échec réel (pas de faux succès).
    [Fact]
    public async Task All_members_quota_exhausted_fails()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(QuotaOut(role)),
            requestIntervention: null,
            TranscriptBase);

        Assert.Equal(DialogueEndReason.ActorFailed, outcome.EndReason);
    }

    // Un échec DUR (pas un quota) ne déclenche pas la relève : c'est un vrai problème.
    [Fact]
    public async Task Hard_failure_does_not_trigger_relief()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(HardFail(role)),
            requestIntervention: null,
            TranscriptBase);

        Assert.Equal(DialogueEndReason.ActorFailed, outcome.EndReason);
        Assert.Equal(ActorRole.CommitteePilotageClaudeChat, outcome.FailedRole);
    }

    // ── Interventions de l'approbatrice ───────────────────────────────────────

    [Fact]
    public async Task Intervention_message_is_injected_with_authority()
    {
        var engine = new DialogueEngine(maxRounds: 2, approverName: "Hajar");
        bool nextPromptSawIntervention = false;
        var interventions = new Queue<DialogueIntervention>([
            new DialogueIntervention(InterventionKind.Message, "Laissez tomber l'option B."),
        ]);

        var outcome = await engine.RunAsync(
            TwoParticipants,
            (role, transcript, round, isFinal) =>
            {
                if (transcript.Any(t => t.IsIntervention)) nextPromptSawIntervention = true;
                return new ActorPrompt(role, "sys", "user");
            },
            (role, _, _) => Task.FromResult(Ok(role, "Contribution.")),
            round => Task.FromResult(interventions.TryDequeue(out var i)
                ? i : new DialogueIntervention(InterventionKind.Continue)),
            TranscriptBase);

        var injected = Assert.Single(outcome.Turns.Where(t => t.IsIntervention));
        Assert.Equal("Hajar", injected.Speaker); // prénom seul, jamais de titre
        Assert.Equal("Laissez tomber l'option B.", injected.Content);
        Assert.True(nextPromptSawIntervention);
        // Le formatage prompt marque l'autorité de l'intervention sans titre accolé
        Assert.Contains("Intervention de Hajar — directive à respecter", DialogueEngine.FormatForPrompt(outcome.Turns));
    }

    [Fact]
    public async Task Intervention_conclude_jumps_to_synthesis()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(Ok(role, "Contribution.")),
            _ => Task.FromResult(new DialogueIntervention(InterventionKind.Conclude)),
            TranscriptBase);

        Assert.Equal(DialogueEndReason.ForcedSynthesis, outcome.EndReason);
        // round 1 complet (2 tours) + synthèse — pas de round 2 malgré maxRounds=3
        Assert.Equal(3, outcome.Turns.Count);
    }

    [Fact]
    public async Task Intervention_stop_preserves_transcript_for_resume()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(Ok(role, "Contribution.")),
            _ => Task.FromResult(new DialogueIntervention(InterventionKind.Stop)),
            TranscriptBase);

        Assert.Equal(DialogueEndReason.Stopped, outcome.EndReason);
        Assert.False(outcome.Success);
        var persisted = await DialogueEngine.TryLoadTranscriptAsync(TranscriptBase);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.Count);
    }

    [Fact]
    public async Task Multiple_interventions_can_be_chained_at_one_checkpoint()
    {
        var engine = new DialogueEngine(maxRounds: 2, approverName: "Hajar");
        var script = new Queue<DialogueIntervention>([
            new(InterventionKind.Message, "Directive 1 : priorisez la sécurité."),
            new(InterventionKind.Message, "Directive 2 : écartez l'option C."),
            new(InterventionKind.Continue),
        ]);

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(Ok(role, "Contribution. ")),
            _ => Task.FromResult(script.TryDequeue(out var i) ? i : new DialogueIntervention(InterventionKind.Conclude)),
            TranscriptBase);

        // Les deux directives sont dans le transcript, dans l'ordre
        var interventions = outcome.Turns.Where(t => t.IsIntervention).ToList();
        Assert.Equal(2, interventions.Count);
        Assert.Contains("Directive 1", interventions[0].Content);
        Assert.Contains("Directive 2", interventions[1].Content);
    }

    [Fact]
    public async Task Every_turn_mode_offers_checkpoint_after_each_speaker()
    {
        var engine = new DialogueEngine(maxRounds: 2, approverName: "Hajar", interventionEveryTurn: true);
        int checkpoints = 0;

        await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(Ok(role, "Contribution.")),
            _ => { checkpoints++; return Task.FromResult(new DialogueIntervention(InterventionKind.Continue)); },
            TranscriptBase);

        // 2 rounds × 2 participants = 4 tours + synthèse ; checkpoint après chaque tour (dès le 1er)
        Assert.True(checkpoints >= 3, $"attendu >= 3 checkpoints, obtenu {checkpoints}");
    }

    // ── Échec acteur ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Actor_failure_ends_dialogue_with_failed_role()
    {
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(role == TwoParticipants[1]
                ? new ActorRunResult(role, false, "", "quota exceeded", 1, false)
                : Ok(role, "Contribution.")),
            requestIntervention: null,
            TranscriptBase);

        Assert.Equal(DialogueEndReason.ActorFailed, outcome.EndReason);
        Assert.Equal(TwoParticipants[1], outcome.FailedRole);
        Assert.Single(outcome.Turns); // le tour réussi est conservé pour la reprise
    }

    // ── Reprise --resume ───────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_continues_from_persisted_transcript()
    {
        var resumed = new List<DialogueTurn>
        {
            new(TwoParticipants[0].ToString(), "Position A.", DateTimeOffset.UtcNow, 1, false),
            new(TwoParticipants[1].ToString(), "Position B.", DateTimeOffset.UtcNow, 1, false),
        };
        var engine = new DialogueEngine(maxRounds: 2, approverName: "Hajar");
        var spoken = new List<ActorRole>();

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) =>
            {
                spoken.Add(role);
                return Task.FromResult(Ok(role, spoken.Count == 2 ? "OK. [CONSENSUS]" : "Round 2."));
            },
            requestIntervention: null,
            TranscriptBase,
            resumedTurns: resumed);

        // La discussion reprend au round 2 avec le premier participant
        Assert.Equal(TwoParticipants[0], spoken[0]);
        Assert.Equal(DialogueEndReason.Converged, outcome.EndReason);
        Assert.Equal(4, outcome.Turns.Count);
    }

    [Fact]
    public async Task Resume_with_already_converged_transcript_returns_immediately()
    {
        var resumed = new List<DialogueTurn>
        {
            new(TwoParticipants[0].ToString(), "Position A.", DateTimeOffset.UtcNow, 1, false),
            new(TwoParticipants[1].ToString(), "Accord. [DECISION FINALE]", DateTimeOffset.UtcNow, 1, false),
        };
        var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");
        int calls = 0;

        var outcome = await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => { calls++; return Task.FromResult(Ok(role, "ne doit pas tourner")); },
            requestIntervention: null,
            TranscriptBase,
            resumedTurns: resumed);

        Assert.Equal(DialogueEndReason.Converged, outcome.EndReason);
        Assert.Equal(0, calls); // aucun acteur relancé — pas de quota consommé
    }

    // ── Persistance ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transcript_roundtrips_through_json()
    {
        var engine = new DialogueEngine(maxRounds: 1, approverName: "Hajar");
        await engine.RunAsync(
            TwoParticipants,
            FakePrompt,
            (role, _, _) => Task.FromResult(Ok(role, "Contenu accentué : décision, priorité. [CONSENSUS]")),
            requestIntervention: null,
            TranscriptBase);

        var loaded = await DialogueEngine.TryLoadTranscriptAsync(TranscriptBase);
        Assert.NotNull(loaded);
        var turn = Assert.Single(loaded!);
        Assert.Contains("décision", turn.Content);
        Assert.True(File.Exists(TranscriptBase + ".md"));
    }

    [Fact]
    public async Task Corrupt_transcript_returns_null_instead_of_throwing()
    {
        await File.WriteAllTextAsync(TranscriptBase + ".json", "{not valid json");
        var loaded = await DialogueEngine.TryLoadTranscriptAsync(TranscriptBase);
        Assert.Null(loaded);
    }
}
