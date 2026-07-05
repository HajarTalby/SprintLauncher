using System.Diagnostics;
using System.Text;
using SprintLauncher.Config;
using SprintLauncher.Jira;
using SprintLauncher.Memory;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;

// Force UTF-8 output so the WPF UI receives correctly encoded text.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;

// Pause on unhandled exception so the window doesn't vanish when double-clicked.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = (Exception)e.ExceptionObject;
    Console.Error.WriteLine($"\n! Erreur : {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"  Cause   : {ex.InnerException.Message}");
    PauseIfInteractive();
    Environment.Exit(1);
};

static void PauseIfInteractive()
{
    if (!Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Appuyez sur une touche pour fermer...");
        Console.ReadKey(true);
    }
}

// ─── Correction 5: graceful shutdown ─────────────────────────────────────────
using var shutdownCts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\n⚠ Arrêt demandé — arrêt propre des acteurs en cours...");
    try { shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
};
// ProcessExit fires after `using var shutdownCts` is disposed on normal exit — guard accordingly.
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
};

// ─── Config ───────────────────────────────────────────────────────────────────
bool dryRun = !args.Contains("--write");
bool listRoles = args.Contains("--list-roles");
bool noCache = args.Contains("--no-cache");
bool resume = args.Contains("--resume");
bool interactive = args.Contains("--interactive");
string? publishManualRole = GetArg(args, "--publish-manual");
string? publishManualFile = GetArg(args, "--from-file");

var modeArg = GetArg(args, "--mode");
var sessionMode = modeArg?.ToLowerInvariant() switch
{
    "cadrage"   => SessionMode.Cadrage,
    "execution" => SessionMode.Execution,
    null        => SessionMode.Execution,
    _           => throw new ArgumentException($"--mode inconnu : '{modeArg}'. Valeurs valides : cadrage, execution"),
};

var optionValues = new HashSet<int>();
foreach (var flag in new[] { "--publish-manual", "--from-file", "--mode", "--sprint" })
{
    var index = Array.IndexOf(args, flag);
    if (index >= 0 && index + 1 < args.Length)
        optionValues.Add(index + 1);
}

var issueKeys = args
    .Select((value, index) => (value, index))
    .Where(item => !item.value.StartsWith("--") && !optionValues.Contains(item.index))
    .Select(item => item.value)
    .ToArray();

// Sprint tickets are resolved from Jira after config load (see below, after JiraClient creation)
var sprintArg = GetArg(args, "--sprint");

// ─── --list-roles ──────────────────────────────────────────────────────────────
if (listRoles)
{
    Console.WriteLine("Acteurs disponibles:");
    ActorGroup? lastGroup = null;
    foreach (ActorRole role in Enum.GetValues<ActorRole>())
    {
        var group = role.GetGroup();
        if (group != lastGroup)
        {
            Console.WriteLine(group.GetGroupLabel());
            lastGroup = group;
        }
        Console.WriteLine($"  {role,-28} [{role.ToSignatureTag()}]{(role.IsSemiManual() ? " (semi-manuel)" : "")}");
    }
    return 0;
}

// ─── Usage guard — before config load so no .env required just to show help ───
if (issueKeys.Length == 0 && sprintArg is null && publishManualRole is null)
{
    Console.Error.WriteLine("Usage: sprint-launcher <ISSUE-KEY> [<ISSUE-KEY> ...] [--write] [--no-cache] [--resume] [--interactive]");
    Console.Error.WriteLine("       sprint-launcher --publish-manual <ROLE> --from-file <path> <ISSUE-KEY> [--write]");
    Console.Error.WriteLine("       sprint-launcher --list-roles");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --interactive   Pause entre chaque groupe d'acteurs — GO/NO-GO manuel.");
    Console.Error.WriteLine("  --mode cadrage  Pipeline pilotage-first : Comité Pilotage → Familles → Arbitrage → QA.");
    Console.Error.WriteLine("  --mode execution (défaut) Pipeline standard : Familles → Comité Pilotage → Arbitrage → QA.");
    PauseIfInteractive();
    return 1;
}

// Configuration is required only for commands that access Jira or launch actors.
var config = SprintLauncherConfig.Load();
if (config.SerzeniaRepoRoot is not null)
    Console.WriteLine($"  Dépôt SERZENIA : {config.SerzeniaRepoRoot}");
else
    Console.WriteLine("  ! Dépôt SERZENIA non trouvé — définir SERZENIA_REPO dans .env pour que les agents implémentation accèdent au code source.");

