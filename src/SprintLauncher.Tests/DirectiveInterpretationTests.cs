using SprintLauncher.Dialogue;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class DirectiveInterpretationTests
{
    [Fact]
    public void Parses_targets_and_phase_order_from_llm_json()
    {
        var json = """
        {
          "intent": "corriger",
          "targets": {
            "actors": ["codex"],
            "groups": ["qa"]
          },
          "phase_order": {
            "action": "replay",
            "phase": "QA"
          }
        }
        """;

        var interpretation = DirectiveInterpretationParser.TryParse(json, "rejoue la QA et fais corriger codex");

        Assert.NotNull(interpretation);
        Assert.Equal("corriger", interpretation!.Intent);
        Assert.Contains(ActorRole.GptImplementation, interpretation.TargetActors);
        Assert.Contains(ActorGroup.Qa, interpretation.TargetGroups);
        Assert.NotNull(interpretation.PhaseOrder);
        Assert.Equal(PhaseOrderKind.Replay, interpretation.PhaseOrder!.Kind);
        Assert.Equal(ActorGroup.Qa.ToString(), interpretation.PhaseOrder.TargetGroup);
    }

    [Fact]
    public void Invalid_or_non_json_output_returns_null_for_fallback()
    {
        var interpretation = DirectiveInterpretationParser.TryParse("Je vais le faire.", "directive libre");

        Assert.Null(interpretation);
    }
}
