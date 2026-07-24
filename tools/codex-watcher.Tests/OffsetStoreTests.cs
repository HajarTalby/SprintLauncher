using SprintLauncher.CodexWatcher;

namespace SprintLauncher.CodexWatcher.Tests;

public sealed class OffsetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-watcher-tests-" + Guid.NewGuid());

    [Fact]
    public void ReadNewLines_ConsumesOnlyCompleteLinesAndPersistsTheOffset()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-test.jsonl");
        var offsets = Path.Combine(_directory, "offsets.json");
        File.WriteAllText(rollout, "one\ntwo");

        var store = new OffsetStore(offsets);
        Assert.Equal(["one"], store.ReadNewLines(rollout));
        Assert.Empty(store.ReadNewLines(rollout));

        File.AppendAllText(rollout, "\nthree\n");
        Assert.Equal(["two", "three"], store.ReadNewLines(rollout));
        Assert.Empty(new OffsetStore(offsets).ReadNewLines(rollout));
    }

    [Fact]
    public void ReadNewLines_TruncationResetsMetadataAndDeduplication()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-test.jsonl");
        var store = new OffsetStore(Path.Combine(_directory, "offsets.json"));
        File.WriteAllText(rollout, "old line is deliberately longer\n");
        _ = store.ReadNewLines(rollout);
        var entry = store.Get(rollout);
        entry.SessionMeta = new SessionMeta("cwd", "codex_vscode", "user", false);
        entry.SeenTurnIds.Add("turn-1");

        File.WriteAllText(rollout, "new\n");
        Assert.Equal(["new"], store.ReadNewLines(rollout));
        Assert.Null(entry.SessionMeta);
        Assert.Empty(entry.SeenTurnIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
