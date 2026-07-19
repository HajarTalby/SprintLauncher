using System.Diagnostics;
using System.Text;

namespace SprintLauncher.Runners;

public sealed record GitStatusEntry(string Path, string Status)
{
    public bool IsUnmerged =>
        Status.Length >= 2 && (Status[0] == 'U' || Status[1] == 'U' ||
                               Status is "AA" or "DD");
}

public sealed record GitStatusSnapshot(IReadOnlyDictionary<string, GitStatusEntry> Entries)
{
    public static GitStatusSnapshot Empty { get; } =
        new(new Dictionary<string, GitStatusEntry>(StringComparer.OrdinalIgnoreCase));
}

public sealed record RepositoryReconciliationPlan(
    IReadOnlyList<string> StagePaths,
    IReadOnlyList<string> Warnings)
{
    public bool HasWork => StagePaths.Count > 0;
}

public sealed record RepositoryReconciliationResult(
    IReadOnlyList<string> StagedPaths,
    IReadOnlyList<string> Warnings,
    bool GitUnavailable);

public sealed class RepositoryReconciler
{
    private readonly string _repoRoot;
    private readonly SemaphoreSlim _gitLock = new(1, 1);

    public RepositoryReconciler(string repoRoot)
    {
        _repoRoot = repoRoot;
    }

    public async Task<GitStatusSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            return await CaptureUnlockedAsync(ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    private async Task<GitStatusSnapshot> CaptureUnlockedAsync(CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunGitAsync(["-c", "core.quotePath=false", "status", "--porcelain=v1", "--untracked-files=all"], ct);
        if (exitCode != 0) return GitStatusSnapshot.Empty;
        return ParseStatus(stdout);
    }

    public async Task<RepositoryReconciliationResult> ReconcileAsync(
        IReadOnlyList<GitStatusSnapshot> baselines,
        CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            var current = await CaptureUnlockedAsync(ct);
            var plan = BuildPlan(baselines, current);
            if (plan.StagePaths.Count == 0)
                return new RepositoryReconciliationResult([], plan.Warnings, GitUnavailable: false);

            foreach (var batch in Batch(plan.StagePaths, 80))
            {
                var args = new List<string> { "add", "--" };
                args.AddRange(batch);
                var (exitCode, _, stderr) = await RunGitAsync(args, ct);
                if (exitCode != 0)
                {
                    var warnings = plan.Warnings.Concat([$"git add cible a echoue : {stderr.Trim()}"]).ToArray();
                    return new RepositoryReconciliationResult([], warnings, GitUnavailable: true);
                }
            }

            return new RepositoryReconciliationResult(plan.StagePaths, plan.Warnings, GitUnavailable: false);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    public static RepositoryReconciliationPlan BuildPlan(
        IReadOnlyList<GitStatusSnapshot> baselines,
        GitStatusSnapshot current)
    {
        var warnings = new List<string>();
        var stage = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (baselines.Count == 0 || current.Entries.Count == 0)
            return new RepositoryReconciliationPlan([], []);

        foreach (var entry in current.Entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.IsUnmerged)
            {
                warnings.Add($"Conflit git non stage automatiquement : {entry.Path} ({entry.Status})");
                continue;
            }

            var dirtyBeforeCount = baselines.Count(b => b.Entries.ContainsKey(entry.Path));
            if (dirtyBeforeCount == baselines.Count)
            {
                warnings.Add($"Ecart preexistant conserve hors git add cible : {entry.Path}");
                continue;
            }

            if (dirtyBeforeCount > 0)
                warnings.Add($"Ecart concurrent signale sur {entry.Path} : chemin deja sale pour un des tours reconciles.");

            if (baselines.Count > 1 && dirtyBeforeCount == 0)
                warnings.Add($"Attribution concurrente possible : {entry.Path} produit pendant plusieurs tours en parallele.");

            stage.Add(entry.Path);
        }

        return new RepositoryReconciliationPlan(stage.ToArray(), warnings);
    }

    private static GitStatusSnapshot ParseStatus(string stdout)
    {
        var entries = new Dictionary<string, GitStatusEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Length < 4) continue;
            var status = rawLine[..2];
            var path = rawLine[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            if (path.Length == 0) continue;
            entries[path] = new GitStatusEntry(path, status);
        }
        return new GitStatusSnapshot(entries);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, "", ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> paths, int size)
    {
        for (var i = 0; i < paths.Count; i += size)
            yield return paths.Skip(i).Take(size).ToArray();
    }
}
