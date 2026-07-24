using SprintLauncher.CodexWatcher;

namespace SprintLauncher.CodexWatcher.Tests;

public sealed class RolloutWatcherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-watcher-watch-" + Guid.NewGuid());

    [Fact]
    public async Task SeedThenAppend_ReplaysNoHistory_ButAlertsTurnsAppendedAfterStartup()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-live.jsonl");
        // Two eligible turns already present when the watcher first sees the file.
        File.WriteAllText(rollout, Meta() + Turn("turn-A", "historique A") + Turn("turn-B", "historique B"));

        var alerts = new List<CompletedTurn>();
        var watcher = new RolloutWatcher(
            _directory,
            new OffsetStore(Path.Combine(_directory, "offsets.json")),
            new RolloutParser(),
            (_, turn, _) => { alerts.Add(turn); return Task.CompletedTask; },
            TextWriter.Null);

        // Seed pass: identity captured, existing turns marked seen, but NO alert.
        await watcher.ProcessFileAsync(rollout, notify: false, CancellationToken.None);
        Assert.Empty(alerts);

        // A new turn completes after startup -> exactly one alert, and only for the new turn.
        File.AppendAllText(rollout, Turn("turn-C", "nouveau apres demarrage"));
        await watcher.ProcessFileAsync(rollout, CancellationToken.None);

        var alerted = Assert.Single(alerts);
        Assert.Equal("turn-C", alerted.TurnId);
        Assert.Equal("nouveau apres demarrage", alerted.LastAgentMessage);
    }

    private static string Meta() =>
        "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"C:\\\\Users\\\\najwa\\\\OneDrive\\\\Desktop\\\\SERZENIA\"," +
        "\"originator\":\"codex_vscode\",\"thread_source\":\"user\"}}\n";

    private static string Turn(string turnId, string message) =>
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"" + turnId +
        "\",\"last_agent_message\":\"" + message + "\"}}\n";

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