// ─── --publish-manual ──────────────────────────────────────────────────────────
if (publishManualRole is not null)
{
    if (issueKeys.Length == 0 || publishManualFile is null)
    {
        Console.Error.WriteLine("Usage: sprint-launcher --publish-manual <ROLE> --from-file <path> <ISSUE-KEY> [--write]");
        return 1;
    }

    if (!Enum.TryParse<ActorRole>(publishManualRole, ignoreCase: true, out var manualRole))
    {
        Console.Error.WriteLine($"Unknown role '{publishManualRole}'. Use --list-roles to see available roles.");
        return 1;
    }

    var responseText = await File.ReadAllTextAsync(publishManualFile);
    using var http = new HttpClient();
    var manualPublisher = new JiraCommentPublisher(http, config.JiraBaseUrl, config.JiraEmail, config.JiraApiToken, dryRun);
    foreach (var key in issueKeys)
    {
        var result = await manualPublisher.PublishManualAsync(key, manualRole, responseText);
        PrintPublishResult(key, manualRole, result);
    }
    return 0;
}

// ─── Main flow: read issues → build prompts → run actors → publish ─────────────
// Step 1: Read all issues — differential cache or full fetch
using var httpClient = new HttpClient();
var jiraClient = new JiraClient(httpClient, config.JiraBaseUrl, config.JiraEmail, config.JiraApiToken);
var issues = new List<SprintLauncher.Jira.JiraIssue>();

var mode = dryRun ? "DRY-RUN" : "WRITE";
Console.WriteLine($"[{mode}] Mode : {sessionMode.ToLabel()}");

// Sprint resolution: Jira is the source of truth; sprints.json next to the binary is the local cache.
// First run → fetch from Jira + write cache. Next runs → compare cache vs Jira, re-fetch on diff.
if (sprintArg is not null && issueKeys.Length == 0)
{
    Console.WriteLine($"[{mode}] Résolution sprint {sprintArg}...");
    var resolver = new SprintLauncher.Jira.SprintResolver(jiraClient, config.ProjectName);
    var cachePath = Path.Combine(AppContext.BaseDirectory, "sprints.json");
    issueKeys = await resolver.ResolveAsync(sprintArg, noCache, cachePath, shutdownCts.Token);
    Console.WriteLine($"Sprint {sprintArg} : {issueKeys.Length} ticket(s) — {string.Join(", ", issueKeys)}");
}

if (issueKeys.Length == 0)
    throw new InvalidOperationException("Aucun ticket résolu. Passez les clés directement ou vérifiez --sprint.");

Console.WriteLine($"[{mode}] Lecture des issues Jira...");

JiraCacheEntry? cache = noCache ? null : await JiraCache.TryLoadLatestAsync(issueKeys);
if (cache is not null)
    Console.WriteLine($"  Cache trouvé ({cache.SavedAt:yyyy-MM-dd HH:mm} UTC) — lecture différentielle");

foreach (var key in issueKeys)
{
    Console.Write($"  {key}... ");
    SprintLauncher.Jira.JiraIssue issue;

    if (cache is not null && cache.Issues.TryGetValue(key, out var cached))
    {
        var liveTotal = await jiraClient.GetCommentTotalAsync(key);
        if (liveTotal == cached.CommentTotal)
        {
            Console.WriteLine($"{cached.CommentTotal} commentaire(s) [cache]");
            issue = new SprintLauncher.Jira.JiraIssue(key, cached.Summary, cached.Description, cached.Comments);
        }
        else
        {
            Console.WriteLine($"{liveTotal} commentaire(s) [re-fetch, cache avait {cached.CommentTotal}]");
            issue = await jiraClient.GetIssueAsync(key);
        }
    }
    else
    {
        issue = await jiraClient.GetIssueAsync(key);
        Console.WriteLine($"{issue.Comments.Count} commentaire(s)");
    }
    issues.Add(issue);
}

// Persist cache after reads (even in dry-run — cache is local only)
await JiraCache.SaveAsync(issueKeys, issues);

// Step 1b: Sync SERZENIA framework tickets (SERZENIA-70/89/91)
// Reads descriptions, detects changes via hash, injects updated content into all prompts.
Console.WriteLine($"[{mode}] Sync frameworks SERZENIA...");
var frameworkSync = new SprintLauncher.Jira.FrameworkSync(jiraClient, config.FrameworkKeys);
var frameworks = await frameworkSync.SyncAsync(ct: shutdownCts.Token);
if (frameworks.HasChanges)
    Console.WriteLine($"  Frameworks mis à jour — {frameworks.Content.Count} ticket(s) rechargé(s).");

