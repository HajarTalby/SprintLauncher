using System.Diagnostics;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class ActorTurnCoordinatorTests : IDisposable
{
    private readonly string _repoDir;

    public ActorTurnCoordinatorTests()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "sl-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDir);
        RunGit("init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Same_engine_turns_are_serialized()
    {
        var coordinator = new ActorTurnCoordinator(_repoDir);
        await using var first = await coordinator.BeginAsync(ActorRole.AnalysisCodex);

        var secondTask = coordinator.BeginAsync(ActorRole.GptImplementation);
        var completedBeforeRelease = await Task.WhenAny(secondTask, Task.Delay(150));

        Assert.NotSame(secondTask, completedBeforeRelease);

        await first.CompleteAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        await second.CompleteAsync();
    }

    [Fact]
    public async Task Different_engine_turns_can_overlap()
    {
        var coordinator = new ActorTurnCoordinator(_repoDir);
        await using var claude = await coordinator.BeginAsync(ActorRole.AnalysisCcode);

        var codexTask = coordinator.BeginAsync(ActorRole.AnalysisCodex);
        await using var codex = await codexTask.WaitAsync(TimeSpan.FromSeconds(5));

        await claude.CompleteAsync();
        await codex.CompleteAsync();
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git unavailable");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }
}
