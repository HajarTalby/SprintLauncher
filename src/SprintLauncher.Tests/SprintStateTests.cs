using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class SprintStateTests : IDisposable
{
    private readonly string _tempDir;

    public SprintStateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sl-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Old_state_file_without_completed_groups_still_loads()
    {
        // state.json produit par une version antérieure à SERZENIA-143 (pas de CompletedGroups)
        var legacyJson = """
        {
          "StartedAt": "2026-07-01T10:00:00+00:00",
          "Keys": ["SERZ-1"],
          "CompletedRoles": ["ClaudePilotage"],
          "InterruptedRoles": []
        }
        """;
        var path = Path.Combine(_tempDir, "state.json");
        await File.WriteAllTextAsync(path, legacyJson);

        var state = await SprintStateManager.TryLoadAsync(path);

        Assert.NotNull(state);
        Assert.Contains("ClaudePilotage", state!.CompletedRoles);
        Assert.NotNull(state.CompletedGroups);
        Assert.Empty(state.CompletedGroups);
    }

    [Fact]
    public async Task Completed_groups_roundtrip_through_save_and_load()
    {
        var path = Path.Combine(_tempDir, "state.json");
        var state = new SprintState
        {
            StartedAt = DateTimeOffset.UtcNow,
            Keys = ["SERZ-1"],
            CompletedRoles = [],
            CompletedGroups = ["CommitteePilotage"],
        };

        await SprintStateManager.SaveAsync(path, state);
        var loaded = await SprintStateManager.TryLoadAsync(path);

        Assert.NotNull(loaded);
        Assert.Contains("CommitteePilotage", loaded!.CompletedGroups);
    }
}
