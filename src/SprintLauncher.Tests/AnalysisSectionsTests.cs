using SprintLauncher.Dialogue;
using SprintLauncher.Prompts;
using Xunit;

namespace SprintLauncher.Tests;

public class AnalysisSectionsTests
{
    private const string Synthesis = """
    ## ANALYSE SERZ-1
    Actions techniques : créer le service de login, impacter AuthController.
    Dépendances : SERZ-9.

    ## ANALYSE SERZ-2
    Actions techniques : page de profil, réutiliser le UserService existant.

    ## SYNTHESE SPRINT
    Ordre recommandé : SERZ-1 puis SERZ-2. [CONSENSUS]
    """;

    [Fact]
    public void Splits_per_us_sections()
    {
        var sections = AnalysisSections.Split(Synthesis, ["SERZ-1", "SERZ-2"]);
        Assert.Equal(2, sections.Count);
        Assert.Contains("AuthController", sections["SERZ-1"]);
        Assert.Contains("UserService", sections["SERZ-2"]);
        Assert.DoesNotContain("SYNTHESE SPRINT", sections["SERZ-2"]);
    }

    [Fact]
    public void Missing_key_is_simply_absent()
    {
        var sections = AnalysisSections.Split(Synthesis, ["SERZ-1", "SERZ-99"]);
        Assert.Single(sections);
        Assert.False(sections.ContainsKey("SERZ-99"));
    }

    [Fact]
    public void Litige_marker_is_detected_and_extracted()
    {
        var text = "Analyse.\n[LITIGE: choix de la couche persistance]\n## SYNTHESE SPRINT\n...";
        Assert.True(AnalysisSections.HasLitige(text));
        Assert.Equal("choix de la couche persistance", AnalysisSections.ExtractLitige(text));
    }

    [Fact]
    public void No_litige_when_marker_absent()
    {
        Assert.False(AnalysisSections.HasLitige(Synthesis));
    }

    [Fact]
    public void Analysis_group_is_in_pipeline_before_families()
    {
        var order = SessionMode.Execution.GetGroupOrder();
        Assert.True(order.IndexOf(ActorGroup.Analysis) < order.IndexOf(ActorGroup.FamilyClaude));
        // L'arbitrage reste présent dans l'ordre — sa convocation est conditionnelle au litige
        Assert.Contains(ActorGroup.CommitteeArbitrage, order);
    }

    // Le cadrage PRÉPARE un sprint, il ne l'exécute pas. Il déroulait auparavant tout
    // le pipeline (familles, QA, rétro) en se contentant de mettre le comité en tête.
    // Le cadrage PRÉPARE un sprint, il ne l'exécute pas : pas d'implémentation, pas de
    // rétrospective. La QA reste, mais avec un rôle de préparation de scénarios (décision
    // Hajar 2026-07-22), pas de verdict.
    [Fact]
    public void Cadrage_never_reaches_implementation_or_retrospective()
    {
        var order = SessionMode.Cadrage.GetGroupOrder();

        Assert.DoesNotContain(ActorGroup.FamilyClaude, order);
        Assert.DoesNotContain(ActorGroup.FamilyGpt, order);
        Assert.DoesNotContain(ActorGroup.Retrospective, order);
    }

    [Fact]
    public void Cadrage_frames_analyses_arbitrates_then_prepares_qa_scenarios()
    {
        var order = SessionMode.Cadrage.GetGroupOrder();

        Assert.True(order.IndexOf(ActorGroup.CommitteePilotage) < order.IndexOf(ActorGroup.Analysis));
        // Arbitrage avant la QA : convoqué seulement sur [LITIGE], sinon une US sortirait
        // du cadrage avec un désaccord d'analyse non tranché.
        Assert.True(order.IndexOf(ActorGroup.CommitteeArbitrage) < order.IndexOf(ActorGroup.Qa));
        // La QA prépare les scénarios de test (dernière étape du cadrage), elle ne
        // rend pas de verdict — il n'y a rien à valider.
        Assert.Equal(ActorGroup.Qa, order[^1]);
    }
}

file static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<ActorGroup> list, ActorGroup value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
