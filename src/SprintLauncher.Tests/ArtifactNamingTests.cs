using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class ArtifactNamingTests
{
    [Fact]
    public void Per_us_artifact_names_are_sanitized_and_stable()
    {
        var first = ArtifactNaming.PerUsOutput(ActorRole.GptImplementation, "SERZ:144/lot\\6");
        var second = ArtifactNaming.PerUsOutput(ActorRole.GptImplementation, "SERZ:144/lot\\6");

        Assert.Equal("output-GptImplementation-SERZ_144_lot_6.txt", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Stable_order_uses_ordinal_case_insensitive_key_order()
    {
        var ordered = ArtifactNaming.OrderedByStableKey(["serz-2", "SERZ-10", "SERZ-1"], x => x);

        Assert.Equal(["SERZ-1", "SERZ-10", "serz-2"], ordered);
    }

    [Fact]
    public void Retrospective_actor_name_is_sanitized()
    {
        var name = ArtifactNaming.Retrospective("Claude:../qa\\review");

        Assert.Equal("retro-Claude_.._qa_review.md", name);
    }
}
