using System.Diagnostics;

namespace SprintLauncher.CodexWatcher;

public sealed class NotifyInvoker
{
    private readonly string _repositoryRoot;
    private readonly TextWriter _log;

    public NotifyInvoker(string repositoryRoot, TextWriter log)
    {
        _repositoryRoot = repositoryRoot;
        _log = log;
    }

    public async Task InvokeAsync(string rolloutPath, CompletedTurn turn, CancellationToken cancellationToken)
    {
        var context = Truncate(turn.LastAgentMessage, 500);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["SPRINTLAUNCHER_HOME"] = _repositoryRoot;
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_repositoryRoot, "tools", "notify"));
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--actor");
        startInfo.ArgumentList.Add("codex");
        startInfo.ArgumentList.Add("--level");
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add($"Tour Codex VS Code termine : {Path.GetFileName(rolloutPath)}");
        startInfo.ArgumentList.Add("--context");
        startInfo.ArgumentList.Add(context);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException("Impossible de lancer notify.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                await _log.WriteLineAsync($"[notify] echec pour {turn.TurnId} (exit {process.ExitCode}): {await standardError}");
            else
                await standardOutput; // Drain redirected streams even when notify is quiet.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _log.WriteLineAsync($"[notify] echec pour {turn.TurnId}: {exception.Message}");
        }
    }

    internal static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";
}
