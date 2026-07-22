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
        Assert.NotNull(state.QuotaResetTimesByEngine);
        Assert.Empty(state.QuotaResetTimesByEngine);
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

    // SERZENIA-144 Lot 3 : les pièces jointes d'une directive en attente doivent
    // survivre à une interruption / --resume au même titre que le texte.
    [Fact]
    public async Task Pending_directive_attachment_paths_roundtrip_through_save_and_load()
    {
        var path = Path.Combine(_tempDir, "state.json");
        var state = new SprintState
        {
            StartedAt = DateTimeOffset.UtcNow,
            Keys = ["SERZ-1"],
            PendingDirectives =
            [
                new PendingDirective
                {
                    SubjectKey = "SERZ-1",
                    Text = "regarde la capture d'écran ci-jointe",
                    TargetActor = "ClaudeImplementation",
                    AttachmentSourcePaths = ["C:\\Users\\hajar\\Desktop\\bug.png"],
                },
            ],
        };

        await SprintStateManager.SaveAsync(path, state);
        var loaded = await SprintStateManager.TryLoadAsync(path);

        Assert.NotNull(loaded);
        var directive = Assert.Single(loaded!.PendingDirectives);
        Assert.Equal(["C:\\Users\\hajar\\Desktop\\bug.png"], directive.AttachmentSourcePaths);
    }

    [Fact]
    public async Task Pause_request_roundtrips_through_save_and_load()
    {
        var path = Path.Combine(_tempDir, "state.json");
        var requestedAt = DateTimeOffset.Parse("2026-07-19T12:34:56+00:00");
        var state = new SprintState
        {
            StartedAt = DateTimeOffset.UtcNow,
            Keys = ["SERZ-1"],
            PauseRequested = true,
            PauseRequestedAt = requestedAt,
        };

        await SprintStateManager.SaveAsync(path, state);
        var loaded = await SprintStateManager.TryLoadAsync(path);

        Assert.NotNull(loaded);
        Assert.True(loaded!.PauseRequested);
        Assert.Equal(requestedAt, loaded.PauseRequestedAt);
    }

    // state.json produit avant SERZENIA-144 Lot 3 (pas d'AttachmentSourcePaths) :
    // doit toujours charger, avec une liste vide plutôt qu'un null qui planterait
    // les appelants.
    [Fact]
    public async Task Legacy_pending_directive_without_attachments_still_loads()
    {
        var legacyJson = """
        {
          "StartedAt": "2026-07-01T10:00:00+00:00",
          "Keys": ["SERZ-1"],
          "PendingDirectives": [
            { "SubjectKey": "SERZ-1", "Text": "continue", "Delivered": false }
          ]
        }
        """;
        var path = Path.Combine(_tempDir, "state.json");
        await File.WriteAllTextAsync(path, legacyJson);

        var state = await SprintStateManager.TryLoadAsync(path);

        Assert.NotNull(state);
        var directive = Assert.Single(state!.PendingDirectives);
        Assert.NotNull(directive.AttachmentSourcePaths);
        Assert.Empty(directive.AttachmentSourcePaths);
    }

    [Fact]
    public async Task Pending_phase_orders_roundtrip_through_save_and_load()
    {
        var path = Path.Combine(_tempDir, "state.json");
        var state = new SprintState
        {
            StartedAt = DateTimeOffset.UtcNow,
            Keys = ["SERZ-1"],
            PendingPhaseOrders =
            [
                new PendingPhaseOrder
                {
                    Kind = PhaseOrderKind.SkipTo,
                    TargetGroup = "Qa",
                    SourceText = "va directement a la QA",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };

        await SprintStateManager.SaveAsync(path, state);
        var loaded = await SprintStateManager.TryLoadAsync(path);

        Assert.NotNull(loaded);
        var order = Assert.Single(loaded!.PendingPhaseOrders);
        Assert.Equal(PhaseOrderKind.SkipTo, order.Kind);
        Assert.Equal("Qa", order.TargetGroup);
        Assert.False(order.Applied);
    }

    [Fact]
    public async Task Hash_set_fields_are_saved_in_stable_order()
    {
        var path = Path.Combine(_tempDir, "state.json");
        var state = new SprintState
        {
            StartedAt = DateTimeOffset.Parse("2026-07-19T12:00:00+00:00"),
            Keys = ["SERZ-1"],
            CompletedRoles = ["GptImplementation", "ClaudeImplementation"],
            CompletedGroups = ["Qa", "Analysis"],
        };

        await SprintStateManager.SaveAsync(path, state);
        var json = await File.ReadAllTextAsync(path);

        Assert.True(json.IndexOf("ClaudeImplementation", StringComparison.Ordinal) <
                    json.IndexOf("GptImplementation", StringComparison.Ordinal));
        Assert.True(json.IndexOf("Analysis", StringComparison.Ordinal) <
                    json.IndexOf("Qa", StringComparison.Ordinal));
    }
}
