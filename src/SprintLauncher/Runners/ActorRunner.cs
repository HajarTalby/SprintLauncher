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

    // Chat live (SL 2026-07-16) : dossier des boîtes de réception d'interventions.
    // Quand il est renseigné, un acteur capable lit stdin en continu et intègre les
    // messages déposés par l'UI (live-input-<role>.txt) PENDANT son tour.
    // ⚠ Chemin claude stream-json — NON encore validé contre le binaire (quota épuisé
    // à l'écriture). Repli automatique sur le one-shot si LiveInputDir est null.
    public string? LiveInputDir { get; set; }

    // Intervalle de scrutation de la boîte de réception live.
    private static readonly TimeSpan LivePollInterval = TimeSpan.FromMilliseconds(500);

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
        // Chat live : messages utilisateur injectables en cours de tour. Le prompt
        // initial et les interventions passent par le même flux stream-json d'entrée.
        var liveChat = LiveInputDir is not null;

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_claudeModel);
        // Flux événementiel : réflexion/outils/texte visibles EN TEMPS RÉEL dans
        // l'UI (demande Hajar), le résultat final est extrait de l'événement result.
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        if (liveChat)
        {
            // Entrée événementielle : stdin devient un flux de messages utilisateur JSON
            // (un par ligne), permettant d'en envoyer d'autres pendant que l'acteur répond.
            psi.ArgumentList.Add("--input-format");
            psi.ArgumentList.Add("stream-json");
        }
        // Implémentation ET QA : les outils (édition, bash, git, lancement d'app)
        // doivent s'exécuter sans approbation interactive — le GO est porté par le
        // lancement du run (SERZENIA-70). La QA exécute elle-même les scénarios sur
        // la release réelle (décision de Hajar, 2026-07-14).
        if (prompt.Role.IsExecutionRole())
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

        // Le contenu stdin diffère selon le mode : prompt brut (one-shot) vs premier
        // message stream-json (live). Le pompage des interventions suit dans le runner.
        var stdinContent = liveChat ? LiveChatProtocol.ClaudeUserMessage(fullPrompt) : fullPrompt;
        return await RunProcessWithStdinAsync(
            prompt.Role, psi, stdinContent, ct,
            interpreter: new StreamJsonInterpreter(),
            liveInput: liveChat ? LiveChatProtocol.ClaudeUserMessage : null);
    }

    // GPT actors (Codex): codex exec (reads task from stdin) — subscription, no OPENAI_API_KEY
    // Prompt passed via stdin to avoid Windows 32767-char command-line limit.
    private async Task<ActorRunResult> RunCodexAsync(ActorPrompt prompt, CancellationToken ct)
    {
        if (_codexBin is null)
            return Fail(prompt.Role, "codex.exe not found. Set CODEX_BIN env var or install the Codex VS Code extension.");

        // Chat live : `codex app-server` (JSON-RPC stdio) permet d'orienter le tour en
        // cours (turn/steer) — le vrai chat live que Hajar a en VS Code. Le mode exec
        // one-shot reste le repli par défaut tant que LiveInputDir n'est pas positionné.
        if (LiveInputDir is not null)
            return await RunCodexLiveAsync(prompt, ct);

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
        else if (prompt.Role.IsExecutionRole())
        {
            // --full-auto laissait .git en lecture seule ("git add: Permission denied
            // sur .git/index.lock", constaté sur SERZENIA-98) : l'acteur codait sans
            // pouvoir committer. Implémentation ET QA (exécution réelle des scénarios)
            // exigent les droits complets — le GO est porté par le run validé (SERZENIA-70).
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

    // ─── Codex en chat live via app-server (JSON-RPC stdio) ──────────────────────
    // Séquence : initialize → thread/start → turn/start, puis lecture des notifications
    // (item/agentMessage/delta pour le texte, turn/completed pour la fin) tout en
    // relayant les interventions de l'inbox par turn/steer sur le tour actif.
    //
    // ⚠ EXPÉRIMENTAL — formes de message issues des schémas officiels, mais comportement
    // runtime NON validé (quota épuisé à l'écriture, 2026-07-16). En cas d'échec du
    // handshake, on retombe proprement en échec d'acteur (pas de crash) ; le mode exec
    // one-shot reste disponible en retirant LiveInputDir.
    private async Task<ActorRunResult> RunCodexLiveAsync(ActorPrompt prompt, CancellationToken ct)
    {
        var fullPrompt = $"{prompt.SystemPrompt}\n\n---\n\n{prompt.UserPrompt}";
        var readOnly = prompt.Role.NeedsReadOnlySandbox() || prompt.ForceReadOnly;
        var bypass = !readOnly && prompt.Role.IsExecutionRole();

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
        psi.ArgumentList.Add("app-server");
        psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");

        using var process = new Process { StartInfo = psi };
        var accumulated = new StringBuilder();
        var stderr = new StringBuilder();
        string? threadId = null, turnId = null;
        var turnDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int nextId = 1;
        var cwd = _repoRoot ?? Directory.GetCurrentDirectory();

        StreamWriter? liveWriter = null;
        if (LiveOutputDir is not null)
            try { liveWriter = new StreamWriter(Path.Combine(LiveOutputDir, $"live-{prompt.Role}.txt"), append: false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (IOException) { }

        void Send(string json)
        {
            try { process.StandardInput.WriteLine(json); process.StandardInput.Flush(); }
            catch (IOException) { } catch (ObjectDisposedException) { }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var msg = CodexAppServerProtocol.Parse(e.Data);
            if (msg is null) return;

            // Réponses aux requêtes : capture des ids thread/turn.
            if (msg.Id is not null && msg.Method is null)
            {
                threadId ??= CodexAppServerProtocol.ThreadId(msg.Root);
                var t = CodexAppServerProtocol.TurnId(msg.Root);
                if (t is not null) turnId = t;
                if (threadId is not null && turnId is null && msg.Id == 2)
                    Send(CodexAppServerProtocol.TurnStart(nextId++, threadId, fullPrompt)); // id 3
                return;
            }

            // Notifications : texte au fil de l'eau + fin de tour.
            var delta = CodexAppServerProtocol.AgentDelta(msg.Method, msg.Root);
            if (delta is not null)
            {
                accumulated.Append(delta);
                try { liveWriter?.Write(delta); } catch (ObjectDisposedException) { }
            }
            else if (CodexAppServerProtocol.IsTurnCompleted(msg.Method))
            {
                turnDone.TrySetResult(true);
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        _job?.AssignProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Handshake : initialize (id 1) → thread/start (id 2). turn/start (id 3) est
        // envoyé dès que thread.id revient (voir handler ci-dessus).
        Send(CodexAppServerProtocol.Initialize(nextId++, "SprintLauncher", "1.0"));
        Send(CodexAppServerProtocol.ThreadStart(nextId++, cwd, readOnly, bypass));

        var timeout = TimeoutFor(prompt.Role);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Pompe des interventions : turn/steer sur le tour actif, tant que le tour dure.
        var inbox = new LiveInputInbox(LiveInputInbox.PathFor(LiveInputDir!, prompt.Role));
        var steerTask = Task.Run(async () =>
        {
            while (!turnDone.Task.IsCompleted && !timeoutCts.IsCancellationRequested && !HasExitedSafe(process))
            {
                if (threadId is not null && turnId is not null)
                    foreach (var line in inbox.DrainNewLines())
                        if (LiveChatProtocol.IsSendable(line))
                        {
                            Send(CodexAppServerProtocol.TurnSteer(nextId++, threadId, turnId, line));
                            AppendLiveNote(prompt.Role, $"⚑ intervention transmise à {prompt.Role} : {Truncate(line, 80)}");
                        }
                try { await Task.Delay(LivePollInterval, timeoutCts.Token); } catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);

        var exitReason = "";
        try
        {
            await Task.WhenAny(turnDone.Task, process.WaitForExitAsync(timeoutCts.Token));
        }
        catch (OperationCanceledException)
        {
            exitReason = ct.IsCancellationRequested ? "annulé" : $"timeout après {timeout.TotalSeconds:0}s";
        }

        // Clôture propre : interrompre un tour encore actif, fermer stdin, tuer si besoin.
        if (threadId is not null && turnId is not null && !turnDone.Task.IsCompleted)
            Send(CodexAppServerProtocol.TurnInterrupt(nextId++, threadId, turnId));
        try { process.StandardInput.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        await steerTask;
        if (!HasExitedSafe(process))
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        liveWriter?.Dispose();

        var output = accumulated.ToString().Trim();
        var errorText = stderr.ToString();
        bool quota = QuotaDetector.IsQuotaExhausted(output, errorText) || QuotaDetector.IsQuotaExhaustedOutput(output);

        if (output.Length == 0 && !turnDone.Task.IsCompleted)
            return new ActorRunResult(prompt.Role, Success: false, Output: output,
                ErrorOutput: $"codex app-server : aucune sortie ({exitReason}). Handshake/protocole à valider (smoke live requis).\n{errorText}",
                ExitCode: -1, IsSemiManual: false, IsQuotaExhausted: quota);

        return new ActorRunResult(prompt.Role, Success: !quota && output.Length > 0, Output: output,
            ErrorOutput: errorText, ExitCode: 0, IsSemiManual: false, IsQuotaExhausted: quota);
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
        ILiveInterpreter? interpreter = null, string? finalOutputFile = null,
        // Non null : mode chat live — stdin reste ouvert, les interventions déposées
        // par l'UI sont cadrées par cette fonction et poussées au fil de l'eau.
        Func<string, string>? liveInput = null)
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
        // Mode live : après le prompt initial, stdin reste ouvert et on pousse les
        // interventions déposées dans l'inbox jusqu'à la fin du tour (process sorti,
        // timeout, ou annulation). Sinon (one-shot) : prompt puis fermeture immédiate.
        var stdinTask = liveInput is not null
            ? Task.Run(() => PumpLiveStdin(process, stdinContent, role, liveInput, timeoutCts.Token))
            : Task.Run(() =>
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

    // ─── Pompe stdin du chat live ────────────────────────────────────────────────
    // Envoie le message initial, puis garde stdin ouvert et relaie les interventions
    // déposées dans l'inbox tant que le tour dure. Ferme stdin quand le process sort,
    // que le timeout tombe, ou que l'annulation est demandée — c'est cette fermeture
    // qui signale à claude « plus d'entrée » et le laisse conclure proprement.
    //
    // ⚠ NON validé contre le binaire (quota épuisé à l'écriture). Le pont est prudent :
    // toute erreur d'IO stoppe le pompage sans faire échouer le tour (le one-shot
    // interne de claude continue jusqu'au result), et le repli one-shot reste la voie
    // par défaut tant que LiveInputDir n'est pas positionné.
    private void PumpLiveStdin(
        Process process, string initialMessage, ActorRole role,
        Func<string, string> frame, CancellationToken ct)
    {
        var stdin = process.StandardInput;
        try
        {
            stdin.WriteLine(initialMessage);
            stdin.Flush();

            LiveInputInbox? inbox = LiveInputDir is not null
                ? new LiveInputInbox(LiveInputInbox.PathFor(LiveInputDir, role))
                : null;

            while (!ct.IsCancellationRequested && !HasExitedSafe(process))
            {
                if (inbox is not null)
                {
                    foreach (var line in inbox.DrainNewLines())
                    {
                        if (!LiveChatProtocol.IsSendable(line)) continue;
                        stdin.WriteLine(frame(line));
                        stdin.Flush();
                        // Trace dans la sortie live pour que l'UI confirme la remise.
                        AppendLiveNote(role, $"⚑ intervention transmise à {role} : {Truncate(line, 80)}");
                    }
                }
                Thread.Sleep(LivePollInterval);
            }
        }
        catch (IOException) { }               // tube cassé (kill/fin) — fin normale du pompage
        catch (ObjectDisposedException) { }   // process déjà terminé
        finally
        {
            try { stdin.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    private static bool HasExitedSafe(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    // Ajoute une ligne au live-<role>.txt (même fichier que la sortie stdout live) pour
    // que l'UI voie la confirmation de remise d'une intervention.
    private void AppendLiveNote(ActorRole role, string note)
    {
        if (LiveOutputDir is null) return;
        try
        {
            File.AppendAllText(
                System.IO.Path.Combine(LiveOutputDir, $"live-{role}.txt"),
                note + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (IOException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static ActorRunResult Fail(ActorRole role, string message) =>
        new(role, false, string.Empty, message, -1, false);
}
