using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class RepositoryReconcilerTests
{
    [Fact]
    public void Build_plan_stages_only_paths_that_became_dirty_after_baseline()
    {
        var baseline = Snapshot(
            ("existing.txt", " M"));
        var current = Snapshot(
            ("existing.txt", " M"),
            ("src/NewFile.cs", "??"),
            ("src/Changed.cs", " M"));

        var plan = RepositoryReconciler.BuildPlan([baseline], current);

        Assert.Equal(["src/Changed.cs", "src/NewFile.cs"], plan.StagePaths);
        Assert.Contains(plan.Warnings, w => w.Contains("existing.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_plan_reports_concurrent_attribution_when_reconciling_parallel_turns()
    {
        var firstBaseline = Snapshot();
        var secondBaseline = Snapshot();
        var current = Snapshot(
            ("b.txt", "??"),
            ("a.txt", "??"));

        var plan = RepositoryReconciler.BuildPlan([firstBaseline, secondBaseline], current);

        Assert.Equal(["a.txt", "b.txt"], plan.StagePaths);
        Assert.Contains(plan.Warnings, w => w.Contains("Attribution concurrente possible", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_plan_never_stages_unmerged_paths()
    {
        var baseline = Snapshot();
        var current = Snapshot(
            ("conflicted.cs", "UU"),
            ("safe.cs", " M"));

        var plan = RepositoryReconciler.BuildPlan([baseline], current);

        Assert.Equal(["safe.cs"], plan.StagePaths);
        Assert.Contains(plan.Warnings, w => w.Contains("Conflit git", StringComparison.Ordinal));
    }

    private static GitStatusSnapshot Snapshot(params (string Path, string Status)[] entries) =>
        new(entries.ToDictionary(
            e => e.Path,
            e => new GitStatusEntry(e.Path, e.Status),
            StringComparer.OrdinalIgnoreCase));
}
