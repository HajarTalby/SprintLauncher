using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class ModelSelectionStateTests
{
    [Fact]
    public void Complexity_recommendation_without_role_targets_next_development_role_only()
    {
        var state = new ModelSelectionState("sonnet-5", "gpt-5.5", "agy");

        var target = state.Apply(new ModelRecommendation(ModelEngine.Claude, "claude-opus-4-8", "analyse"), nextDevelopmentOnly: true);

        Assert.Equal(ActorRole.ClaudeImplementation, target);
        Assert.Equal("claude-opus-4-8", state.ModelFor(ActorRole.ClaudeImplementation));
        Assert.Equal("sonnet-5", state.ModelFor(ActorRole.CommitteePilotageClaudeChat));
    }

    [Fact]
    public void Explicit_role_recommendation_targets_that_actor()
    {
        var state = new ModelSelectionState("sonnet-5", "gpt-5.5", "agy");

        var target = state.Apply(
            new ModelRecommendation(ModelEngine.Codex, "gpt-5.5-high", "hajar", ActorRole.GptImplementation),
            nextDevelopmentOnly: false);

        Assert.Equal(ActorRole.GptImplementation, target);
        Assert.Equal("gpt-5.5-high", state.ModelFor(ActorRole.GptImplementation));
        Assert.Equal("gpt-5.5", state.ModelFor(ActorRole.AnalysisCodex));
    }

    // Le lot 1 ne connaissait que Claude/Codex ; l'integration AG (SERZENIA-141) a ajoute
    // un troisieme moteur. La selection par role doit le couvrir aussi, sinon un !model agy
    // retomberait silencieusement sur le modele codex.
    [Fact]
    public void Agy_engine_is_routed_to_its_own_model_and_development_role()
    {
        var state = new ModelSelectionState("sonnet-5", "gpt-5.5", "agy");

        Assert.Equal("agy", state.ModelFor(ActorRole.AgImplementation));

        var target = state.Apply(
            new ModelRecommendation(ModelEngine.Agy, "agy-fast", "analyse"),
            nextDevelopmentOnly: true);

        Assert.Equal(ActorRole.AgImplementation, target);
        Assert.Equal("agy-fast", state.ModelFor(ActorRole.AgImplementation));
        Assert.Equal("gpt-5.5", state.ModelFor(ActorRole.AnalysisCodex));
        Assert.Equal("sonnet-5", state.ModelFor(ActorRole.CommitteePilotageClaudeChat));
    }
}
