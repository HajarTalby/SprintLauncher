using SprintLauncher.Dialogue;
using SprintLauncher.Jira;
using SprintLauncher.Prompts;
using Xunit;

namespace SprintLauncher.Tests;

public class PromptBuilderDialogueTests
{
    private static readonly List<JiraIssue> Issues =
        [new("SERZ-1", "Ticket de test", "Description du ticket", [])];

    private readonly PromptBuilder _builder = new("SERZENIA", "Hajar");

    [Fact]
    public void First_turn_opens_the_discussion()
    {
        var prompt = _builder.BuildDialogueTurn(
            ActorRole.CommitteePilotageClaudeChat, Issues, "SERZ-1",
            transcript: [], round: 1, maxRounds: 3, isFinalSynthesis: false);

        Assert.Contains("Ouvre la discussion", prompt.UserPrompt);
        Assert.DoesNotContain("## Discussion en cours", prompt.UserPrompt);
        Assert.Contains(DialogueEngine.ConsensusMarker, prompt.SystemPrompt);
        Assert.Contains("Hajar", prompt.SystemPrompt); // autorité de l'approbatrice déclarée
    }

    [Fact]
    public void Later_turn_includes_full_transcript_and_authority_marker()
    {
        var transcript = new List<DialogueTurn>
        {
            new("CommitteePilotageClaudeChat", "Je propose l'option A.", DateTimeOffset.UtcNow, 1, false),
            new("Hajar", "Écartez l'option B.", DateTimeOffset.UtcNow, 1, true),
        };

        var prompt = _builder.BuildDialogueTurn(
            ActorRole.CommitteePilotageGptChat, Issues, "SERZ-1",
            transcript, round: 2, maxRounds: 3, isFinalSynthesis: false);

        Assert.Contains("## Discussion en cours", prompt.UserPrompt);
        Assert.Contains("Je propose l'option A.", prompt.UserPrompt);
        Assert.Contains("Écartez l'option B.", prompt.UserPrompt);
        Assert.Contains("Intervention de Hajar — directive à respecter", prompt.UserPrompt);
        Assert.Contains("jamais de titre", prompt.SystemPrompt); // consigne anti-qualificatifs
        Assert.Contains("round 2/3", prompt.UserPrompt);
    }

    [Fact]
    public void Final_synthesis_turn_requires_decision_marker()
    {
        var transcript = new List<DialogueTurn>
        {
            new("CommitteePilotageClaudeChat", "Option A.", DateTimeOffset.UtcNow, 1, false),
            new("CommitteePilotageGptChat", "Option B.", DateTimeOffset.UtcNow, 1, false),
        };

        var prompt = _builder.BuildDialogueTurn(
            ActorRole.CommitteePilotageGptChat, Issues, "SERZ-1",
            transcript, round: 3, maxRounds: 3, isFinalSynthesis: true);

        Assert.Contains("SYNTHÈSE FINALE", prompt.UserPrompt);
        Assert.Contains(DialogueEngine.FinalDecisionMarker, prompt.UserPrompt);
    }

    [Fact]
    public void Decisions_registry_is_injected_with_do_not_reask_instruction()
    {
        var builder = new PromptBuilder("SERZENIA", "Hajar");
        builder.DecisionsRegistry = "### [SERZ-1] (2026-07-08)\nDécision de Hajar : Google ET Apple à développer.";

        var prompt = builder.BuildDialogueTurn(
            ActorRole.CommitteePilotageClaudeChat, Issues, "SERZ-1",
            transcript: [], round: 1, maxRounds: 3, isFinalSynthesis: false);

        Assert.Contains("DÉCISIONS DÉJÀ ACTÉES PAR HAJAR", prompt.UserPrompt);
        Assert.Contains("NE LES REDEMANDE JAMAIS", prompt.UserPrompt);
        Assert.Contains("Google ET Apple", prompt.UserPrompt);
    }

    [Fact]
    public void Qa_coverage_guard_flags_missing_us()
    {
        // La QA ne mentionne que SERZENIA-98 → les backend sont un trou de couverture.
        var keys = new[] { "SERZENIA-98", "SERZENIA-116", "SERZENIA-112" };
        var verdict = "## Verdict SERZENIA-98\nOK.\n\n## ECARTS\n- [SERZENIA-98] captures manquantes";
        var issue = AnalysisSections.ValidateQaCoverage(verdict, keys);
        Assert.NotNull(issue);
        Assert.Contains("SERZENIA-116", issue);
        Assert.Contains("SERZENIA-112", issue);

        var full = verdict + "\n## Verdict SERZENIA-116\nSentry OK\n## Verdict SERZENIA-112\nGoogle OK";
        Assert.Null(AnalysisSections.ValidateQaCoverage(full, keys));
    }

    [Fact]
    public void Qa_prompt_requires_per_us_coverage_and_stop_conditions()
    {
        var issues = new List<JiraIssue>
        {
            new("SERZ-98", "UI", "desc", []),
            new("SERZ-116", "Sentry", "desc", []),
        };
        var prompt = _builder.BuildDialogueTurn(
            ActorRole.ClaudeQaVerdict, issues, "SERZ-98",
            transcript: [], round: 1, maxRounds: 3, isFinalSynthesis: false);

        Assert.Contains("COUVERTURE OBLIGATOIRE", prompt.SystemPrompt);
        Assert.Contains("SERZ-116", prompt.SystemPrompt);
        Assert.Contains("STOP-CONDITIONS", prompt.SystemPrompt);
    }

    [Fact]
    public void Sprint_context_is_present_in_every_turn()
    {
        var prompt = _builder.BuildDialogueTurn(
            ActorRole.ClaudeQaVerdict, Issues, "SERZ-1",
            transcript: [], round: 1, maxRounds: 3, isFinalSynthesis: false);

        Assert.Contains("[SERZ-1] Ticket de test", prompt.UserPrompt);
        Assert.Contains("Description du ticket", prompt.UserPrompt);
    }
}
