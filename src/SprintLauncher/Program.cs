using System.Diagnostics;
using System.Text;
using SprintLauncher.Cadrage;
using SprintLauncher.Config;
using SprintLauncher.Dialogue;
using SprintLauncher.Events;
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
bool publishFromArtifacts = args.Contains("--publish-from-artifacts");
string? publishRolesFilter = GetArg(args, "--roles");
string? createUsFile = GetArg(args, "--create-us");
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
foreach (var flag in new[] { "--publish-manual", "--from-file", "--mode", "--sprint", "--roles", "--create-us" })
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
if (issueKeys.Length == 0 && sprintArg is null && publishManualRole is null && createUsFile is null)
{
    Console.Error.WriteLine("Usage: sprint-launcher <ISSUE-KEY> [<ISSUE-KEY> ...] [--write] [--no-cache] [--resume] [--interactive]");
    Console.Error.WriteLine("       sprint-launcher --sprint <id> [options]");
    Console.Error.WriteLine("       sprint-launcher <ISSUE-KEY> --publish-from-artifacts [--roles <csv>] [--write]");
    Console.Error.WriteLine("       sprint-launcher <REF-KEY> --create-us <fichier.json> [--write]");
    Console.Error.WriteLine("       sprint-launcher --publish-manual <ROLE> --from-file <path> <ISSUE-KEY> [--write]");
    Console.Error.WriteLine("       sprint-launcher --list-roles");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --interactive   Checkpoints : GO/intervention/conclure entre rounds de discussion et groupes.");
    Console.Error.WriteLine("  --mode cadrage  Comité Pilotage d'abord (discussion) → US proposées → Analyse → Implémentation → QA.");
    Console.Error.WriteLine("  --mode execution (défaut) Analyse → Implémentation (tour de rôle) → Pilotage → QA.");
    Console.Error.WriteLine("  --publish-from-artifacts  Publie les sorties dry-run validées, sans réexécuter les acteurs.");
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

// ─── --create-us : créer les US validées depuis un fichier de propositions ─────
// Produit par la session de cadrage (us-proposals.json), validé par l'approbatrice
// dans l'UI ou à la main. Dry-run par défaut ; --write crée réellement.
if (createUsFile is not null)
{
    if (!File.Exists(createUsFile))
    {
        Console.Error.WriteLine($"Fichier de propositions introuvable : {createUsFile}");
        return 1;
    }

    var proposalsJson = await File.ReadAllTextAsync(createUsFile);
    var proposals = System.Text.Json.JsonSerializer.Deserialize<List<UsProposal>>(
        proposalsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

    if (proposals.Count == 0)
    {
        Console.Error.WriteLine("Aucune proposition d'US valide dans le fichier.");
        return 1;
    }

    var refIssueKey = issueKeys.FirstOrDefault();
    var projectKey = (refIssueKey ?? config.ProjectName).Split('-')[0];

    using var createHttp = new HttpClient();
    var creator = new JiraIssueCreator(createHttp, config.JiraBaseUrl, config.JiraEmail, config.JiraApiToken, dryRun);

    Console.WriteLine($"[{(dryRun ? "DRY-RUN" : "WRITE")}] Création de {proposals.Count} US (projet {projectKey}{(refIssueKey is null ? "" : $", liées à {refIssueKey}")})...");
    foreach (var proposal in proposals)
    {
        var missing = UsProposalParser.MissingTemplateSections(proposal.Description);
        if (missing.Count > 0)
            Console.WriteLine($"  ! '{proposal.Summary}' — sections SERZENIA-89 absentes : {string.Join(", ", missing)}");

        var created = await creator.CreateAsync(proposal, projectKey, refIssueKey, shutdownCts.Token);
        if (created.Created)
            Console.WriteLine($"  ✓ {created.Key} — {proposal.Summary}");
        else if (created.Error is not null)
            Console.Error.WriteLine($"  ✗ '{proposal.Summary}' — {created.Error}");
        EventEmitter.Emit("us-created", new { key = created.Key, summary = proposal.Summary, dryRun, error = created.Error });
    }

    Console.WriteLine(dryRun
        ? "\nDry-run terminé — relancez avec --write pour créer réellement les US."
        : "\nCréation terminée.");
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

// ─── --publish-from-artifacts : publier les sorties dry-run déjà validées ─────
// Aucune réexécution d'acteur (zéro quota) : les output-*.txt du run précédent
// sont republiés tels quels, avec le même routage que le run d'origine.
if (publishFromArtifacts)
{
    var publishTag = sprintArg is not null ? $"sprint{sprintArg}" : "run";
    var publishDir = Path.Combine("artifacts", publishTag, string.Join("-", issueKeys));
    if (!Directory.Exists(publishDir))
    {
        Console.Error.WriteLine($"Aucun artefact trouvé dans {Path.GetFullPath(publishDir)} — lancez d'abord un dry-run.");
        return 1;
    }

    var rolesFilter = publishRolesFilter?
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var artifactPublisher = new JiraCommentPublisher(httpClient, config.JiraBaseUrl, config.JiraEmail, config.JiraApiToken, dryRun);
    var refKey = issueKeys[0];
    Console.WriteLine($"[{mode}] Publication depuis artefacts : {Path.GetFullPath(publishDir)}");

    // Acteurs individuels (familles) — même routage que le run d'origine
    foreach (ActorRole role in Enum.GetValues<ActorRole>())
    {
        if (role.IsCollective() || role.IsSemiManual()) continue;
        if (rolesFilter is not null && !rolesFilter.Contains(role.ToString())) continue;

        var outputPath = Path.Combine(publishDir, $"output-{role}.txt");
        if (!File.Exists(outputPath)) continue;

        var content = await File.ReadAllTextAsync(outputPath);
        if (string.IsNullOrWhiteSpace(content)) continue;

        var artifactResult = new ActorRunResult(role, true, content, null, 0, false);
        var targets = role.PublishesToReferenceTicketOnly() ? new[] { refKey } : issueKeys;
        foreach (var key in targets)
        {
            var pub = await artifactPublisher.PublishAsync(key, artifactResult, shutdownCts.Token);
            PrintPublishResult(key, role, pub);
        }
    }

    // Groupes collectifs — commentaire de synthèse de la discussion
    foreach (ActorGroup group in Enum.GetValues<ActorGroup>())
    {
        if (rolesFilter is not null && !rolesFilter.Contains(group.ToString())) continue;

        var collectivePath = Path.Combine(publishDir, $"output-{group}-collective.txt");
        if (!File.Exists(collectivePath)) continue;

        var content = await File.ReadAllTextAsync(collectivePath);
        if (string.IsNullOrWhiteSpace(content)) continue;

        var pub = await artifactPublisher.PublishCollectiveAsync(refKey, group, content, shutdownCts.Token);
        Console.WriteLine($"  {(pub.Status == PublishStatus.Posted ? "✓" : pub.Status == PublishStatus.DryRun ? "~" : "○")} [{refKey}] {pub.Status} (collectif {group})");
        EventEmitter.Emit("publish", new { key = refKey, actor = group.ToString(), status = pub.Status.ToString() });
    }

    Console.WriteLine(dryRun
        ? "\nDry-run terminé — relancez avec --write pour publier réellement."
        : "\nPublication terminée.");
    return 0;
}

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
runner.LiveOutputDir = Path.GetFullPath(artifactsDir); // sorties acteurs visibles au fil de l'eau dans l'UI

EventEmitter.Emit("manifest", new
{
    keys = issueKeys,
    dryRun,
    mode = sessionMode.ToLabel(),
    artifactsDir = Path.GetFullPath(artifactsDir),
    maxDialogueRounds = config.MaxDialogueRounds,
    approver = config.ApproverName,
    roles = Enum.GetValues<ActorRole>().Select(r => new
    {
        name = r.ToString(),
        group = r.GetGroup().ToString(),
        groupLabel = r.GetGroup().GetGroupLabel().Trim('─', ' '),
        semiManual = r.IsSemiManual(),
        collective = r.IsCollective(),
    }),
});

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
bool implementationPhaseDone = false;

foreach (var group in sessionMode.GetGroupOrder())
{
    if (shutdownCts.IsCancellationRequested) break;

    var rolesInGroup = Enum.GetValues<ActorRole>()
        .Where(r => r.GetGroup() == group)
        .ToArray();

    bool isCollectiveGroup = group is ActorGroup.Analysis
        or ActorGroup.CommitteePilotage
        or ActorGroup.CommitteeArbitrage
        or ActorGroup.Qa;

    // ── Arbitrage conditionnel : convoqué uniquement sur litige (SERZENIA-143 lot 4)
    if (group == ActorGroup.CommitteeArbitrage &&
        !sprintState.CompletedGroups.Contains(group.ToString()))
    {
        if (!sprintState.LitigeDetected)
        {
            Console.WriteLine($"\n{group.GetGroupLabel()}");
            Console.WriteLine("  [non convoqué — aucun litige détecté en analyse]");
            lastGroupPrinted = group;
            foreach (var skipped in rolesInGroup)
                reportEntries.Add(new ActorReportEntry(
                    Role: skipped, Success: true, IsSemiManual: false, IsSkipped: true,
                    ExitCode: 0, ElapsedSeconds: 0, OutputChars: 0,
                    ErrorSnippet: null, OutputFilePath: null, SemiManualPromptPath: null));
            continue;
        }

        if (interactive)
        {
            Console.WriteLine($"\n{group.GetGroupLabel()}");
            EventEmitter.Emit("checkpoint", new { kind = "group", group = group.ToString(), next = "COMITÉ D'ARBITRAGE (litige détecté)" });
            Console.Write("  ⚠ Litige détecté en analyse. GO pour continuer avec l'arbitrage ? [Entrée=oui / n=non] > ");
            var arbAnswer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (arbAnswer is "n" or "non" or "no")
            {
                Console.WriteLine("  Arbitrage non convoqué — le pipeline continue.");
                lastGroupPrinted = group;
                continue;
            }
        }
    }

    // Skip : groupes collectifs = discussion publiée (CompletedGroups) ;
    // familles = tous les rôles complétés sans interruption à rejouer.
    var allCompleted = isCollectiveGroup
        ? sprintState.CompletedGroups.Contains(group.ToString())
        : rolesInGroup.All(r =>
            sprintState.CompletedRoles.Contains(r.ToString()) &&
            !sprintState.InterruptedRoles.Contains(r.ToString()));

    if (lastGroupPrinted != group)
    {
        Console.WriteLine($"\n{group.GetGroupLabel()}");
        EventEmitter.Emit("group", new { group = group.ToString(), label = group.GetGroupLabel().Trim('─', ' ') });
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

    if (isCollectiveGroup)
    {
        await RunDialogueGroupAsync(
            group, rolesInGroup, issues, pilotageKey,
            sprintState, stateFile, artifactsDir,
            builder, runner, publisher, shutdownCts.Token);
    }
    else
    {
        foreach (var role in rolesInGroup)
        {
            if (shutdownCts.IsCancellationRequested) break;

            // Implémentation : gérée per-US par l'ordonnanceur — tour de rôle
            // ccode/codex + relève sur quota (SERZENIA-143 lot 5).
            if (role is ActorRole.ClaudeImplementation or ActorRole.GptImplementation)
            {
                if (!implementationPhaseDone)
                {
                    implementationPhaseDone = true;
                    await RunImplementationPhaseAsync(
                        issues, artifactsDir, sprintState, stateFile,
                        builder, runner, publisher, shutdownCts.Token);
                }
                continue;
            }

            if (sprintState.CompletedRoles.Contains(role.ToString()) &&
                !sprintState.InterruptedRoles.Contains(role.ToString()))
            {
                Console.WriteLine($"  ○ {role,-28} [déjà complété — ignoré]");
                continue;
            }

            // Pilotage actors publish to the reference ticket only (correction 3).
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
            EventEmitter.Emit("checkpoint", new { kind = "group", group = group.ToString(), next = nextGroup.GetGroupLabel().Trim('─', ' ') });
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
EventEmitter.Emit("run-end", new
{
    reportPath,
    artifactsDir = Path.GetFullPath(artifactsDir),
    dryRun,
    publishable = dryRun && !shutdownCts.IsCancellationRequested,
});
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

    EventEmitter.Emit("actor-start", new { role = role.ToString() });
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

// ─── Phase implémentation : tour de rôle per-US + relève sur quota (lot 5) ─────
async Task RunImplementationPhaseAsync(
    IReadOnlyList<SprintLauncher.Jira.JiraIssue> issues,
    string artifactsDir,
    SprintState state,
    string stateFile,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct)
{
    Console.WriteLine("  [implémentation per-US — tour de rôle ccode/codex, relève sur quota]");

    foreach (var issue in issues)
    {
        if (ct.IsCancellationRequested) break;

        if (state.CompletedUsImplementations.Contains(issue.Key))
        {
            Console.WriteLine($"  ○ {issue.Key,-28} [déjà implémentée — ignorée]");
            continue;
        }

        // Spécialisation front/backend : les US UI et les US backend partent chacune
        // vers leur moteur attitré — pas de chevauchement de code sur le sprint.
        var usType = UsTypeClassifier.Classify(issue.Summary, issue.Description);
        var engineName = ImplementationRotation.PickEngineForUs(
            usType, state.LastImplementer, state.QuotaExhaustedEngines,
            config.EngineFront, config.EngineBack);
        if (engineName is null)
        {
            Console.WriteLine("  ⚠ Les deux moteurs sont à quota épuisé — US restantes en attente. Reprendre avec --resume après reset du quota.");
            break;
        }

        var engine = Enum.Parse<ActorRole>(engineName);
        var typeLabel = usType switch { UsType.Front => " [front]", UsType.Backend => " [backend]", _ => "" };
        Console.WriteLine($"  ▶ {issue.Key}{typeLabel} → {engine}");
        EventEmitter.Emit("implementation-us", new { key = issue.Key, engine = engine.ToString(), relief = false, usType = usType.ToString() });

        var prompt = builder.Build(engine, [issue], issue.Key, mode: sessionMode, frameworks: frameworks, memory: agentMemory);
        var result = await RunDialogueTurnAsync(engine, prompt, artifactsDir, runner, ct);

        // ── Relève : quota épuisé → l'autre moteur reprend avec le handoff ────
        if (result.IsQuotaExhausted)
        {
            state.QuotaExhaustedEngines.Add(engine.ToString());
            await SprintStateManager.SaveAsync(stateFile, state);
            Console.WriteLine($"  ⚠ {engine} à quota épuisé — relève par l'autre moteur...");
            EventEmitter.Emit("quota", new { engine = engine.ToString(), key = issue.Key });

            var reliefName = ImplementationRotation.PickRelief(engine.ToString(), state.QuotaExhaustedEngines);
            if (reliefName is null)
            {
                Console.WriteLine("  ⚠ Aucun moteur de relève disponible — arrêt de la phase implémentation.");
                break;
            }

            var relief = Enum.Parse<ActorRole>(reliefName);
            var reliefBase = builder.Build(relief, [issue], issue.Key, mode: sessionMode, frameworks: frameworks, memory: agentMemory);
            var handoffPrompt = BuildHandoffPrompt(reliefBase, engine, result.Output);

            Console.WriteLine($"  ▶ {issue.Key} → relève par {relief}");
            EventEmitter.Emit("implementation-us", new { key = issue.Key, engine = relief.ToString(), relief = true });
            result = await RunDialogueTurnAsync(relief, handoffPrompt, artifactsDir, runner, ct);

            if (result.IsQuotaExhausted)
            {
                state.QuotaExhaustedEngines.Add(relief.ToString());
                await SprintStateManager.SaveAsync(stateFile, state);
                Console.WriteLine("  ⚠ Moteur de relève également à quota épuisé — arrêt de la phase.");
                EventEmitter.Emit("quota", new { engine = relief.ToString(), key = issue.Key });
                break;
            }
            engine = relief;
        }

        // Une déclaration d'attente de GO n'est PAS une implémentation (lot 7) :
        // en headless personne ne répond — l'US n'est pas marquée complétée.
        if (result.Success && ImplementationOutputGuard.IsAwaitingGo(result.Output))
        {
            Console.WriteLine($"  ! {issue.Key} — {engine} s'est arrêté à une demande de GO : US NON implémentée (rejouée au prochain --resume).");
            EventEmitter.Emit("implementation-blocked", new { key = issue.Key, engine = engine.ToString() });
            continue;
        }

        if (result.Success)
        {
            // Trace per-US en plus du fichier par rôle (écrasé à chaque US)
            await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-{engine}-{issue.Key}.txt"), result.Output);
            var pub = await publisher.PublishAsync(issue.Key, result, ct);
            PrintPublishResult(issue.Key, engine, pub);

            // Revue croisée : l'autre moteur relit, l'implémenteur corrige (lot 7)
            if (config.CrossReviewEnabled && !ct.IsCancellationRequested)
                await RunCrossReviewAsync(issue, engine, result.Output, artifactsDir, state, stateFile, builder, runner, publisher, ct);

            state.LastImplementer = engine.ToString();
            state.CompletedUsImplementations.Add(issue.Key);
            await SprintStateManager.SaveAsync(stateFile, state);
        }
        else if (ct.IsCancellationRequested)
        {
            break;
        }
        else
        {
            Console.WriteLine($"  ✗ {issue.Key} — échec {engine} (non-quota). US suivante ; --resume rejouera celle-ci.");
        }
    }
}

// ─── Revue croisée post-dev (lot 7) : observations par l'autre moteur, ─────────
// correctifs chez l'implémenteur, intervention de Hajar possible entre les deux.
async Task RunCrossReviewAsync(
    SprintLauncher.Jira.JiraIssue issue,
    ActorRole implementer,
    string implementationOutput,
    string artifactsDir,
    SprintState state,
    string stateFile,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct)
{
    var reviewerName = ImplementationRotation.PickRelief(implementer.ToString(), state.QuotaExhaustedEngines);
    if (reviewerName is null)
    {
        Console.WriteLine($"  ○ {issue.Key} — revue croisée sautée (réviseur à quota épuisé).");
        return;
    }

    var reviewer = Enum.Parse<ActorRole>(reviewerName);
    Console.WriteLine($"  ⟲ {issue.Key} — revue croisée : {reviewer} relit {implementer}");

    var reviewPrompt = builder.BuildCrossReview(reviewer, issue, implementer, implementationOutput);
    var review = await RunDialogueTurnAsync(reviewer, reviewPrompt, artifactsDir, runner, ct);
    if (!review.Success)
    {
        if (review.IsQuotaExhausted)
        {
            state.QuotaExhaustedEngines.Add(reviewer.ToString());
            await SprintStateManager.SaveAsync(stateFile, state);
        }
        Console.WriteLine($"  ○ {issue.Key} — revue croisée abandonnée (échec {reviewer}), l'implémentation reste valide.");
        return;
    }

    await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-Review-{issue.Key}.txt"), review.Output);
    EventEmitter.Emit("turn", new { group = $"RevueCroisee-{issue.Key}", speaker = reviewer.ToString(), round = 1, isIntervention = false, content = review.Output.Trim() });

    // Checkpoint : Hajar peut orienter les correctifs (ou les sauter)
    string? directive = null;
    if (interactive)
    {
        EventEmitter.Emit("checkpoint", new { kind = "review", group = $"RevueCroisee-{issue.Key}", round = 1 });
        Console.Write($"  Revue {issue.Key} reçue — [Entrée=appliquer les correctifs / texte=directive / n=passer] > ");
        var answer = Console.ReadLine()?.Trim();
        if (answer?.ToLowerInvariant() is "n" or "non" or "no")
        {
            Console.WriteLine("  Correctifs sautés sur décision de Hajar — revue publiée telle quelle.");
            var reviewOnly = $"## Revue croisée {issue.Key}\n\n### Observations ({reviewer})\n\n{review.Output.Trim()}\n\n_Correctifs non appliqués (décision de Hajar)._";
            var pubR = await publisher.PublishAsync(issue.Key, new ActorRunResult(implementer, true, reviewOnly, null, 0, false), ct);
            PrintPublishResult(issue.Key, implementer, pubR);
            return;
        }
        if (!string.IsNullOrEmpty(answer)) directive = answer;
    }

    var corrPrompt = builder.BuildReviewCorrections(implementer, issue, review.Output, directive);
    var corrections = await RunDialogueTurnAsync(implementer, corrPrompt, artifactsDir, runner, ct);

    var body = new StringBuilder();
    body.AppendLine($"## Revue croisée {issue.Key}");
    body.AppendLine();
    body.AppendLine($"### Observations ({reviewer})");
    body.AppendLine();
    body.AppendLine(review.Output.Trim());
    if (corrections.Success)
    {
        await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-{implementer}-{issue.Key}-corrections.txt"), corrections.Output);
        EventEmitter.Emit("turn", new { group = $"RevueCroisee-{issue.Key}", speaker = implementer.ToString(), round = 2, isIntervention = false, content = corrections.Output.Trim() });
        body.AppendLine();
        body.AppendLine($"### Correctifs ({implementer})");
        body.AppendLine();
        body.AppendLine(corrections.Output.Trim());
    }
    else
    {
        body.AppendLine();
        body.AppendLine($"_Correctifs non appliqués (échec {implementer}) — à rejouer via --resume._");
    }

    var pub = await publisher.PublishAsync(issue.Key, new ActorRunResult(implementer, true, body.ToString(), null, 0, false), ct);
    PrintPublishResult(issue.Key, implementer, pub);
}

// Le handoff donne au moteur de relève l'état réel : travail partiel + consigne
// de vérifier le dépôt avant d'agir — il ne repart pas de zéro.
static ActorPrompt BuildHandoffPrompt(ActorPrompt basePrompt, ActorRole failedEngine, string partialOutput)
{
    var handoffSection =
        "\n\n## RELÈVE — contexte de handoff\n" +
        $"Le moteur précédent ({failedEngine}) a été interrompu par un épuisement de quota sur cette US. " +
        "Vérifie l'état réel du dépôt (git status / git diff) avant d'agir : une partie du travail est peut-être déjà commitée ou en cours. " +
        "Reprends là où il s'est arrêté — ne repars pas de zéro et ne défais pas son travail.\n" +
        (string.IsNullOrWhiteSpace(partialOutput)
            ? "Aucune sortie partielle disponible du moteur précédent."
            : $"### Sortie partielle du moteur précédent\n{partialOutput}");

    return basePrompt with { UserPrompt = basePrompt.UserPrompt + handoffSection };
}

// ─── Collective group : discussion multi-tours → 1 commentaire Jira (SERZENIA-143)
async Task RunDialogueGroupAsync(
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
    var transcriptBase = Path.Combine(artifactsDir, $"dialogue-{group}");

    // Reprise : le transcript persisté est rechargé, la discussion continue au tour suivant.
    var resumedTurns = resume ? await DialogueEngine.TryLoadTranscriptAsync(transcriptBase, ct) : null;
    if (resumedTurns is { Count: > 0 })
    {
        Console.WriteLine($"  Reprise discussion — {resumedTurns.Count} tour(s) déjà joué(s)");
        foreach (var t in resumedTurns)
            EventEmitter.Emit("turn", new { group = group.ToString(), speaker = t.Speaker, round = t.Round, isIntervention = t.IsIntervention, content = t.Content, resumed = true });
    }

    var engine = new DialogueEngine(config.MaxDialogueRounds, config.ApproverName, config.InterventionEveryTurn);

    // Le round/synthèse du tour courant est capturé par buildPrompt pour l'événement "turn"
    // (runTurn ne connaît pas le round ; les deux delegates sont appelés séquentiellement).
    int currentRound = 1;
    bool currentIsFinal = false;

    var outcome = await engine.RunAsync(
        participants: roles,
        buildPrompt: (role, transcript, round, isFinal) =>
        {
            currentRound = round;
            currentIsFinal = isFinal;
            return builder.BuildDialogueTurn(role, issues, publishKey, transcript, round,
                config.MaxDialogueRounds, isFinal, sessionMode, frameworks, agentMemory);
        },
        runTurn: async (role, prompt, token) =>
        {
            var result = await RunDialogueTurnAsync(role, prompt, artifactsDir, runner, token);
            if (result.Success)
                EventEmitter.Emit("turn", new { group = group.ToString(), speaker = role.ToString(), round = currentRound, isIntervention = false, isFinalSynthesis = currentIsFinal, content = result.Output.Trim() });
            return result;
        },
        requestIntervention: interactive ? round => RequestInterventionAsync(group, round) : null,
        transcriptBasePath: transcriptBase,
        resumedTurns: resumedTurns,
        // Garde de complétude : l'analyse ne peut pas conclure tant que chaque US
        // n'a pas sa section '## ANALYSE <KEY>' (tour de rattrapage sinon).
        validateConclusion: group == ActorGroup.Analysis
            ? text => AnalysisSections.ValidateCoverage(text, issues.Select(i => i.Key).ToList())
            : null,
        ct: ct);

    if (outcome.EndReason == DialogueEndReason.Stopped)
    {
        Console.WriteLine($"  ⚠ Discussion {group} interrompue — transcript sauvegardé, reprise via --resume");
        return;
    }
    if (outcome.EndReason == DialogueEndReason.ActorFailed)
    {
        Console.WriteLine($"  ✗ Discussion {group} : échec de {outcome.FailedRole} — --resume rejouera ce tour");
        return;
    }

    // ── Analyse : détection de litige + publication per-US (SERZENIA-143 lot 4) ──
    if (group == ActorGroup.Analysis && outcome.FinalContribution is { } analysisText)
    {
        if (AnalysisSections.HasLitige(analysisText))
        {
            state.LitigeDetected = true;
            await SprintStateManager.SaveAsync(stateFile, state);
            Console.WriteLine("  ⚠ LITIGE détecté dans la synthèse d'analyse — l'arbitrage sera proposé.");
            EventEmitter.Emit("litige", new { group = group.ToString(), detail = AnalysisSections.ExtractLitige(analysisText) });
        }

        // Un commentaire d'analyse par US concernée (couvre SERZENIA-139)
        var sections = AnalysisSections.Split(analysisText, issues.Select(i => i.Key).ToArray());
        foreach (var (usKey, section) in sections)
        {
            if (usKey == publishKey) continue; // la synthèse complète part déjà sur le ticket pilotage
            var perUsBody = $"## Analyse {usKey}\n\n{section}";
            await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-Analysis-{usKey}.txt"), perUsBody);
            var perUsResult = await publisher.PublishCollectiveAsync(usKey, group, perUsBody);
            Console.WriteLine($"  {(perUsResult.Status == PublishStatus.Posted ? "✓" : "~")} [{usKey}] {perUsResult.Status} (analyse per-US)");
            EventEmitter.Emit("publish", new { key = usKey, actor = "Analysis", status = perUsResult.Status.ToString() });
        }
        if (issues.Count > 1 && sections.Count == 0)
            Console.WriteLine("  ! Synthèse d'analyse sans sections '## ANALYSE <KEY>' — publication sur le ticket pilotage uniquement.");
    }

    var combined = BuildDialogueComment(group, outcome, config.MaxDialogueRounds, transcriptBase, config.ApproverName);
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

    if (pubResult.Status != PublishStatus.Failed)
    {
        state.CompletedGroups.Add(group.ToString());
        await SprintStateManager.SaveAsync(stateFile, state);
    }

    // ── Cadrage : extraction des US décidées par la discussion (SERZENIA-143 lot 3)
    // Le bloc structuré de la synthèse est parsé et sauvegardé — la création Jira
    // n'a lieu qu'après validation explicite (--create-us / bouton UI).
    if (sessionMode == SessionMode.Cadrage && group == ActorGroup.CommitteePilotage
        && outcome.FinalContribution is { } finalText)
    {
        var proposals = UsProposalParser.TryParse(finalText);
        if (proposals.Count > 0)
        {
            var proposalsFile = Path.Combine(artifactsDir, "us-proposals.json");
            await File.WriteAllTextAsync(proposalsFile, System.Text.Json.JsonSerializer.Serialize(
                proposals, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            Console.WriteLine($"\n  Le cadrage propose {proposals.Count} US :");
            foreach (var p in proposals)
                Console.WriteLine($"    • {p.Summary}");
            Console.WriteLine($"  Propositions sauvegardées : {proposalsFile}");
            Console.WriteLine($"  Pour créer après validation : sprint-launcher --create-us \"{proposalsFile}\" {publishKey} --write");

            EventEmitter.Emit("us-proposals", new
            {
                file = Path.GetFullPath(proposalsFile),
                refKey = publishKey,
                proposals = proposals.Select(p => new { p.Summary, p.Description, p.ReadyConditions }),
            });
        }
        else
        {
            Console.WriteLine("  ! Cadrage terminé sans bloc US structuré — aucune proposition extraite.");
        }
    }
}

// ─── Un tour de discussion : timer, artefacts, entrée rapport ─────────────────
async Task<ActorRunResult> RunDialogueTurnAsync(
    ActorRole role, ActorPrompt prompt, string artifactsDir, ActorRunner runner, CancellationToken ct)
{
    var promptFile = Path.Combine(artifactsDir, $"prompt-{role}.txt");
    await File.WriteAllTextAsync(promptFile, $"=== SYSTEM ===\n{prompt.SystemPrompt}\n\n=== USER ===\n{prompt.UserPrompt}");

    EventEmitter.Emit("actor-start", new { role = role.ToString() });
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

    // Dernier tour du rôle = fichier de sortie affiché par l'UI (écrasé à chaque tour).
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

    return runResult;
}

// ─── Checkpoint d'intervention entre rounds (mode --interactive) ──────────────
// « GO pour continuer » est le motif reconnu par l'UI pour afficher le panneau GO/ARRÊT.
Task<DialogueIntervention> RequestInterventionAsync(ActorGroup group, int round)
{
    Console.WriteLine();
    Console.WriteLine($"  ▶ Round {round - 1} terminé — discussion {group.GetGroupLabel()}");
    EventEmitter.Emit("checkpoint", new { kind = "round", group = group.ToString(), round });
    Console.Write("  GO pour continuer la discussion ? [Entrée=oui / n=arrêt / fin=conclure / texte=intervention] > ");
    var answer = Console.ReadLine()?.Trim();

    var intervention = answer?.ToLowerInvariant() switch
    {
        null or ""           => new DialogueIntervention(InterventionKind.Continue),
        "n" or "non" or "no" => new DialogueIntervention(InterventionKind.Stop),
        "fin" or "conclure"  => new DialogueIntervention(InterventionKind.Conclude),
        _                    => new DialogueIntervention(InterventionKind.Message, answer),
    };

    if (intervention.Kind == InterventionKind.Message)
    {
        Console.WriteLine($"  ⚑ Intervention de {config.ApproverName} injectée dans la discussion.");
        EventEmitter.Emit("turn", new { group = group.ToString(), speaker = config.ApproverName, round, isIntervention = true, content = intervention.Text });
    }
    if (intervention.Kind == InterventionKind.Stop)
        Console.WriteLine("  Arrêt demandé. Utilisez --resume pour reprendre la discussion.");

    return Task.FromResult(intervention);
}

// ─── Commentaire Jira de la discussion : synthèse + traçabilité ───────────────
static string BuildDialogueComment(ActorGroup group, DialogueOutcome outcome, int maxRounds, string transcriptBase, string approverName)
{
    var label = group switch
    {
        ActorGroup.CommitteePilotage  => "Comité de pilotage",
        ActorGroup.CommitteeArbitrage => "Comité d'arbitrage complet",
        ActorGroup.Qa                 => "Verdict QA",
        _ => group.ToString(),
    };

    var actorTurns = outcome.Turns.Where(t => !t.IsIntervention).ToList();
    var interventions = outcome.Turns.Count(t => t.IsIntervention);
    var rounds = actorTurns.Count == 0 ? 0 : actorTurns.Max(t => t.Round);

    var sb = new StringBuilder();
    sb.AppendLine($"## Délibération collective — {label}");
    sb.AppendLine();
    sb.Append($"Discussion multi-tours : {actorTurns.Count} prise(s) de parole sur {rounds} round(s) (max {maxRounds})");
    sb.AppendLine(interventions > 0 ? $", {interventions} intervention(s) de {approverName}." : ".");
    sb.AppendLine(outcome.EndReason == DialogueEndReason.Converged
        ? "Convergence explicite atteinte."
        : "Synthèse finale produite au plafond de tours (sans marqueur de convergence).");
    sb.AppendLine();
    sb.AppendLine($"Participants : {string.Join(", ", actorTurns.Select(t => t.Speaker).Distinct())}");
    sb.AppendLine();
    sb.AppendLine("### Décision finale");
    sb.AppendLine();
    sb.AppendLine(outcome.FinalContribution ?? "(aucune contribution)");
    sb.AppendLine();
    sb.AppendLine($"_Transcript complet de la discussion : `{Path.GetFileName(transcriptBase)}.md` (artefacts du run)._");

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
    EventEmitter.Emit("actor-done", new
    {
        role = label,
        success = result.Success,
        semiManual = result.IsSemiManual,
        seconds = elapsed,
        chars = result.Output.Length,
        exitCode = result.ExitCode,
        error = result.ErrorOutput?.Trim() is { Length: > 0 } e ? e[..Math.Min(200, e.Length)] : null,
    });
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
    EventEmitter.Emit("publish", new { key, actor = role.ToString(), status = result.Status.ToString() });
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
// test
