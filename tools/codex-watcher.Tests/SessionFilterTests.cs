using SprintLauncher.CodexWatcher;

namespace SprintLauncher.CodexWatcher.Tests;

public sealed class SessionFilterTests
{
    [Theory]
    [InlineData("interactive-vscode.jsonl", true)]
    [InlineData("guardian-subagent.jsonl", false)]
    [InlineData("computer-use.jsonl", false)]
    [InlineData("delegation-worktree.jsonl", false)]
    public void ShouldNotify_AppliesTheFourSessionRules(string fixture, bool expected)
    {
        var parsed = new RolloutParser().Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture)));

        Assert.Equal(expected, SessionFilter.ShouldNotify(parsed.SessionMeta));
    }
}
