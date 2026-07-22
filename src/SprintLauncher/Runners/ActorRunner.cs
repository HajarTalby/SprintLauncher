using System.Diagnostics;
using System.Text;
using SprintLauncher.Dialogue;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed class ActorRunner : IDisposable
{
    private readonly string? _claudeBin;
    private readonly string? _codexBin;
    private readonly string? _agyBin;
    // Mutables : commutables EN COURS DE RUN via la commande « !model » de Hajar
    // (2026-07-17 : changer de modèle sur quota sans redémarrer le run).
    private string _claudeModel;
    private string _codexModel;
    private string _agyModel;
    private string _directiveInterpreterModel;

    public string ClaudeModel { get => _claudeModel; set => _claudeModel = value; }
    public string CodexModel { get => _codexModel; set => _codexModel = value; }
    public string AgyModel { get => _agyModel; set => _agyModel = value; }
    public string DirectiveInterpreterModel { get => _directiveInterpreterModel; set => _directiveInterpreterModel = value; }
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
        string? agyBin = null,
        string? claudeModel = null,
        string? codexModel = null,
        string? agyModel = null,
        string? directiveInterpreterModel = null,
        TimeSpan? actorTimeout = null,
        string? repoRoot = null,
        TimeSpan? implementationTimeout = null,
        bool gptPilotageAuto = true)
    {
        _gptPilotageAuto = gptPilotageAuto;
        _claudeBin = claudeBin ?? BinaryLocator.FindClaude();
        _codexBin = codexBin ?? BinaryLocator.FindCodex();
        _agyBin = agyBin ?? BinaryLocator.FindAgy();
        _claudeModel = claudeModel ?? "sonnet-5";
        _codexModel = codexModel ?? "gpt-5.5";
        _agyModel = agyModel ?? "gemini-3-pro";
        _directiveInterpreterModel = directiveInterpreterModel ?? "gpt-5-mini";
        _actorTimeout = actorTimeout ?? TimeSpan.FromMinutes(10);
        // Un vrai dev d'US prend 15-45 min — le timeout dialogue (10 min) tuait
        // les implémentations en plein travail (constaté au run sprint 6).
        _implementationTimeout = implementationTimeout ?? TimeSpan.FromMinutes(60);
        _repoRoot = repoRoot;
    }

    private static bool IsImplementationRole(ActorRole role) =>
        role is ActorRole.ClaudeImplementation or ActorRole.GptImplementation or ActorRole.AgImplementation;

    private TimeSpan TimeoutFor(ActorRole role) =>
        IsImplementationRole(role) ? _implementationTimeout : _actorTimeout;

    public void Dispose() => _job?.Dispose();

    public async Task<ActorRunResult> RunAsync(
        ActorPrompt prompt, CancellationToken ct = default)
        => await RunAsync(prompt, modelOverride: null, ct);

    public async Task<ActorRunResult> RunAsync(
        ActorPrompt prompt, string? modelOverride, CancellationToken ct = default)
    {
        if (prompt.Role.IsSemiManual() && !_gptPilotageAuto)
            return RunSemiManual(prompt);

        if (prompt.Role.IsClaudeFamily())
            return await RunClaudeAsync(prompt, modelOverride, ct);

        if (prompt.Role.IsAgFamily())
            return await RunAgyAsync(prompt, modelOverride, ct);

        return await RunCodexAsync(prompt, modelOverride, ct);
    }

    public async Task<DirectiveInterpretation?> InterpretDirectiveAsync(
        string directive,
        IReadOnlyCollection<string> knownTargets,
        IReadOnlyCollection<string> knownPhases,
        CancellationToken ct = default)
    {
        if (_codexBin is null || string.IsNullOrWhiteSpace(directive))
            return null;

        var prompt =
            "Tu interpretes une directive de Hajar pour Sprint Launcher. " +
            "Reponds uniquement par un objet JSON, sans markdown. Schema attendu: " +
            "{\"intent\":\"corriger|relancer|reorienter|question|directive\",\"targets\":{\"actors\":[],\"groups\":[]},\"phase_order\":{\"action\":\"replay|insert|skip_to\",\"phase\":\"Analysis|FamilyClaude|FamilyGpt|CommitteePilotage|CommitteeArbitrage|Qa|Retrospective\"}}. " +
            "Omet phase_order s'il n'y a pas d'ordre de phase. Omet les cibles inconnues. " +
            $"Cibles connues: {string.Join(", ", knownTargets)}. Phases connues: {string.Join(", ", knownPhases)}.\n\n" +
            $"Directive: {directive}";

        var lastMsgFile = Path.Combine(Path.GetTempPath(), $"codex-directive-interpretation-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo
        {
            FileName = _codexBin,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot ?? Directory.GetCurrentDirectory(),
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        MarkAsActor(psi);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_directiveInterpreterModel);
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("read-only");
        psi.ArgumentList.Add("--output-last-message");
        psi.ArgumentList.Add(lastMsgFile);
        psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
            _job?.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeoutCts.Token);

            var output = File.Exists(lastMsgFile)
                ? (await File.ReadAllTextAsync(lastMsgFile, CancellationToken.None)).Trim()
                : stdout.ToString();
            return process.ExitCode == 0
                ? DirectiveInterpretationParser.TryParse(output, directive)
                : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            try { if (!HasExitedSafe(process)) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        finally
        {
            try { File.Delete(lastMsgFile); } catch (IOException) { }
        }
    }

    /// Nom de la variable qui signale « ce process est un acteur du sprint launcher ».
    /// Les hooks du repo cible s'en servent pour ne PAS injecter de contexte de session.
    public const string ActorEnvVar = "SPRINTLAUNCHER_ACTOR";

    // Les acteurs tournent avec WorkingDirectory = repo cible, donc claude.exe y démarre
    // une vraie session et déclenche les hooks SessionStart de CE repo. Au run sprint 6 du
    // 2026-07-19, le hook handoff de SERZENIA a injecté le handoff de la session de dev du
    // launcher dans l'acteur de pilotage : il a exécuté cette to-do (merges, push) au lieu
    // de piloter le sprint. Le marqueur ci-dessous laisse les hooks se désactiver eux-mêmes.
    private static void MarkAsActor(ProcessStartInfo psi) => psi.EnvironmentVariables[ActorEnvVar] = "1";

    internal const int WindowsCommandLineLimit = 32767;
    internal const int AgyPromptArgumentSafetyLimit = 30000;

    internal sealed record PreparedAgyInvocation(ProcessStartInfo StartInfo, string PromptFile, string? Error);

    /// <summary>
    /// Consigne courte passée à `agy -p` quand le prompt est trop long pour la ligne de
    /// commande : elle ne fait que renvoyer vers le fichier qui porte le vrai prompt.
    /// Formulation validée en smoke réel (docs/141-ag-smoke.md).
    /// </summary>
    internal static string BuildAgyPromptFileInstruction(string promptFile) =>
        $"Lis integralement le fichier {promptFile} et execute la consigne qu'il contient. " +
        "Ce fichier porte ton prompt complet : ne demande aucune clarification, " +
        "ne resume pas le fichier, execute-le.";

    internal static PreparedAgyInvocation PrepareAgyInvocation(
        ActorPrompt prompt,
        string agyBin,
        string model,
        string? modelOverride,
        string? repoRoot)
    {
        var fullPrompt = $"{prompt.SystemPrompt}\n\n---\n\n{prompt.UserPrompt}";
        var promptFile = Path.Combine(Path.GetTempPath(), $"agy-prompt-{prompt.Role}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(promptFile, fullPrompt, new UTF8Encoding(false));

        var psi = new ProcessStartInfo
        {
            FileName = agyBin,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot ?? Directory.GetCurrentDirectory(),
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        MarkAsActor(psi);
        psi.ArgumentList.Add("-p");

        // agy n'accepte le prompt QUE sur la ligne de commande : `-p` exige son argument
        // ("flag needs an argument: -p" sinon), il n'y a pas de stdin. Or Windows plafonne
        // une ligne de commande à 32767 caractères, et les prompts du SL montent bien
        // au-delà (534 Ko pour le pilotage du run sprint 6).
        //
        // Contournement validé en smoke réel le 2026-07-19 (docs/141-ag-smoke.md) : le
        // prompt est écrit dans un fichier, son dossier est ajouté au workspace, et seule
        // une consigne courte qui pointe dessus passe en argument. Teste avec 75 Ko :
        // exit 0, consigne finale exécutée, aucune troncature.
        var promptFileArgumentTemplate = Environment.GetEnvironmentVariable("AGY_PROMPT_FILE_ARGUMENT");
        if (!string.IsNullOrWhiteSpace(promptFileArgumentTemplate))
        {
            // Échappatoire si Google publie un contrat natif (@file, --prompt-file…).
            psi.ArgumentList.Add(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                promptFileArgumentTemplate,
                promptFile));
        }
        else if (fullPrompt.Length <= AgyPromptArgumentSafetyLimit)
        {
            psi.ArgumentList.Add(fullPrompt);
        }
        else
        {
            psi.ArgumentList.Add(BuildAgyPromptFileInstruction(promptFile));
        }

        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(modelOverride) ? model : modelOverride);
        if (repoRoot is not null)
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(repoRoot);
        }
        // Le dossier du prompt doit être dans le workspace, sinon agy ne peut pas le lire.
        // Il vit hors du repo pour ne pas polluer l'arbre git de l'acteur.
        var promptDir = Path.GetDirectoryName(promptFile);
        if (promptDir is not null)
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(promptDir);
        }
        if (prompt.Role.IsExecutionRole() && !prompt.ForceReadOnly)
            psi.ArgumentList.Add("--dangerously-skip-permissions");
        if (prompt.Role.NeedsReadOnlySandbox() || prompt.ForceReadOnly)
            psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("--print-timeout");
        psi.ArgumentList.Add("60m");

        psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");

        return new PreparedAgyInvocation(psi, promptFile, null);
    }

    // Claude actors: claude -p (reads prompt from stdin) — subscription, no ANTHROPIC_API_KEY
    // Prompt passed via stdin to avoid Windows 32767-char command-line limit.
    private async Task<ActorRunResult> RunClaudeAsync(ActorPrompt prompt, string? modelOverride, CancellationToken ct)
    {
        if (_claudeBin is null)
            return Fail(prompt.Role, "claude.exe not found. Set CLAUDE_BIN env var or install Claude Desktop App.");

        var model = string.IsNullOrWhiteSpace(modelOverride) ? _claudeModel : modelOverride;
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
            // stdin SANS BOM : Encoding.UTF8 émet U+FEFF à la 1re écriture sur un pipe,
            // et le parseur stream-json de claude rejette la ligne (constaté au smoke
            // live du 2026-07-16 : "JSON Parse error: Unrecognized token").
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Chat live : messages utilisateur injectables en cours de tour. Le prompt
        // initial et les interventions passent par le même flux stream-json d'entrée.
        var liveChat = LiveInputDir is not null;

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
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
        MarkAsActor(psi);

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
    private async Task<ActorRunResult> RunCodexAsync(ActorPrompt prompt, string? modelOverride, CancellationToken ct)
    {
        if (_codexBin is null)
            return Fail(prompt.Role, "codex.exe not found. Set CODEX_BIN env var or install the Codex VS Code extension.");

        var model = string.IsNullOrWhiteSpace(modelOverride) ? _codexModel : modelOverride;
        // Chat live : `codex app-server` (JSON-RPC stdio) permet d'orienter le tour en
        // cours (turn/steer) — le vrai chat live que Hajar a en VS Code. Le mode exec
        // one-shot reste le repli par défaut tant que LiveInputDir n'est pas positionné.
        if (LiveInputDir is not null)
            return await RunCodexLiveAsync(prompt, model, ct);

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
            // stdin SANS BOM : Encoding.UTF8 émet U+FEFF à la 1re écriture sur un pipe,
            // et le parseur stream-json de claude rejette la ligne (constaté au smoke
            // live du 2026-07-16 : "JSON Parse error: Unrecognized token").
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        MarkAsActor(psi);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
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

    // AG actors: agy -p "<prompt>" prints plain text. No JSON/streaming contract is documented.
    private async Task<ActorRunResult> RunAgyAsync(ActorPrompt prompt, string? modelOverride, CancellationToken ct)
    {
        if (_agyBin is null)
            return Fail(prompt.Role, "agy.exe not found. Set AGY_BIN env var or install Google Antigravity CLI.");

        var invocation = PrepareAgyInvocation(
            prompt,
            _agyBin,
            _agyModel,
            modelOverride,
            _repoRoot);

        try
        {
            if (invocation.Error is not null)
                return Fail(prompt.Role, invocation.Error);

            return await RunProcessNoStdinAsync(prompt.Role, invocation.StartInfo, ct);
        }
        finally
        {
            try { File.Delete(invocation.PromptFile); } catch (IOException) { }
        }
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
    private async Task<ActorRunResult> RunCodexLiveAsync(ActorPrompt prompt, string? modelOverride, CancellationToken ct)
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
            // stdin SANS BOM : Encoding.UTF8 émet U+FEFF à la 1re écriture sur un pipe,
            // et le parseur stream-json de claude rejette la ligne (constaté au smoke
            // live du 2026-07-16 : "JSON Parse error: Unrecognized token").
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        MarkAsActor(psi);
        psi.ArgumentList.Add("app-server");
        psi.EnvironmentVariables["CODEX_MODEL"] = string.IsNullOrWhiteSpace(modelOverride) ? _codexModel : modelOverride;
        psi.EnvironmentVariables.Remove("OPENAI_API_KEY");
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");

        using var process = new Process { StartInfo = psi };
        var accumulated = new StringBuilder();
        var stderr = new StringBuilder();
        string? threadId = null, turnId = null, lastItemId = null;
        var liveAtLineStart = true;
        var turnDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int nextId = 1;
        var cwd = _repoRoot ?? Directory.GetCurrentDirectory();

        StreamWriter? liveWriter = null;
        if (LiveOutputDir is not null)
            try { liveWriter = new StreamWriter(Path.Combine(LiveOutputDir, $"live-{prompt.Role}.txt"), append: false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (IOException) { }

        // SL_LIVE_DEBUG=1 : trace JSONL brute du protocole (diagnostic app-server).
        StreamWriter? rawLog = null;
        if (Environment.GetEnvironmentVariable("SL_LIVE_DEBUG") == "1" && LiveOutputDir is not null)
            try { rawLog = new StreamWriter(Path.Combine(LiveOutputDir, $"raw-{prompt.Role}.jsonl"), append: false, new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (IOException) { }

        void Send(string json)
        {
            try { rawLog?.WriteLine($">> {json}"); } catch (ObjectDisposedException) { }
            try { process.StandardInput.WriteLine(json); process.StandardInput.Flush(); }
            catch (IOException) { } catch (ObjectDisposedException) { }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try { rawLog?.WriteLine($"<< {e.Data}"); } catch (ObjectDisposedException) { }
            var msg = CodexAppServerProtocol.Parse(e.Data);
            if (msg is null) return;

            // Réponse d'erreur JSON-RPC : visible dans stderr du résultat (diagnostic).
            if (msg.Root.TryGetProperty("error", out var errEl))
                stderr.AppendLine($"[app-server error] {e.Data}");

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
                // Chaque item = un paragraphe : sans séparateur, les messages successifs
                // de l'agent se collent en un bloc illisible (retour Hajar, 2026-07-17).
                var itemId = CodexAppServerProtocol.ItemId(msg.Root);
                if (itemId is not null && lastItemId is not null && itemId != lastItemId && accumulated.Length > 0)
                {
                    accumulated.AppendLine().AppendLine();
                    WriteLiveText(liveWriter, ref liveAtLineStart, Environment.NewLine + Environment.NewLine);
                }
                if (itemId is not null) lastItemId = itemId;

                accumulated.Append(delta);
                WriteLiveText(liveWriter, ref liveAtLineStart, delta);
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
                            var resolved = ResolveLiveAttachments(prompt.Role, line);
                            Send(CodexAppServerProtocol.TurnSteer(nextId++, threadId, turnId, WrapLiveIntervention(resolved)));
                            NotifyLiveDelivery(prompt.Role, line);
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
        SalvageLeftoverInterventions(inbox, prompt.Role); // arrivées après la fin du tour → directives
        if (!HasExitedSafe(process))
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        liveWriter?.Dispose();
        rawLog?.Dispose();

        var output = accumulated.ToString().Trim();
        var errorText = stderr.ToString();
        bool quota = QuotaDetector.IsQuotaExhausted(output, errorText) || QuotaDetector.IsQuotaExhaustedOutput(output);
        var reset = quota ? QuotaResetParser.TryParse(output, errorText, DateTimeOffset.Now)?.ResetAt : null;

        if (output.Length == 0 && !turnDone.Task.IsCompleted)
            return new ActorRunResult(prompt.Role, Success: false, Output: output,
                ErrorOutput: $"codex app-server : aucune sortie ({exitReason}). Handshake/protocole à valider (smoke live requis).\n{errorText}",
                ExitCode: -1, IsSemiManual: false, IsQuotaExhausted: quota, QuotaResetAt: reset);

        return new ActorRunResult(prompt.Role, Success: !quota && output.Length > 0, Output: output,
            ErrorOutput: errorText, ExitCode: 0, IsSemiManual: false, IsQuotaExhausted: quota, QuotaResetAt: reset);
    }

    // Semi-manual: generates prompt, writes to file, returns immediately
    private static ActorRunResult RunSemiManual(ActorPrompt prompt)
    {
        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "prompts");
        Directory.CreateDirectory(outputDir);
        var fileName = ArtifactNaming.Prompt(prompt.Role);
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

    private async Task<ActorRunResult> RunProcessNoStdinAsync(
        ActorRole role, ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var liveAtLineStart = true;

        StreamWriter? liveWriter = null;
        if (LiveOutputDir is not null)
        {
            try
            {
                liveWriter = new StreamWriter(
                    Path.Combine(LiveOutputDir, $"live-{role}.txt"),
                    append: false,
                    new UTF8Encoding(false))
                { AutoFlush = true };
            }
            catch (IOException) { liveWriter = null; }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            WriteLiveLine(liveWriter, ref liveAtLineStart, e.Data);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        _job?.AssignProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { process.StandardInput.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }

        var timeout = TimeoutFor(role);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

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
            catch (InvalidOperationException) { }
            liveWriter?.Dispose();

            var reason = ct.IsCancellationRequested
                ? "Actor execution cancelled."
                : $"Actor execution timed out after {timeout.TotalSeconds:0} seconds.";
            return Fail(role, reason);
        }

        liveWriter?.Dispose();
        var outputText = stdout.ToString();
        var errorText = stderr.ToString();
        var success = process.ExitCode == 0;
        var quotaExhausted = (!success && QuotaDetector.IsQuotaExhausted(outputText, errorText))
            || QuotaDetector.IsQuotaExhaustedOutput(outputText);

        return new ActorRunResult(
            Role: role,
            Success: success && !quotaExhausted,
            Output: outputText,
            ErrorOutput: errorText,
            ExitCode: process.ExitCode,
            IsSemiManual: false,
            IsQuotaExhausted: quotaExhausted);
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
        var liveAtLineStart = true;

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
                WriteLiveLine(liveWriter, ref liveAtLineStart, liveLine);
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
            ? Task.Run(() => PumpLiveStdin(process, stdinContent, role, liveInput,
                () => interpreter?.TurnsCompleted ?? 0, timeoutCts.Token))
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
        var reset = quotaExhausted ? QuotaResetParser.TryParse(outputText, errorText, DateTimeOffset.Now)?.ResetAt : null;

        return new ActorRunResult(
            Role: role,
            Success: success && !quotaExhausted, // un message de quota n'est jamais un livrable
            Output: outputText,
            ErrorOutput: errorText,
            ExitCode: process.ExitCode,
            IsSemiManual: false,
            IsQuotaExhausted: quotaExhausted,
            QuotaResetAt: reset);
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
        Func<string, string> frame, Func<int> turnsCompleted, CancellationToken ct)
    {
        var stdin = process.StandardInput;
        LiveInputInbox? inbox = LiveInputDir is not null
            ? new LiveInputInbox(LiveInputInbox.PathFor(LiveInputDir, role))
            : null;
        int sent = 0;
        try
        {
            stdin.WriteLine(initialMessage);
            stdin.Flush();
            sent = 1;

            while (!ct.IsCancellationRequested && !HasExitedSafe(process))
            {
                if (inbox is not null)
                {
                    foreach (var line in inbox.DrainNewLines())
                    {
                        if (!LiveChatProtocol.IsSendable(line)) continue;
                        var resolved = ResolveLiveAttachments(role, line);
                        stdin.WriteLine(frame(WrapLiveIntervention(resolved)));
                        stdin.Flush();
                        sent++;
                        NotifyLiveDelivery(role, line);
                    }
                }

                // ANTI-DEADLOCK : en session stream-json, claude attend indéfiniment du
                // stdin après son tour — et nous attendrions sa sortie. Dès que chaque
                // message envoyé a eu sa réponse (événement result) et que l'inbox est
                // vide, on ferme stdin : c'est le signal de fin de session.
                if (turnsCompleted() >= sent) break;

                Thread.Sleep(LivePollInterval);
            }
        }
        catch (IOException) { }               // tube cassé (kill/fin) — fin normale du pompage
        catch (ObjectDisposedException) { }   // process déjà terminé
        finally
        {
            try { stdin.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
            SalvageLeftoverInterventions(inbox, role);
        }
    }

    // Interventions arrivées trop tard pour ce tour : reversées dans pending-directive.txt
    // (même dossier), où le flux de directives adressées du CLI les ramasse — jamais perdues.
    private void SalvageLeftoverInterventions(LiveInputInbox? inbox, ActorRole role)
    {
        if (inbox is null || LiveInputDir is null) return;
        var leftovers = inbox.DrainNewLines().Where(LiveChatProtocol.IsSendable).ToList();
        if (leftovers.Count == 0) return;
        try
        {
            File.AppendAllLines(
                System.IO.Path.Combine(LiveInputDir, "pending-directive.txt"),
                leftovers.Select(l => $"@{role} {l}"), new UTF8Encoding(false));
            AppendLiveNote(role, $"⚑ {leftovers.Count} intervention(s) arrivée(s) après la fin du tour — reversée(s) en directive pour {role}.");
        }
        catch (IOException) { }
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
        var liveAtLineStart = true;
        try
        {
            using var writer = new StreamWriter(
                System.IO.Path.Combine(LiveOutputDir, $"live-{role}.txt"),
                append: true, new UTF8Encoding(false));
            WriteLiveLine(writer, ref liveAtLineStart, note);
        }
        catch (IOException) { }
    }

    private static void WriteLiveLine(StreamWriter? writer, ref bool atLineStart, string line)
    {
        WriteLiveText(writer, ref atLineStart, line);
        WriteLiveText(writer, ref atLineStart, Environment.NewLine);
    }

    private static void WriteLiveText(StreamWriter? writer, ref bool atLineStart, string text)
    {
        if (writer is null || text.Length == 0) return;
        try
        {
            foreach (var c in text)
            {
                if (atLineStart && c is not '\r' and not '\n')
                {
                    writer.Write($"[{DateTime.Now:HH:mm:ss}] ");
                    atLineStart = false;
                }
                writer.Write(c);
                if (c == '\n') atLineStart = true;
            }
        }
        catch (ObjectDisposedException) { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // Pièces jointes (SERZENIA-144 Lot 3) : une ligne live-input peut porter le même
    // marqueur qu'une directive déposée par fichier. Extraites et copiées ICI, dans
    // le dossier du run de CET acteur (LiveInputDir == artifactsDir du run), avant
    // d'habiller le message — l'acteur ne voit jamais le marqueur brut, seulement des
    // chemins de fichiers réels.
    private string ResolveLiveAttachments(ActorRole role, string line)
    {
        var (cleanText, sourcePaths) = DirectiveAttachments.Extract(line);
        if (sourcePaths.Count == 0 || LiveInputDir is null) return line;

        var attachDir = System.IO.Path.Combine(LiveInputDir, "attachments", role.ToString());
        var copied = DirectiveAttachments.CopyToRunFolder(sourcePaths, attachDir);
        var body = cleanText.Length == 0 ? DirectiveAttachments.EmptyTextPlaceholder : cleanText;
        return body + DirectiveAttachments.FormatForPrompt(copied);
    }

    // Habillage d'une intervention live : l'acteur doit ACCUSER RÉCEPTION dans sa
    // sortie — sans ça, impossible de savoir si le message a été pris en compte
    // (retour de Hajar, 2026-07-17 : « je ne vois pas s'il a pris en compte ou pas »).
    private static string WrapLiveIntervention(string text) =>
        "[INTERVENTION DE HAJAR — reçue en cours de tour, autorité immédiate]\n" + text +
        "\n(Confirme la prise en compte dans ta sortie par une ligne commençant par " +
        "« ⚑ Intervention de Hajar prise en compte : » suivie de ce que tu en fais, puis applique-la.)";

    // Remise visible partout : sortie live de l'acteur (SORTIE ACTEUR) + événement
    // pour le fil DISCUSSION et le journal de l'UI.
    private void NotifyLiveDelivery(ActorRole role, string line)
    {
        AppendLiveNote(role, $"⚑ intervention transmise à {role} : {Truncate(line, 80)}");
        Events.EventEmitter.Emit("live-delivered", new { target = role.ToString(), text = line });
    }

    private static ActorRunResult Fail(ActorRole role, string message) =>
        new(role, false, string.Empty, message, -1, false);
}
