using SprintLauncher.CodexWatcher;

namespace SprintLauncher.CodexWatcher.Tests;

public sealed class RolloutParserTests
{
    private readonly RolloutParser _parser = new();

    [Fact]
    public void Parse_InteractiveVsCode_ReturnsBothCompletedTurnsAndKeepsFirstMetadata()
    {
        var parsed = ParseFixture("interactive-vscode.jsonl");

        Assert.NotNull(parsed.SessionMeta);
        Assert.Equal("C:\\Users\\najwa\\OneDrive\\Desktop\\SERZENIA", parsed.SessionMeta!.Cwd);
        Assert.Collection(parsed.CompletedTurns,
            turn =>
            {
                Assert.Equal("turn-A", turn.TurnId);
                Assert.Contains("276 verts", turn.LastAgentMessage);
            },
            turn =>
            {
                Assert.Equal("turn-B", turn.TurnId);
                Assert.StartsWith("Prochaine etape", turn.LastAgentMessage);
            });
    }

    [Fact]
    public void Parse_GuardianSubagent_ReturnsTurnWithoutFiltering()
    {
        var parsed = ParseFixture("guardian-subagent.jsonl");

        var turn = Assert.Single(parsed.CompletedTurns);
        Assert.Equal("019f8fc8-a80c-7a72-a1fd-cf7a9564a98b", turn.TurnId);
        Assert.True(parsed.SessionMeta!.HasSubagent);
    }

    [Fact]
    public void Parse_InvalidLinesAndApprovalExtension_AreTolerated()
    {
        var parsed = _parser.Parse("not json\n{\"type\":\"session_meta\"}\n{\"type\":\"event_msg\",\"payload\":{}}\n");

        Assert.Empty(parsed.CompletedTurns);
        Assert.False(RolloutEvent.IsApprovalRequest(""));
    }

    private RolloutParseResult ParseFixture(string fixture) =>
        _parser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)));
}