// Step 1c: Load Claude Code session memory (memory/*.md, type=project or inject_to_agents=true)
var agentMemory = MemorySync.Load();
if (agentMemory.HasEntries)
    Console.WriteLine($"  Mémoire projet — {agentMemory.Entries.Count} entrée(s) injectée(s) dans les prompts.");

// Step 2: Build prompts and run actors
var builder = new PromptBuilder(config.ProjectName, config.ApproverName, config.FrameworkKeys);
using var runner = new ActorRunner(
    claudeModel: config.ClaudeModel,
    codexModel: config.CodexModel,
    actorTimeout: TimeSpan.FromSeconds(config.ActorTimeoutSeconds),
    repoRoot: config.SerzeniaRepoRoot);
var publisher = new JiraCommentPublisher(httpClient, config.JiraBaseUrl, config.JiraEmail, config.JiraApiToken, dryRun);

// Collect entries for the HTML report
var reportEntries = new List<ActorReportEntry>();

var sprintTag = sprintArg is not null ? $"sprint{sprintArg}" : "run";
var artifactsDir = Path.Combine("artifacts", sprintTag, string.Join("-", issueKeys));
Directory.CreateDirectory(artifactsDir);
Console.WriteLine($"Artefacts dans : {Path.GetFullPath(artifactsDir)}");

var stateFile  = Path.Combine(artifactsDir, "state.json");
var handoffFile = Path.Combine(artifactsDir, "session-handoff.md");

// The pilotage ticket: first key (sprint-level synthesis goes here, not duplicated on all scope tickets)
var pilotageKey = issueKeys[0];

SprintState sprintState;
if (resume)
{
    sprintState = await SprintStateManager.TryLoadAsync(stateFile) ?? new SprintState
    {
        StartedAt = DateTimeOffset.UtcNow,
        Keys = issueKeys,
        CompletedRoles = []
    };
    if (sprintState.CompletedRoles.Count > 0)
        Console.WriteLine($"  Reprise — acteurs déjà faits : {string.Join(", ", sprintState.CompletedRoles)}");
    if (sprintState.InterruptedRoles.Count > 0)
        Console.WriteLine($"  Reprise — acteurs interrompus (seront rejoués) : {string.Join(", ", sprintState.InterruptedRoles)}");
}
else
{
    sprintState = new SprintState
    {
        StartedAt = DateTimeOffset.UtcNow,
        Keys = issueKeys,
        CompletedRoles = [],
        InterruptedRoles = []
    };
}

// Step 3: Run actors group by group
ActorGroup? lastGroupPrinted = null;

