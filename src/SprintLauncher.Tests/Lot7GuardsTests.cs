using SprintLauncher.Dialogue;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class AnalysisCoverageGuardTests
{
    [Fact]
    public void Complete_coverage_is_accepted()
    {
        var text = "## ANALYSE SERZ-1\nAnalyse complète du premier ticket.\n## ANALYSE SERZ-2\nAnalyse complète du second ticket.\n## SYNTHESE SPRINT\nok";
        Assert.Null(AnalysisSections.ValidateCoverage(text, ["SERZ-1", "SERZ-2"]));
    }

    [Fact]
    public void Missing_us_sections_are_rejected_with_their_keys()
    {
        var text = "## ANALYSE SERZ-1\nSeul le premier ticket est analysé ici.\n## SYNTHESE SPRINT\nok [CONSENSUS]";
        var issue = AnalysisSections.ValidateCoverage(text, ["SERZ-1", "SERZ-2", "SERZ-3"]);
        Assert.NotNull(issue);
        Assert.Contains("SERZ-2", issue);
        Assert.Contains("SERZ-3", issue);
        Assert.DoesNotContain("SERZ-1,", issue);
    }
}

public class DialogueRescueTests
{
    private static readonly ActorRole[] Two = [ActorRole.AnalysisCcode, ActorRole.AnalysisCodex];
    private static ActorPrompt P(ActorRole r, IReadOnlyList<DialogueTurn> _, int __, bool ___) => new(r, "s", "u");
    private static ActorRunResult Ok(ActorRole r, string o) => new(r, true, o, null, 0, false);

    [Fact]
    public async Task Incomplete_conclusion_triggers_rescue_turn_then_converges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sl-rescue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var engine = new DialogueEngine(maxRounds: 3, approverName: "Hajar");
            int calls = 0;

            var outcome = await engine.RunAsync(
                Two, P,
                (r, _, _) => Task.FromResult(Ok(r, ++calls switch
                {
                    1 => "## ANALYSE SERZ-1\nAnalyse partielle du premier ticket seulement. [CONSENSUS]", // incomplet → refusé
                    _ => "## ANALYSE SERZ-1\nAnalyse complète du premier ticket avec détails.\n" +
                         "## ANALYSE SERZ-2\nAnalyse complète du second ticket avec détails. [CONSENSUS]",
                })),
                requestIntervention: null,
                Path.Combine(dir, "d"),
                validateConclusion: text => AnalysisSections.ValidateCoverage(text, ["SERZ-1", "SERZ-2"]));

            Assert.Equal(DialogueEndReason.Converged, outcome.EndReason);
            // 1er tour refusé + tour de garde injecté + 2e tour accepté
            Assert.Contains(outcome.Turns, t => t.IsIntervention && t.Speaker.Contains("garde de complétude"));
            Assert.Equal(2, calls);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Rescue_budget_exhausted_accepts_with_trace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sl-rescue2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var engine = new DialogueEngine(maxRounds: 2, approverName: "Hajar");
            var outcome = await engine.RunAsync(
                Two, P,
                (r, _, _) => Task.FromResult(Ok(r, "Toujours incomplet. [CONSENSUS]")),
                requestIntervention: null,
                Path.Combine(dir, "d"),
                validateConclusion: _ => "il manque tout.",
                maxRescueTurns: 1);

            // Budget épuisé → la discussion se termine quand même (pas de boucle infinie)
            Assert.True(outcome.Success);
            Assert.Single(outcome.Turns.Where(t => t.IsIntervention)); // 1 seul tour de garde
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ImplementationOutputGuardTests
{
    [Theory]
    [InlineData("Merci de répondre `GO SERZENIA-115` pour autoriser l'exécution complète dans ce périmètre.")]
    [InlineData("Sans GO explicite, je ne lis pas le repo, ne lance aucune commande et ne modifie aucun fichier.")]
    [InlineData("J'attends le GO avant toute implémentation.")]
    public void Awaiting_go_declarations_are_detected(string output)
    {
        Assert.True(ImplementationOutputGuard.IsAwaitingGo(output));
    }

    [Theory]
    [InlineData("Implémentation terminée : 3 fichiers modifiés, tests 12/12 verts, commit abc123 pushé.")]
    [InlineData("## Résumé\n- Service créé\n- Tests xUnit ajoutés\n- git push effectué")]
    public void Real_implementations_pass(string output)
    {
        Assert.False(ImplementationOutputGuard.IsAwaitingGo(output));
    }
}
