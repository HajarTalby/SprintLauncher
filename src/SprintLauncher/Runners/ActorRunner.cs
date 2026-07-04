using System.Diagnostics;
using System.Text;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed class ActorRunner : IDisposable
{
    private readonly string? _claudeBin;
    private readonly string? _codexBin;
    private readonly string _claudeModel;
    private readonly string _codexModel;
    private readonly TimeSpan _actorTimeout;
    private readonly string? _repoRoot;
    // Kills all child processes if the launcher exits for any reason (window close, crash, taskkill).
    private readonly WindowsJobObject? _job = WindowsJobObject.TryCreate();

    public ActorRunner(
        string? claudeBin = null,
        string? codexBin = null,
        string? claudeModel = null,
        string? codexModel = null,
        TimeSpan? actorTimeout = null,
        string? repoRoot = null)
    {
        _claudeBin = claudeBin ?? BinaryLocator.FindClaude();
        _codexBin = codexBin ?? BinaryLocator.FindCodex();
        _claudeModel = claudeModel ?? "claude-opus-4-8";
        _codexModel = codexModel ?? "gpt-5.5";
        _actorTimeout = actorTimeout ?? TimeSpan.FromMinutes(10);
        _repoRoot = repoRoot;
    }

    public void Dispose() => _job?.Dispose();

    public async Task<ActorRunResult> RunAsync(
        ActorPrompt prompt, CancellationToken ct = default)
    {
        if (prompt.Role.IsSemiManual())
            return RunSemiManual(prompt);

        if (prompt.Role.IsClaudeFamily())
            return await RunClaudeAsync(prompt, ct);

        return await RunCodexAsync(prompt, ct);
    }

    // Claude actors: claude -p (reads prompt from stdin) — subscription, no ANTHROPIC_API_KEY
    // Prompt passed via stdin to avoid Windows 32767-char command-line limit.
    private async Task<ActorRunResult> RunClaudeAsync(ActorPrompt prompt, CancellationToken ct)
    {
        if (_claudeBin is null)
            return Fail(prompt.Role, "claude.exe not found. Set CLAUDE_BIN env var or install Claude Desktop App.");

        var fullPrompt = $"{prompt.SystemPrompt}\n\n---\n\n{prompt.UserPrompt}";

        var psi = new ProcessStartInfo
        {
            FileName = _claudeBin,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_claudeModel);
        // Point claude.exe at the SERZENIA source repo so agents can read/write code
        // regardless of where the launcher binary is located.
        if (_repoRoot is not null)
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(_repoRoot);
            psi.WorkingDirectory = _repoRoot;
        }
        // Prompt delivered via stdin, not as CLI arg — avoids Windows 32767-char limit

        // Isolation: strip API keys + Claude Code sentinel vars so claude.exe starts fresh
        // (CLAUDECODE=1 / CLAUDE_CODE_CHILD_SESSION=1 cause immediate exit when inherited)
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
        psi.EnvironmentVariables.Remove("CLAUDECODE");
        psi.EnvironmentVariables.Remove("CLAUDE_CODE_CHILD_SESSION");
        psi.EnvironmentVariables.Remove("CLAUDE_CODE_SESSION_ID");
        psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENTRYPOINT");
        psi.EnvironmentVariables.Remove("CLAUDE_AGENT_SDK_VERSION");
        psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING");
        psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENABLE_TASKS");
        psi.EnvironmentVariables.Remove("MCP_CONNECTION_NONBLOCKING");

        return await RunProcessWithStdinAsync(prompt.Role, psi, fullPrompt, ct);
    }

    // GPT actors (Codex): codex exec (reads task from stdin) — subscription, no OPENAI_API_KEY
    // Prompt passed via stdin to avoid Windows 32767-char command-line limit.
    private async Task<ActorRunResult> RunCodexAsync(ActorPrompt prompt, CancellationToken ct)
    {
        if (_codexBin is null)
            return Fail(prompt.Role, "codex.exe not found. Set CODEX_BIN env var or install the Codex VS Code extension.");

        var fullPrompt = $"{prompt.SystemPrompt}\n\n---\n\n{prompt.UserPrompt}";

        var psi = new ProcessStartInfo
        {
            FileName = _codexBin,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot ?? Directory.GetCurrentDirectory(),
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_codexModel);
        if (prompt.Role.NeedsReadOnlySandbox())
        {
            psi.ArgumentList.Add("--sandbox");
            psi.ArgumentList.Add("read-only");
        }
        // Prompt delivered via stdin, not as CLI arg — avoids Windows 32767-char limit

        // Isolation: strip API keys from subprocess env to guarantee subscription mode
        psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");

        return await RunProcessWithStdinAsync(prompt.Role, psi, fullPrompt, ct);
    }

    // Semi-manual: generates prompt, writes to file, returns immediately
    private static ActorRunResult RunSemiManual(ActorPrompt prompt)
    {
        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "prompts");
        Directory.CreateDirectory(outputDir);
        var fileName = $"{prompt.Role}-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
        var filePath = Path.Combine(outputDir, fileName);

        var content = new StringBuilder();
        content.AppendLine("=== SYSTEM PROMPT ===");
        content.AppendLine(prompt.SystemPrompt);
        content.AppendLine();
        content.AppendLine("=== USER PROMPT ===");
        content.AppendLine(prompt.UserPrompt);

        File.WriteAllText(filePath, content.ToString(), Encoding.UTF8);

        var message = $"[SEMI-MANUAL] Prompt écrit dans : {filePath}\n" +
                      $"Copiez le contenu dans ChatGPT web (interface abonnement), " +
                      $"puis fournissez la réponse pour publication Jira.";

        return new ActorRunResult(
            Role: prompt.Role,
            Success: true,
            Output: message,
            ErrorOutput: null,
            ExitCode: 0,
            IsSemiManual: true);
    }

    private async Task<ActorRunResult> RunProcessWithStdinAsync(
        ActorRole role, ProcessStartInfo psi, string stdinContent, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        _job?.AssignProcess(process); // register in job so it dies if launcher exits unexpectedly
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Write prompt via sync path in Task.Run — WriteAsync on anonymous Windows pipes uses
        // AsyncOverSyncWithIoCancellation which fails for large prompts (>64 KB).
        await Task.Run(() =>
        {
            process.StandardInput.Write(stdinContent);
            process.StandardInput.Close();
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_actorTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // The process exited between cancellation and the kill attempt.
            }

            var reason = ct.IsCancellationRequested
                ? "Actor execution cancelled."
                : $"Actor execution timed out after {_actorTimeout.TotalSeconds:0} seconds.";
            return Fail(role, reason);
        }

        return new ActorRunResult(
            Role: role,
            Success: process.ExitCode == 0,
            Output: stdout.ToString(),
            ErrorOutput: stderr.ToString(),
            ExitCode: process.ExitCode,
            IsSemiManual: false);
    }

    private static ActorRunResult Fail(ActorRole role, string message) =>
        new(role, false, string.Empty, message, -1, false);
}