foreach (var group in sessionMode.GetGroupOrder())
{
    if (shutdownCts.IsCancellationRequested) break;

    var rolesInGroup = Enum.GetValues<ActorRole>()
        .Where(r => r.GetGroup() == group)
        .ToArray();

    // Skip group if all roles are completed (and no interrupted ones to replay)
    var allCompleted = rolesInGroup.All(r =>
        sprintState.CompletedRoles.Contains(r.ToString()) &&
        !sprintState.InterruptedRoles.Contains(r.ToString()));

    if (lastGroupPrinted != group)
    {
        Console.WriteLine($"\n{group.GetGroupLabel()}");
        lastGroupPrinted = group;
    }

    if (allCompleted)
    {
        Console.WriteLine("  [déjà complet — ignoré]");
        foreach (var skipped in rolesInGroup)
            reportEntries.Add(new ActorReportEntry(
                Role: skipped, Success: true, IsSemiManual: false, IsSkipped: true,
                ExitCode: 0, ElapsedSeconds: 0, OutputChars: 0,
                ErrorSnippet: null, OutputFilePath: null, SemiManualPromptPath: null));
        continue;
    }

    bool isCollectiveGroup = group is ActorGroup.CommitteePilotage
        or ActorGroup.CommitteeArbitrage
        or ActorGroup.Qa;

    if (isCollectiveGroup)
    {
        await RunCollectiveGroupAsync(
            group, rolesInGroup, issues, pilotageKey,
            sprintState, stateFile, artifactsDir,
            builder, runner, publisher, shutdownCts.Token);
    }
    else
    {
        foreach (var role in rolesInGroup)
        {
            if (shutdownCts.IsCancellationRequested) break;

            if (sprintState.CompletedRoles.Contains(role.ToString()) &&
                !sprintState.InterruptedRoles.Contains(role.ToString()))
            {
                Console.WriteLine($"  ○ {role,-28} [déjà complété — ignoré]");
                continue;
            }

            // Pilotage + implementation actors publish to the reference ticket only.
            // Correction 3 (pilotage) + intérimaire implémentation (per-US analysis is a future US).
            var publishKeys = role.PublishesToReferenceTicketOnly() ? new[] { pilotageKey } : issueKeys;
            var currentKey = publishKeys[0];

            await RunSingleActorAsync(
                role, issues, currentKey, publishKeys, artifactsDir,
                sprintState, stateFile,
                builder, runner, publisher, shutdownCts.Token);
        }
    }

    // ── Mode interactif : checkpoint entre groupes ─────────────────────────────
    if (interactive && !shutdownCts.IsCancellationRequested)
    {
        var nextGroup = sessionMode.GetGroupOrder()
            .SkipWhile(g => g != group)
            .Skip(1)
            .FirstOrDefault();

        Console.WriteLine();
        Console.WriteLine($"  ▶ Groupe [{group.GetGroupLabel()}] terminé.");
        Console.WriteLine($"    Résultats dans : {artifactsDir}");

        if (nextGroup != default)
        {
            Console.Write($"  GO pour continuer avec {nextGroup.GetGroupLabel()} ? [Entrée=oui / n=arrêt] > ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer == "n" || answer == "non" || answer == "no")
            {
                Console.WriteLine("  Arrêt demandé. Utilisez --resume pour reprendre depuis ce point.");
                shutdownCts.Cancel();
            }
        }
    }
}

await SprintStateManager.WriteHandoffAsync(handoffFile, sprintState);

// ─── Point F: generate HTML report ───────────────────────────────────────────
var cacheDir = Path.Combine("artifacts", "jira-cache");
string? cacheFilePath = Directory.Exists(cacheDir)
    ? Directory.GetFiles(cacheDir).OrderByDescending(f => f).FirstOrDefault()
    : null;

var reportPath = await HtmlReportGenerator.GenerateAsync(
    artifactsDir: artifactsDir,
    dryRun: dryRun,
    issueKeys: issueKeys,
    runStartedAt: sprintState.StartedAt,
    entries: reportEntries,
    handoffFilePath: handoffFile,
    cacheFilePath: cacheFilePath);

Console.WriteLine($"\nArtefacts écrits dans : {artifactsDir}");
Console.WriteLine($"Rapport HTML          : {reportPath}");
return 0;

// ─── Single actor execution (famille Claude / famille GPT) ────────────────────
async Task RunSingleActorAsync(
    ActorRole role,
    IReadOnlyList<SprintLauncher.Jira.JiraIssue> issues,
    string primaryKey,
    string[] publishKeys,
    string artifactsDir,
    SprintState state,
    string stateFile,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct)
{
    var prompt = builder.Build(role, issues, primaryKey, mode: sessionMode, frameworks: frameworks, memory: agentMemory);

    var promptFile = Path.Combine(artifactsDir, $"prompt-{role}.txt");
    await File.WriteAllTextAsync(promptFile, $"=== SYSTEM ===\n{prompt.SystemPrompt}\n\n=== USER ===\n{prompt.UserPrompt}");

    // Correction 1: live timer
    var sw = Stopwatch.StartNew();
    if (Console.IsOutputRedirected) Console.WriteLine($"  > {role}");
    else Console.Write($"  > {role,-28}");

    using var timerCts = new CancellationTokenSource();
    var timerTask = StartTimerTask(role.ToString(), sw, timerCts.Token);

    var runResult = await runner.RunAsync(prompt, ct);

    timerCts.Cancel();
    await timerTask;
    sw.Stop();

    PrintActorResult(role.ToString(), sw, runResult);

    var outputFile = Path.Combine(artifactsDir, $"output-{role}.txt");
    await File.WriteAllTextAsync(outputFile, runResult.Output);

    string? semiManualPromptPath = null;
    if (runResult.IsSemiManual)
    {
        // Extract the prompt path from the semi-manual output message
        var match = System.Text.RegularExpressions.Regex.Match(
            runResult.Output, @"\[SEMI-MANUAL\] Prompt écrit dans : (.+)");
        semiManualPromptPath = match.Success ? match.Groups[1].Value.Trim() : null;
    }

    reportEntries.Add(new ActorReportEntry(
        Role: role,
        Success: runResult.Success,
        IsSemiManual: runResult.IsSemiManual,
        IsSkipped: false,
        ExitCode: runResult.ExitCode,
        ElapsedSeconds: (int)sw.Elapsed.TotalSeconds,
        OutputChars: runResult.Output.Length,
        ErrorSnippet: runResult.ErrorOutput?.Trim() is { Length: > 0 } err ? err[..Math.Min(300, err.Length)] : null,
        OutputFilePath: outputFile,
        SemiManualPromptPath: semiManualPromptPath));

    if (runResult.Success)
    {
        foreach (var key in publishKeys)
        {
            var pub = await publisher.PublishAsync(key, runResult);
            PrintPublishResult(key, role, pub);
        }
        state.CompletedRoles.Add(role.ToString());
        state.InterruptedRoles.Remove(role.ToString());
    }
    else if (ct.IsCancellationRequested)
    {
        state.InterruptedRoles.Add(role.ToString());
        Console.WriteLine($"  ⚠ {role} marqué interrupted dans state.json");
    }

    await SprintStateManager.SaveAsync(stateFile, state);
}

// ─── Collective group (comité / QA): sequential deliberation → 1 Jira comment ─
async Task RunCollectiveGroupAsync(
    ActorGroup group,
    ActorRole[] roles,
    IReadOnlyList<SprintLauncher.Jira.JiraIssue> issues,
    string publishKey,
    SprintState state,
    string stateFile,
    string artifactsDir,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct)
{
    var contributions = new List<(ActorRole Role, string Output)>();
    bool anyFailed = false;

    foreach (var role in roles)
    {
        if (ct.IsCancellationRequested) break;

        if (state.CompletedRoles.Contains(role.ToString()) &&
            !state.InterruptedRoles.Contains(role.ToString()))
        {
            Console.WriteLine($"  ○ {role,-28} [déjà complété]");
            // Can't continue deliberation without prior output — restart group from scratch
            contributions.Clear();
            break;
        }

        // Build previous contributions context
        string? previousContribs = contributions.Count > 0
            ? string.Join("\n\n---\n\n", contributions.Select(c =>
                $"### {c.Role} [{c.Role.ToSignatureTag()}]\n{c.Output}"))
            : null;

        var prompt = builder.Build(role, issues, publishKey, previousContribs, sessionMode, frameworks, agentMemory);

        var promptFile = Path.Combine(artifactsDir, $"prompt-{role}.txt");
        await File.WriteAllTextAsync(promptFile, $"=== SYSTEM ===\n{prompt.SystemPrompt}\n\n=== USER ===\n{prompt.UserPrompt}");

        // Correction 1: live timer
        var sw = Stopwatch.StartNew();
        if (Console.IsOutputRedirected) Console.WriteLine($"  > {role}");
        else Console.Write($"  > {role,-28}");

        using var timerCts = new CancellationTokenSource();
        var timerTask = StartTimerTask(role.ToString(), sw, timerCts.Token);

        var runResult = await runner.RunAsync(prompt, ct);

        timerCts.Cancel();
        await timerTask;
        sw.Stop();

        PrintActorResult(role.ToString(), sw, runResult);

        var outputFile = Path.Combine(artifactsDir, $"output-{role}.txt");
        await File.WriteAllTextAsync(outputFile, runResult.Output);

        reportEntries.Add(new ActorReportEntry(
            Role: role,
            Success: runResult.Success,
            IsSemiManual: runResult.IsSemiManual,
            IsSkipped: false,
            ExitCode: runResult.ExitCode,
            ElapsedSeconds: (int)sw.Elapsed.TotalSeconds,
            OutputChars: runResult.Output.Length,
            ErrorSnippet: runResult.ErrorOutput?.Trim() is { Length: > 0 } errC ? errC[..Math.Min(300, errC.Length)] : null,
            OutputFilePath: outputFile,
            SemiManualPromptPath: null));

        if (runResult.Success)
        {
            contributions.Add((role, runResult.Output));
            state.CompletedRoles.Add(role.ToString());
            state.InterruptedRoles.Remove(role.ToString());
            await SprintStateManager.SaveAsync(stateFile, state);
        }
        else
        {
            anyFailed = true;
            if (ct.IsCancellationRequested)
            {
                state.InterruptedRoles.Add(role.ToString());
                Console.WriteLine($"  ⚠ {role} marqué interrupted dans state.json");
                await SprintStateManager.SaveAsync(stateFile, state);
            }
            break;
        }
    }

    if (!anyFailed && contributions.Count > 0 && !ct.IsCancellationRequested)
    {
        // Build combined comment (deliberation collective → 1 seul commentaire Jira)
        var combined = BuildCombinedComment(group, contributions);

        var collectiveFile = Path.Combine(artifactsDir, $"output-{group}-collective.txt");
        await File.WriteAllTextAsync(collectiveFile, combined);

        var pubResult = await publisher.PublishCollectiveAsync(publishKey, group, combined);
        var icon = pubResult.Status switch
        {
            PublishStatus.Posted  => "✓",
            PublishStatus.DryRun  => "~",
            PublishStatus.Skipped => "○",
            PublishStatus.Failed  => "✗",
            _                     => "?"
        };
        Console.WriteLine($"  {icon} [{publishKey}] {pubResult.Status} (commentaire collectif {group})");
    }
}

// ─── Collective comment builder ───────────────────────────────────────────────
static string BuildCombinedComment(ActorGroup group, List<(ActorRole Role, string Output)> contributions)
{
    var label = group switch
    {
        ActorGroup.CommitteePilotage  => "Comité de pilotage",
        ActorGroup.CommitteeArbitrage => "Comité d'arbitrage complet",
        ActorGroup.Qa                 => "Verdict QA",
        _ => group.ToString(),
    };

    var sb = new StringBuilder();
    sb.AppendLine($"## Délibération collective — {label}");
    sb.AppendLine();

    for (int i = 0; i < contributions.Count; i++)
    {
        var (role, output) = contributions[i];
        sb.AppendLine($"### {role} [{role.ToSignatureTag()}]");
        sb.AppendLine();
        sb.AppendLine(output.Trim());
        if (i < contributions.Count - 1)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
    }

    return sb.ToString().TrimEnd();
}

// ─── Timer helper ─────────────────────────────────────────────────────────────
static Task StartTimerTask(string label, Stopwatch sw, CancellationToken ct)
{
    if (Console.IsOutputRedirected) return Task.CompletedTask;

    return Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                await Task.Delay(1000, ct);
                Console.Write($"\r  > {label,-28} {(int)sw.Elapsed.TotalSeconds}s    ");
            }
        }
        catch (TaskCanceledException) { }
    }, ct);
}

