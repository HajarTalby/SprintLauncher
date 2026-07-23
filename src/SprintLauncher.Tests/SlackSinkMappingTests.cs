using SprintLauncher.Notifications;
using Xunit;

namespace SprintLauncher.Tests;

public class SlackSinkMappingTests
{
    [Theory]
    // Familles Claude → canal ccode
    [InlineData("ClaudePilotage", "ccode")]
    [InlineData("ClaudeImplementation", "ccode")]
    [InlineData("ClaudeQaVerdict", "ccode")]
    // Le nom "Ccode" (analyse/comité) route aussi vers ccode, sans "Claude" dans le nom
    [InlineData("AnalysisCcode", "ccode")]
    [InlineData("CommitteeCcode", "ccode")]
    // Familles GPT/Codex → canal codex
    [InlineData("GptImplementation", "codex")]
    [InlineData("GptPilotage", "codex")]
    [InlineData("AnalysisCodex", "codex")]
    [InlineData("CommitteeCodex", "codex")]
    // Famille Agy → canal ag
    [InlineData("AgImplementation", "ag")]
    // Rôles orchestrateur sans moteur → canal sl
    [InlineData("CommitteePilotage", "sl")]
    [InlineData("CommitteeArbitrage", "sl")]
    [InlineData("Analysis", "sl")]
    [InlineData("Qa", "sl")]
    public void ActorFromRole_maps_expected_channel(string role, string expected) =>
        Assert.Equal(expected, SlackSink.ActorFromRole(role));

    [Fact]
    public void ActorFromRole_arbitrage_not_confused_with_ag()
    {
        // "Arbitrage" contient "ag" en minuscules — la casse sensible évite le faux positif.
        Assert.Equal("sl", SlackSink.ActorFromRole("CommitteeArbitrage"));
    }

    [Theory]
    [InlineData("Claude", "ccode")]
    [InlineData("Codex", "codex")]
    [InlineData("Agy", "ag")]
    [InlineData(null, "sl")]
    [InlineData("", "sl")]
    public void ActorFromEngine_maps_expected_channel(string? engine, string expected) =>
        Assert.Equal(expected, SlackSink.ActorFromEngine(engine));

    [Fact]
    public void Enabled_is_false_under_test_host()
    {
        // Garde-fou anti-spam : jamais d'émission Slack pendant dotnet test.
        Assert.False(SlackSink.Enabled);
    }
}
