using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class PhaseOrderQueueTests
{
    [Fact]
    public void Replay_inserts_target_after_current_boundary_and_clears_completion()
    {
        var phases = new List<ActorGroup>
        {
            ActorGroup.Analysis,
            ActorGroup.FamilyClaude,
            ActorGroup.Qa,
        };
        var pending = new List<PendingPhaseOrder>
        {
            new()
            {
                Kind = PhaseOrderKind.Replay,
                TargetGroup = ActorGroup.Analysis.ToString(),
                SourceText = "rejoue l'analyse",
            },
        };
        var cleared = new List<ActorGroup>();

        var applied = PhaseOrderQueue.ApplyNext(phases, pending, currentIndex: 1, cleared.Add);

        Assert.NotNull(applied);
        Assert.Equal(ActorGroup.Analysis, phases[2]);
        Assert.Contains(ActorGroup.Analysis, cleared);
        Assert.True(pending[0].Applied);
        Assert.Equal(2, applied!.NextIndex);
    }

    [Fact]
    public void Skip_to_moves_next_index_to_existing_target()
    {
        var phases = new List<ActorGroup>
        {
            ActorGroup.Analysis,
            ActorGroup.FamilyClaude,
            ActorGroup.FamilyGpt,
            ActorGroup.CommitteePilotage,
            ActorGroup.Qa,
        };
        var pending = new List<PendingPhaseOrder>
        {
            new()
            {
                Kind = PhaseOrderKind.SkipTo,
                TargetGroup = ActorGroup.Qa.ToString(),
                SourceText = "va directement a la QA",
            },
        };

        var cleared = new List<ActorGroup>();

        var applied = PhaseOrderQueue.ApplyNext(phases, pending, currentIndex: 1, cleared.Add);

        Assert.NotNull(applied);
        Assert.Equal(4, applied!.NextIndex);
        Assert.Contains(ActorGroup.Qa, cleared);
        Assert.True(pending[0].Applied);
    }

    [Fact]
    public void Skip_to_inserts_previous_target_after_current_boundary_and_clears_completion()
    {
        var phases = new List<ActorGroup>
        {
            ActorGroup.Analysis,
            ActorGroup.FamilyClaude,
            ActorGroup.FamilyGpt,
        };
        var pending = new List<PendingPhaseOrder>
        {
            new()
            {
                Kind = PhaseOrderKind.SkipTo,
                TargetGroup = ActorGroup.Analysis.ToString(),
                SourceText = "retourne a l'analyse",
            },
        };
        var cleared = new List<ActorGroup>();

        var applied = PhaseOrderQueue.ApplyNext(phases, pending, currentIndex: 2, cleared.Add);

        Assert.NotNull(applied);
        Assert.Equal(3, applied!.NextIndex);
        Assert.Equal(ActorGroup.Analysis, phases[3]);
        Assert.Contains(ActorGroup.Analysis, cleared);
        Assert.True(pending[0].Applied);
    }

    [Theory]
    [InlineData("rejouer", PhaseOrderKind.Replay)]
    [InlineData("insere", PhaseOrderKind.Insert)]
    [InlineData("skip_to", PhaseOrderKind.SkipTo)]
    public void Parses_order_kind_aliases(string raw, PhaseOrderKind expected)
    {
        Assert.Equal(expected, PhaseOrderQueue.ParseKind(raw));
    }
}