// ─── Result printers ──────────────────────────────────────────────────────────
static void PrintActorResult(string label, Stopwatch sw, ActorRunResult result)
{
    var elapsed = (int)sw.Elapsed.TotalSeconds;
    // When stdout is redirected (UI mode), emit clean lines without \r overwrite tricks.
    // In terminal mode, use \r to overwrite the spinning ⟳ line.
    bool redirected = Console.IsOutputRedirected;
    if (result.IsSemiManual)
    {
        if (redirected) Console.WriteLine($"  ~ {label} ({elapsed}s) [SEMI-MANUAL]");
        else            Console.WriteLine($"\r  ~ {label,-28} ({elapsed}s) [SEMI-MANUAL]                    ");
        Console.WriteLine($"    {result.Output}");
    }
    else if (result.Success)
    {
        if (redirected) Console.WriteLine($"  ok {label} ({elapsed}s, {result.Output.Length} chars)");
        else            Console.WriteLine($"\r  ✓ {label,-28} ({elapsed}s, {result.Output.Length} chars)    ");
    }
    else
    {
        var err = result.ErrorOutput?.Trim() ?? "";
        if (redirected) Console.WriteLine($"  echec {label} ({elapsed}s) exit {result.ExitCode}");
        else            Console.WriteLine($"\r  ✗ {label,-28} ({elapsed}s) exit {result.ExitCode}    ");
        if (!string.IsNullOrWhiteSpace(err))
            Console.WriteLine($"    {err[..Math.Min(120, err.Length)]}");
    }
}

static void PrintPublishResult(string key, ActorRole role, PublishResult result)
{
    var icon = result.Status switch
    {
        PublishStatus.Posted  => "✓",
        PublishStatus.DryRun  => "~",
        PublishStatus.Skipped => "○",
        PublishStatus.Failed  => "✗",
        _                     => "?"
    };
    Console.WriteLine($"  {icon} [{key}] {result.Status}: {result.CommentId ?? result.Message?[..Math.Min(80, result.Message?.Length ?? 0)] ?? ""}");
}

static string? GetArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
