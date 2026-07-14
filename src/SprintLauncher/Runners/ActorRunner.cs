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
    private readonly TimeSpan _implementationTimeout;
    private readonly string? _repoRoot;
    // GptPilotage : true (défaut) = exécuté automatiquement via codex (read-only) ;
    // false = flux semi-manuel historique (prompt à coller dans ChatGPT web).
    private readonly bool _gptPilotageAuto;
    // Kills all child processes if the launcher exits for any reason (window close, crash, taskkill).
    private readonly WindowsJobObject? _job = WindowsJobObject.TryCreate();

    // Dossier des sorties live : chaque ligne stdout d'un acteur y est ajoutée au fil
    // de l'eau (live-<role>.txt) pour que l'UI montre ce que l'acteur fait PENDANT
    // son tour, pas seulement à la fin. codex streame sa progression ; claude -p
    // n'émet sa sortie qu'à la fin (limite du mode -p).
    public string? LiveOutputDir { get; set; }

    public ActorRunner(
        string? claudeBin = null,
        string? codexBin = null,
        string? claudeModel = null,
        string? codexModel = null,
        TimeSpan? actorTimeout = null,
        string? repoRoot = null,
        TimeSpan? implementationTimeout = null,
        bool gptPilotageAuto = true)
    {
        _gptPilotageAuto = gptPilotageAuto;
        _claudeBin = claudeBin ?? BinaryLocator.FindClaude();
        _codexBin = codexBin ?? BinaryLocator.FindCodex();
        _claudeModel = claudeModel ?? "claude-opus-4-8";
        _codexModel = codexModel ?? "gpt-5.5";
        _actorTimeout = actorTimeout ?? TimeSpan.FromMinutes(10);
        // Un vrai dev d'US prend 15-45 min — le timeout dialogue (10 min) tuait
        // les implémentations en plein travail (constaté au run sprint 6).
        _implementationTimeout = implementationTimeout ?? TimeSpan.FromMinutes(60);
        _repoRoot = repoRoot;
    }

    private static bool IsImplementationRole(ActorRole role) =>
        role is ActorRole.ClaudeImplementation or ActorRole.GptImplementation;

    private TimeSpan TimeoutFor(ActorRole role) =>
        IsImplementationRole(role) ? _implementationTimeout : _actorTimeout;

    public void Dispose() => _job?.Dispose();

    public async Task<ActorRunResult> RunAsync(
        ActorPrompt prompt, CancellationToken ct = default)
    {
        if (prompt.Role.IsSemiManual() && !_gptPilotageAuto)
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
            // Les prompts sont en français : UTF-8 explicite sur les trois flux.
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_claudeModel);
        // Flux événementiel : réflexion/outils/texte visibles EN TEMPS RÉEL dans
        // l'UI (demande Hajar), le résultat final est extrait de l'événement result.
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        // Implémentation : les outils (édition, bash, git) doivent s'exécuter sans
        // approbation interactive — le GO est porté par le lancement du run (SERZENIA-70).
        // Sans ce mode, claude -p refuse les outils et l'acteur "analyse" au lieu de coder.
        if (IsImplementationRole(prompt.Role))
        {
            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add("bypassPermissions");
        }
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

        return await RunProcessWithStdinAsync(prompt.Role, psi, fullPrompt, ct, interpreter: new StreamJsonInterpreter());
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
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_codexModel);
        // Sans ce flag, codex exec refuse de démarrer hors d'un dépôt git « trusted »
        // ("Not inside a trusted directory") — constaté en test isolé.
        psi.ArgumentList.Add("--skip-git-repo-check");
        // --json : événements JSONL au fil de l'eau → sortie live (réflexion, commandes,
        // fichiers) comme claude, sinon codex n'émet rien avant la fin (« 0 car. »).
        psi.ArgumentList.Add("--json");
        // Livrable final fiable écrit dans un fichier (le stdout est du JSONL).
        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex-last-{prompt.Role}-{Guid.NewGuid():N}.txt");
        psi.ArgumentList.Add("--output-last-message");
        psi.ArgumentList.Add(lastMsgFile);
        if (prompt.Role.NeedsReadOnlySandbox() || prompt.ForceReadOnly)
        {
            psi.ArgumentList.Add("--sandbox");
            psi.ArgumentList.Add("read-only");
        }
        else if (IsImplementationRole(prompt.Role))
        {
            // --full-auto laissait .git en lecture seule ("git add: Permission denied
            // sur .git/index.lock", constaté sur SERZENIA-98) : l'acteur codait sans
            // pouvoir committer. L'implémentation exige les droits complets — le GO
            // est porté par le lancement du run validé (SERZENIA-70).
            psi.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        }
        // Prompt delivered via stdin, not as CLI arg — avoids Windows 32767-char limit

        // Isolation: strip API keys from subprocess env to guarantee subscription mode
        psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");

        return await RunProcessWithStdinAsync(
            prompt.Role, psi, fullPrompt, ct,
            interpreter: new CodexJsonInterpreter(), finalOutputFile: lastMsgFile);
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
        ActorRole role, ProcessStartInfo psi, string stdinContent, CancellationToken ct,
        ILiveInterpreter? interpreter = null, string? finalOutputFile = null)
    {
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        string? liveFile = null;
        StreamWriter? liveWriter = null;
        if (LiveOutputDir is not null)
        {
            try
            {
                liveFile = Path.Combine(LiveOutputDir, $"live-{role}.txt");
                // UTF-8 SANS BOM : sinon le live-<role>.txt démarre par ﻿ (parasite à l'affichage).
                liveWriter = new StreamWriter(liveFile, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (IOException) { liveWriter = null; }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            var liveLine = interpreter is not null ? interpreter.Interpret(e.Data) : e.Data;
            if (liveLine is not null)
                try { liveWriter?.WriteLine(liveLine); } catch (ObjectDisposedException) { }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        _job?.AssignProcess(process); // register in job so it dies if launcher exits unexpectedly
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Le timeout doit couvrir AUSSI l'écriture du prompt : si l'acteur ne lit
        // jamais stdin (ex. codex figé par un conflit OAuth), Write bloque sur le
        // tube plein et un timeout limité à WaitForExit ne se déclencherait jamais
        // (constaté au run réel sprint 6 : 35 min de blocage silencieux).
        var timeout = TimeoutFor(role);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Write prompt via sync path in Task.Run — WriteAsync on anonymous Windows pipes uses
        // AsyncOverSyncWithIoCancellation which fails for large prompts (>64 KB).
        var stdinTask = Task.Run(() =>
        {
            try
            {
                process.StandardInput.Write(stdinContent);
                process.StandardInput.Close();
            }
            catch (IOException) { }                  // tube cassé après kill — géré par le timeout
            catch (ObjectDisposedException) { }      // process déjà terminé
        });

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
            await stdinTask; // se débloque une fois le tube cassé par le kill
            liveWriter?.Dispose(); // le live-<role>.txt reste comme trace du blocage

            var reason = ct.IsCancellationRequested
                ? "Actor execution cancelled."
                : $"Actor execution timed out after {timeout.TotalSeconds:0} seconds (prompt non lu ou acteur figé — vérifier les sessions OAuth concurrentes).";
            return Fail(role, reason);
        }

        await stdinTask;

        liveWriter?.Dispose();
        if (liveFile is not null)
            try { File.Delete(liveFile); } catch (IOException) { } // le fichier final output-<role>.txt prend le relais

        // Livrable : priorité au fichier --output-last-message (codex --json), sinon
        // le résultat extrait du flux, sinon le stdout brut.
        string outputText;
        if (finalOutputFile is not null && File.Exists(finalOutputFile))
        {
            try
            {
                var fromFile = (await File.ReadAllTextAsync(finalOutputFile, CancellationToken.None)).Trim();
                outputText = fromFile.Length > 0 ? fromFile : (interpreter?.Output ?? stdout.ToString());
            }
            catch (IOException) { outputText = interpreter?.Output ?? stdout.ToString(); }
            try { File.Delete(finalOutputFile); } catch (IOException) { }
        }
        else
        {
            outputText = interpreter is not null ? interpreter.Output : stdout.ToString();
        }
        var errorText = stderr.ToString();
        bool success = process.ExitCode == 0;

        // Quota : échec avec motif quota, OU exit 0 avec pour seule sortie un message
        // de limite (claude.exe imprime "You've hit your session limit…" et sort en 0).
        bool quotaExhausted = (!success && QuotaDetector.IsQuotaExhausted(outputText, errorText))
            || QuotaDetector.IsQuotaExhaustedOutput(outputText);

        return new ActorRunResult(
            Role: role,
            Success: success && !quotaExhausted, // un message de quota n'est jamais un livrable
            Output: outputText,
            ErrorOutput: errorText,
            ExitCode: process.ExitCode,
            IsSemiManual: false,
            IsQuotaExhausted: quotaExhausted);
    }

    private static ActorRunResult Fail(ActorRole role, string message) =>
        new(role, false, string.Empty, message, -1, false);
}
