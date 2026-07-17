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
foreach (var flag in new[] { "--publish-manual", "--from-file", "--mode", "--sprint", "--roles", "--create-us", "--pilotage-us" })
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
// US portant la synthèse sprint-level (comité de pilotage, verdict QA) — déclarée, pas devinée.
var pilotageUsArg = GetArg(args, "--pilotage-us");

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

// ─── --smoke-live : valide le chat live contre les vrais binaires ─────────────
// Consomme un peu de quota. OBLIGATOIRE (vert) avant d'activer LIVE_CHAT en run réel.
if (args.Contains("--smoke-live"))
{
    var engineArg = args.SkipWhile(a => a != "--smoke-live").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "all";
    var smokeModels = SprintLauncherConfig.LoadModelsOnly(); // .env optionnel : modèles + repo
    using var smokeRunner = new ActorRunner(
        claudeModel: smokeModels.ClaudeModel,
        codexModel: smokeModels.CodexModel,
        actorTimeout: TimeSpan.FromMinutes(5),
        repoRoot: smokeModels.SerzeniaRepoRoot);
    return await LiveChatSmoke.RunAsync(engineArg, smokeRunner, shutdownCts.Token);
}

// ─── Usage guard — before config load so no .env required just to show help ───
if (issueKeys.Length == 0 && sprintArg is null && publishManualRole is null && createUsFile is null)
{
    Console.Error.WriteLine("Usage: sprint-launcher <ISSUE-KEY> [<ISSUE-KEY> ...] [--write] [--no-cache] [--resume] [--interactive]");
    Console.Error.WriteLine("       sprint-launcher --sprint <id> [--pilotage-us <CLE>] [options]");
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

// Registre des décisions déjà actées (scan des commentaires du sprint) : injecté
// dans tous les prompts — les acteurs ne redemandent plus une décision prise.
var decisionEntries = issues
    .SelectMany(i => i.Comments.Select(c => (Issue: i.Key, Comment: c)))
    .Where(x => x.Comment.Body.Contains("Décision de Hajar", StringComparison.OrdinalIgnoreCase)
             || x.Comment.Body.Contains("[decision:", StringComparison.OrdinalIgnoreCase)
             || x.Comment.Body.Contains($"Décision de {config.ApproverName}", StringComparison.OrdinalIgnoreCase))
    .Select(x =>
    {
        var body = x.Comment.Body.Trim();
        if (body.Length > 700) body = body[..700] + " (…)";
        return $"### [{x.Issue}] ({x.Comment.Created})\n{body}";
    })
    .ToList();
if (decisionEntries.Count > 0)
{
    builder.DecisionsRegistry = string.Join("\n\n", decisionEntries);
    Console.WriteLine($"  Registre des décisions : {decisionEntries.Count} décision(s) actée(s) injectée(s) dans tous les prompts.");
}
using var runner = new ActorRunner(
    claudeModel: config.ClaudeModel,
    codexModel: config.CodexModel,
    actorTimeout: TimeSpan.FromSeconds(config.ActorTimeoutSeconds),
    repoRoot: config.SerzeniaRepoRoot,
    implementationTimeout: TimeSpan.FromSeconds(config.ImplementationTimeoutSeconds),
    gptPilotageAuto: config.GptPilotageAuto);
var publisher = new JiraCommentPublisher(httpClient, config.JiraBaseUrl, config.JiraEmail, config.JiraApiToken, dryRun);

// Collect entries for the HTML report
var reportEntries = new List<ActorReportEntry>();

var sprintTag = sprintArg is not null ? $"sprint{sprintArg}" : "run";
var artifactsDir = Path.Combine("artifacts", sprintTag, string.Join("-", issueKeys));
Directory.CreateDirectory(artifactsDir);
Console.WriteLine($"Artefacts dans : {Path.GetFullPath(artifactsDir)}");
runner.LiveOutputDir = Path.GetFullPath(artifactsDir); // sorties acteurs visibles au fil de l'eau dans l'UI
// Chat live : n'active l'injection en cours de tour que si LIVE_CHAT=true (protocole
// streaming/app-server à valider). Sinon, mode one-shot inchangé (release stable).
if (config.LiveChatEnabled)
{
    runner.LiveInputDir = Path.GetFullPath(artifactsDir);
    Console.WriteLine("  ⚡ Chat live ACTIVÉ (LIVE_CHAT=true) — interventions injectées pendant le tour. Protocole expérimental : surveiller la 1re exécution.");
}

// Sorties périmées : les live-* d'un run précédent ne doivent jamais passer
// pour l'activité du run courant (constat Hajar : anciennes sorties affichées).
foreach (var stale in Directory.GetFiles(artifactsDir, "live-*.txt"))
    try { File.Delete(stale); } catch (IOException) { }

// Archivage des sorties d'AFFICHAGE du run précédent : la vue UI ne montre que
// le run courant (vision claire). Conservés pour la reprise : state, transcripts
// dialogue-*, collectifs, sorties per-US (revues en attente les relisent).
var displayFiles = Enum.GetValues<ActorRole>()
    .SelectMany(r => new[]
    {
        Path.Combine(artifactsDir, $"output-{r}.txt"),
        Path.Combine(artifactsDir, $"prompt-{r}.txt"),
    })
    .Where(File.Exists)
    .ToList();
if (displayFiles.Count > 0)
{
    var archiveDir = Path.Combine(artifactsDir, "archive", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(archiveDir);
    foreach (var f in displayFiles)
        try { File.Move(f, Path.Combine(archiveDir, Path.GetFileName(f)), overwrite: true); } catch (IOException) { }
    Console.WriteLine($"  Sorties du run précédent archivées ({displayFiles.Count} fichiers → archive/).");
}

// ─── US de pilotage : LUE DANS JIRA, pas devinée ─────────────────────────────
// La synthèse sprint-level (comité de pilotage, verdict QA) est publiée ICI et nulle
// part ailleurs. L'info est dans le titre du ticket (« Pilotage Sprint 6 ») — l'outil
// la lit au lieu de supposer que c'est le premier ticket de la liste.
var pilotageResolution = PilotageUsResolver.Resolve(issues, issueKeys, sprintArg, pilotageUsArg
    ?? Environment.GetEnvironmentVariable("PILOTAGE_US"));
var pilotageKey = pilotageResolution.Key;

if (pilotageResolution.IsFallback)
{
    Console.WriteLine($"⚠ US de pilotage NON identifiée — {pilotageResolution.Reason}.");
    Console.WriteLine($"  Repli sur le premier ticket du sprint : {pilotageKey}. Si ce n'est pas la bonne, la synthèse");
    Console.WriteLine($"  sprint-level partira au mauvais endroit (incident sprint 6 : délibération sur SERZENIA-98");
    Console.WriteLine($"  au lieu de SERZENIA-111). Nomme l'US « Pilotage Sprint <N> » ou passe --pilotage-us <CLE>.");
    EventEmitter.Emit("pilotage-us-fallback", new { key = pilotageKey, reason = pilotageResolution.Reason });
}
else
{
    Console.WriteLine($"  US de pilotage : {pilotageKey} — {pilotageResolution.Reason}. La synthèse sprint-level y sera publiée.");
    EventEmitter.Emit("pilotage-us", new { key = pilotageKey, reason = pilotageResolution.Reason });
}

// Pré-directives déposées AVANT le lancement (fichier au répertoire courant, écrit
// par l'UI) : STOCKÉES et injectées au registre — PAS publiées immédiatement sur Jira.
// Elles partiront sur Jira portées par le commentaire de l'acteur qui traite le sujet
// (seed dans sprintState.PendingDirectives une fois l'état initialisé).
var preDirectiveFile = Path.Combine(Directory.GetCurrentDirectory(), "pending-directive.txt");
(string Key, string Text)? preDirectivePending = null;
if (File.Exists(preDirectiveFile))
{
    var preText = (await File.ReadAllTextAsync(preDirectiveFile)).Trim();
    try { File.Delete(preDirectiveFile); } catch (IOException) { }
    if (preText.Length > 0)
    {
        var refKey = pilotageKey; // US de pilotage réelle (titre Jira), pas le 1er ticket de la liste
        Console.WriteLine($"  ⚑ Pré-directive(s) de {config.ApproverName} détectée(s) — stockée(s) et injectée(s) (publiée(s) quand un acteur commentera le sujet).");
        var preEntry = $"### [{refKey}] (pré-directive de ce run)\n{preText}";
        builder.DecisionsRegistry = builder.DecisionsRegistry is null ? preEntry : builder.DecisionsRegistry + "\n\n" + preEntry;
        preDirectivePending = (refKey, preText);
        EventEmitter.Emit("turn", new { group = "PreRun", speaker = config.ApproverName, round = 0, isIntervention = true, content = preText });
    }
}

// Registre des décisions consultable comme artefact (bouton Artefacts de l'UI)
if (builder.DecisionsRegistry is not null)
    await File.WriteAllTextAsync(Path.Combine(artifactsDir, "decisions-registry.md"),
        $"# Décisions actées par {config.ApproverName} — sprint {sprintTag}\n\n{builder.DecisionsRegistry}\n");

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

// (US de pilotage : détectée plus haut, cf. PilotageUsResolver)

SprintState sprintState;
if (resume)
{
    sprintState = await SprintStateManager.TryLoadAsync(stateFile) ?? new SprintState
    {
        StartedAt = DateTimeOffset.UtcNow,
        Keys = issueKeys,
        CompletedRoles = []
    };
    // L'épuisement de quota est temporel (fenêtres d'abonnement) : un nouveau
    // lancement repart avec les deux moteurs — la détection re-marquera si besoin.
    if (sprintState.QuotaExhaustedEngines.Count > 0)
    {
        Console.WriteLine($"  Quota : {string.Join(", ", sprintState.QuotaExhaustedEngines)} réactivé(s) pour ce run (épuisement précédent effacé).");
        sprintState.QuotaExhaustedEngines.Clear();
    }
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

// Seed de la pré-directive dans le stock de directives en attente (publiée quand
// un acteur commentera l'US de pilotage).
if (preDirectivePending is { } preDir)
    sprintState.PendingDirectives.Add(new PendingDirective
    {
        SubjectKey = preDir.Key, Text = preDir.Text,
        CreatedAt = DateTimeOffset.UtcNow, Published = false
    });

// ─── REDRESSEMENT (--resume d'un sprint déjà implémenté) : le PILOTAGE d'abord ──
// Retour de Hajar : « c'est comme si la QA faisait le rôle du pilotage ». Sur une
// reprise, ce n'est PAS la QA qui décide ce qui reste à traiter : le comité de
// pilotage ré-évalue l'état réel (verdict QA précédent, décisions actées, inputs
// désormais fournis — ex. DSN Sentry) dans une discussion NEUVE et DÉCIDE les
// actions par US ('## ECARTS'). Ces actions partent immédiatement en
// implémentation front/back EN PARALLÈLE, puis la QA re-vérifie l'ensemble.
var redressement = resume && sprintState.CompletedUsImplementations.Count > 0;
if (redressement)
{
    sprintState.CompletedGroups.Remove(ActorGroup.CommitteePilotage.ToString());
    sprintState.CompletedGroups.Remove(ActorGroup.Qa.ToString());
    foreach (var f in new[] { "dialogue-CommitteePilotage.json", "dialogue-CommitteePilotage.md", "dialogue-Qa.json", "dialogue-Qa.md" })
        try { File.Delete(Path.Combine(artifactsDir, f)); } catch (IOException) { }
    Console.WriteLine("  ↻ REDRESSEMENT : le comité de pilotage reprend la main (discussion neuve) — il décide ce qui reste à traiter AVANT la QA.");
}

// Step 3: Run actors group by group
ActorGroup? lastGroupPrinted = null;
bool implementationPhaseDone = false;
// Directive saisie par Hajar à un checkpoint de groupe — injectée avec autorité
// dans le groupe suivant (discussion ou acteurs simples), puis consommée.
string? pendingApproverDirective = null;
// Verrou de consommation du fichier pending-directive.txt (directives déposées
// en cours de run) — deux workers parallèles peuvent le lire en concurrence.
var pendingDirectiveLock = new SemaphoreSlim(1, 1);

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
            var arbAnswer = CleanLine(Console.ReadLine())?.ToLowerInvariant();
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

    // Directive de Hajar en attente : elle s'applique à CE groupe puis est consommée.
    var groupDirective = pendingApproverDirective;
    pendingApproverDirective = null;

    if (isCollectiveGroup)
    {
        // QA outillée : exécution réelle + audit des preuves injectés au verdict
        string? qaContext = group == ActorGroup.Qa
            ? await BuildQaContextAsync(shutdownCts.Token)
            : null;

        // Redressement : le comité reçoit sa mission (décider ce qui reste à traiter)
        // avec le verdict QA précédent — les décisions de Hajar sont déjà au registre.
        if (redressement && group == ActorGroup.CommitteePilotage)
        {
            var prevVerdictFile = Path.Combine(artifactsDir, "output-Qa-collective.txt");
            var prevVerdict = File.Exists(prevVerdictFile) ? await File.ReadAllTextAsync(prevVerdictFile) : null;
            var mission =
                "MISSION DE REDRESSEMENT : ce sprint a déjà été implémenté — vous reprenez la main pour DÉCIDER " +
                "ce qui reste à traiter (ce n'est pas le rôle de la QA). Appuyez-vous sur : le verdict QA précédent " +
                "ci-dessous, les décisions déjà actées au registre, et les inputs désormais fournis — un input externe " +
                "arrivé depuis (ex. DSN/secret dans l'environnement) LÈVE la stop-condition : la finalisation réelle et " +
                "sa preuve deviennent DUES. Couvrez TOUTES les US du sprint, front ET backend. " +
                "Votre synthèse DOIT contenir une section '## ECARTS' au format '- [CLE-US] action requise' " +
                "(une ligne par action ; 'AUCUN' si rien) : ces actions partiront immédiatement en implémentation " +
                "front/back en parallèle, la QA vérifiera ensuite." +
                (prevVerdict is not null ? $"\n\n## Verdict QA précédent (avant remédiation)\n{prevVerdict}" : "");
            groupDirective = groupDirective is null ? mission : $"{groupDirective}\n\n{mission}";
        }

        await RunDialogueGroupAsync(
            group, rolesInGroup, issues, pilotageKey,
            sprintState, stateFile, artifactsDir,
            builder, runner, publisher, groupDirective, qaContext, shutdownCts.Token);

        // Redressement : boucle DEV ↔ PILOTAGE. Les actions décidées par le comité
        // partent en implémentation (front/back en parallèle) + revue croisée, puis
        // le comité RÉ-ÉVALUE — autant d'allers-retours que nécessaire : c'est LUI
        // qui décide du passage à la QA (## ECARTS vide), pas la QA (retour Hajar).
        if (redressement && group == ActorGroup.CommitteePilotage && !shutdownCts.IsCancellationRequested)
        {
            var comFile = Path.Combine(artifactsDir, $"output-{ActorGroup.CommitteePilotage}-collective.txt");
            var pilotageCycle = 0;
            while (!shutdownCts.IsCancellationRequested)
            {
                var comEcarts = File.Exists(comFile)
                    ? EcartParser.Parse(await File.ReadAllTextAsync(comFile))
                    : new List<(string Key, string Description)>();

                if (comEcarts.Count == 0)
                {
                    Console.WriteLine("  ✔ Pilotage : plus d'action restante — GO pour la QA.");
                    break;
                }
                if (pilotageCycle >= config.MaxRemediationCycles)
                {
                    Console.WriteLine($"  ⚠ Plafond d'allers-retours dev↔pilotage atteint ({config.MaxRemediationCycles}) — actions restantes soumises à {config.ApproverName} ; la QA jugera l'état actuel.");
                    break;
                }

                pilotageCycle++;
                Console.WriteLine($"\n── REDRESSEMENT cycle {pilotageCycle} — {comEcarts.Count} action(s) décidée(s) par le pilotage → implémentation ──");
                foreach (var (k, d) in comEcarts) Console.WriteLine($"  - [{k}] {d}");
                EventEmitter.Emit("ecarts", new { count = comEcarts.Count, cycle = pilotageCycle, source = "pilotage" });

                await RemediateEcartsAsync(comEcarts, null, shutdownCts.Token);
                if (shutdownCts.IsCancellationRequested) break;

                // Revue croisée des remédiations — prérequis DoD (retour Hajar).
                await ProcessPendingReviewsAsync(issues, artifactsDir, sprintState, stateFile, builder, runner, publisher, shutdownCts.Token);

                // Le comité ré-évalue l'état réel après ce lot de dev (discussion neuve).
                sprintState.CompletedGroups.Remove(ActorGroup.CommitteePilotage.ToString());
                foreach (var f in new[] { "dialogue-CommitteePilotage.json", "dialogue-CommitteePilotage.md" })
                    try { File.Delete(Path.Combine(artifactsDir, f)); } catch (IOException) { }
                await SprintStateManager.SaveAsync(stateFile, sprintState);

                var reEval =
                    $"RÉ-ÉVALUATION DE REDRESSEMENT (cycle {pilotageCycle}) : les actions du cycle précédent viennent " +
                    "d'être implémentées et revues (voir commentaires Jira des US et l'état du dépôt — vérifie-les). " +
                    "DÉCIDEZ : soit de nouvelles actions restent dues ('## ECARTS' au format '- [CLE-US] action requise'), " +
                    "soit le sprint est prêt pour la QA ('## ECARTS' suivi de 'AUCUN'). C'est votre décision, pas celle de la QA.";
                Console.WriteLine($"\n── PILOTAGE — ré-évaluation post-dev (cycle {pilotageCycle}) ──");
                await RunDialogueGroupAsync(
                    ActorGroup.CommitteePilotage, rolesInGroup, issues, pilotageKey,
                    sprintState, stateFile, artifactsDir,
                    builder, runner, publisher, reEval, null, shutdownCts.Token);
            }
        }
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
                builder, runner, publisher, groupDirective, shutdownCts.Token);
        }
    }

    // ── Mode interactif : checkpoint entre groupes ─────────────────────────────
    // Sur --resume, les groupes déjà validés au run initial sont sautés : on NE
    // redemande PAS un « GO pour continuer » à chaque frontière (checkpoints
    // fantômes qui donnaient l'impression que l'outil était bloqué). On enchaîne
    // droit vers le vrai travail restant (remédiation). Le checkpoint de
    // remédiation, lui, reste interactif.
    if (interactive && !resume && !shutdownCts.IsCancellationRequested)
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
            Console.Write($"  GO pour continuer avec {nextGroup.GetGroupLabel()} ? [Entrée=oui / n=arrêt / texte=directive pour la suite] > ");
            var answer = CleanLine(Console.ReadLine());
            if (answer?.ToLowerInvariant() is "n" or "non" or "no")
            {
                Console.WriteLine("  Arrêt demandé. Utilisez --resume pour reprendre depuis ce point.");
                shutdownCts.Cancel();
            }
            else if (!string.IsNullOrEmpty(answer))
            {
                pendingApproverDirective = answer;
                Console.WriteLine($"  ⚑ Directive de {config.ApproverName} enregistrée — elle s'applique au groupe suivant.");
                EventEmitter.Emit("turn", new { group = nextGroup.ToString(), speaker = config.ApproverName, round = 0, isIntervention = true, content = answer });
                // Stockée seulement — Jira quand un acteur commentera le sujet (pilotage).
                await AddDirectiveAsync(pilotageKey, answer);
            }
        }
    }
}

// ─── BOUCLE DE REMÉDIATION (lot 8) : le sprint n'est PAS terminé tant que le ────
// verdict QA liste des écarts. L'outil les fait traiter INTÉGRALEMENT (code,
// tests, preuves) par le moteur du périmètre, puis relance une QA outillée —
// jusqu'à zéro écart, plafond de cycles, ou décision de Hajar.
while (!shutdownCts.IsCancellationRequested)
{
    var qaVerdictFile = Path.Combine(artifactsDir, "output-Qa-collective.txt");
    if (!File.Exists(qaVerdictFile)) break;

    var ecarts = EcartParser.Parse(await File.ReadAllTextAsync(qaVerdictFile));
    EventEmitter.Emit("ecarts", new { count = ecarts.Count, cycle = sprintState.RemediationCycles });

    if (ecarts.Count == 0)
    {
        Console.WriteLine($"\n✔ DoD : aucun écart au verdict QA — sprint clôturable (décision finale : {config.ApproverName}).");
        break;
    }

    Console.WriteLine($"\n── REMÉDIATION — {ecarts.Count} écart(s) détecté(s), cycle {sprintState.RemediationCycles + 1}/{config.MaxRemediationCycles} ──");
    foreach (var (k, d) in ecarts) Console.WriteLine($"  - [{k}] {d}");

    if (sprintState.RemediationCycles >= config.MaxRemediationCycles)
    {
        Console.WriteLine($"  ⚠ Plafond de cycles de remédiation atteint — écarts restants soumis à la décision de {config.ApproverName}.");
        break;
    }

    string? remediationDirective = null;
    if (interactive)
    {
        EventEmitter.Emit("checkpoint", new { kind = "group", group = "Remediation", next = $"REMÉDIATION cycle {sprintState.RemediationCycles + 1} ({ecarts.Count} écarts)" });
        Console.Write("  GO pour traiter intégralement ces écarts ? [Entrée=oui / n=arrêt / texte=directive] > ");
        var answer = CleanLine(Console.ReadLine());
        if (answer?.ToLowerInvariant() is "n" or "non" or "no") break;
        if (!string.IsNullOrEmpty(answer))
        {
            remediationDirective = answer;
            await AddDirectiveAsync(pilotageKey, answer);
        }
    }

    await RemediateEcartsAsync(ecarts, remediationDirective, shutdownCts.Token);
    if (shutdownCts.IsCancellationRequested) break;

    // Revue croisée des remédiations — prérequis DoD (retour Hajar).
    await ProcessPendingReviewsAsync(issues, artifactsDir, sprintState, stateFile, builder, runner, publisher, shutdownCts.Token);
    if (shutdownCts.IsCancellationRequested) break;

    // Re-vérification : QA outillée rejouée sur l'état remédié
    sprintState.RemediationCycles++;
    sprintState.CompletedGroups.Remove(ActorGroup.Qa.ToString());
    foreach (var f in new[] { Path.Combine(artifactsDir, "dialogue-Qa.json"), Path.Combine(artifactsDir, "dialogue-Qa.md") })
        try { File.Delete(f); } catch (IOException) { }
    await SprintStateManager.SaveAsync(stateFile, sprintState);

    Console.WriteLine("\n── QA (re-vérification post-remédiation) ──");
    var qaRoles = Enum.GetValues<ActorRole>().Where(r => r.GetGroup() == ActorGroup.Qa).ToArray();
    var freshQaContext = await BuildQaContextAsync(shutdownCts.Token);
    await RunDialogueGroupAsync(
        ActorGroup.Qa, qaRoles, issues, pilotageKey,
        sprintState, stateFile, artifactsDir,
        builder, runner, publisher, null, freshQaContext, shutdownCts.Token);
}

// ─── Directives jamais remises : une directive adressée à un acteur qui n'a plus
// pris la parole ne doit PAS disparaître en silence (incident 2026-07-16). Elle
// reste dans l'état persisté — le prochain run la livrera — et Hajar est prévenue ici.
await IngestPendingDirectivesAsync(artifactsDir, shutdownCts.Token);
var undelivered = sprintState.PendingDirectives.Where(d => !d.Delivered).ToList();
if (undelivered.Count > 0)
{
    Console.WriteLine($"\n⚑ {undelivered.Count} directive(s) de {config.ApproverName} NON REMISE(S) — destinataire jamais reparu dans ce run :");
    foreach (var d in undelivered)
        Console.WriteLine($"  - → {d.TargetActor ?? d.TargetGroup ?? "tous"} : {Truncate(d.Text, 100)}");
    Console.WriteLine("  Elles restent en attente et seront livrées au prochain run (état persisté).");
    EventEmitter.Emit("directives-undelivered", new
    {
        count = undelivered.Count,
        items = undelivered.Select(d => new { target = d.TargetActor ?? d.TargetGroup ?? "tous", text = d.Text }),
    });
}
await SprintStateManager.SaveAsync(stateFile, sprintState);

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
    string? approverDirective,
    CancellationToken ct)
{
    var prompt = builder.Build(role, issues, primaryKey, mode: sessionMode, frameworks: frameworks, memory: agentMemory);

    // Directives déposées à tout moment + celles adressées nommément à CE rôle.
    await IngestPendingDirectivesAsync(artifactsDir, ct);
    var directives = Join(approverDirective, DirectivesForActor(role));
    if (!string.IsNullOrWhiteSpace(directives))
        prompt = prompt with { UserPrompt = prompt.UserPrompt + $"\n\n## Directive de {config.ApproverName} — à respecter\n{directives}" };

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
            await FlushDirectivesForAsync(key, CancellationToken.None);
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

// ─── QA outillée : exécution réelle de QA_COMMAND + audit automatique des preuves
// DoD (SERZENIA-91) — le verdict QA s'appuie sur des faits, pas des déclarations.
async Task<string> BuildQaContextAsync(CancellationToken ct)
{
    Console.WriteLine($"  [QA outillée] exécution réelle : {config.QaCommand}");
    EventEmitter.Emit("qa-execution", new { command = config.QaCommand });
    var log = await QaExecutor.RunAsync(config.QaCommand, config.SerzeniaRepoRoot,
        TimeSpan.FromSeconds(config.ImplementationTimeoutSeconds), ct);
    var audit = ProofAuditor.Audit(config.SerzeniaRepoRoot, sprintTag, issueKeys);
    Console.WriteLine("  [QA outillée] smoke sur release réelle...");
    var smoke = await ReleaseSmoke.RunAsync(config.ReleaseCommand, config.AppExe, config.SerzeniaRepoRoot,
        sprintTag, config.FfmpegPath, TimeSpan.FromSeconds(config.ImplementationTimeoutSeconds), ct);
    // Éléments d'exécution fournis à la QA : elle déroule ELLE-MÊME les scénarios
    // sur la release réelle (demande Hajar) — chemins et outils explicites.
    var tools =
        "## Éléments d'exécution QA (tu as les droits — déroule les scénarios toi-même)\n" +
        $"- Dépôt applicatif : {config.SerzeniaRepoRoot}\n" +
        $"- Release réelle (déjà générée par la commande ci-dessus) : exécutable {config.AppExe}\n" +
        $"- Commande de release (pour régénérer si besoin) : {config.ReleaseCommand}\n" +
        $"- ffmpeg (vidéos de scénarios, gdigrab) : {config.FfmpegPath ?? "non configuré"}\n" +
        $"- Dépose tes preuves dans : artifacts/{sprintTag}/<US>/screenshots|videos|test-results/";
    var context = log + "\n\n" + audit + "\n\n" + smoke + "\n\n" + tools;
    await File.WriteAllTextAsync(Path.Combine(artifactsDir, "qa-execution.log"), context, CancellationToken.None);
    Console.WriteLine("  [QA outillée] logs + audit preuves écrits (qa-execution.log)");
    return context;
}

// ─── Attente de fenêtre de quota : quand les deux moteurs sont épuisés, l'outil
// reste ouvert et retente après un délai — les fenêtres d'abonnement se rouvrent
// d'elles-mêmes ; le run n'exige plus de relance manuelle (demande Hajar).
async Task<bool> WaitForQuotaWindowAsync(SprintState state, string stateFile, CancellationToken ct)
{
    if (!config.QuotaWaitEnabled || ct.IsCancellationRequested) return false;
    var minutes = config.QuotaWaitMinutes;
    Console.WriteLine($"  ⏳ Les deux moteurs sont à quota épuisé — nouvelle tentative dans {minutes} min (l'outil reste ouvert ; ARRET/Ctrl+C pour interrompre, reprise via --resume).");
    EventEmitter.Emit("quota-wait", new { minutes });
    try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct); }
    catch (TaskCanceledException) { return false; }
    if (ct.IsCancellationRequested) return false;
    state.QuotaExhaustedEngines.Clear();
    await SprintStateManager.SaveAsync(stateFile, state);
    Console.WriteLine("  ⏳ Fenêtre de quota retentée — moteurs réactivés.");
    EventEmitter.Emit("quota-retry", new { });
    return true;
}

// ─── Directives déposées par Hajar EN COURS DE RUN (fichier pending-directive.txt
// écrit par l'UI à tout moment) : versées au stock dès qu'on les voit. Le fichier est
// un SAS, pas un stockage : il est vidé ici et son contenu part dans l'état persisté
// (incident 2026-07-16 : run mort avant le point de lecture → directive jamais lue).
// Une ligne = une directive (l'UI en ajoute une par intervention).
async Task IngestPendingDirectivesAsync(string artifactsDir, CancellationToken ct)
{
    var file = Path.Combine(artifactsDir, "pending-directive.txt");
    await pendingDirectiveLock.WaitAsync(CancellationToken.None);
    try
    {
        if (!File.Exists(file)) return;
        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(file, ct);
            File.Delete(file);
        }
        catch (IOException) { return; } // en cours d'écriture par l'UI — prochain passage

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await AddDirectiveAsync(pilotageKey, line);
    }
    finally { pendingDirectiveLock.Release(); }
}

// Directives dues à CET acteur : adressées à lui (@ActorRole), à son groupe
// (@qa, @pilotage…), ou non adressées (= tout le monde). Livrées une fois, puis
// marquées — le stock survit à un crash tant qu'elles n'ont pas trouvé leur cible.
string? DirectivesForActor(ActorRole role)
{
    var due = sprintState.PendingDirectives.Where(d => !d.Delivered && DirectiveTargets(d, role)).ToList();
    if (due.Count == 0) return null;

    foreach (var d in due)
    {
        d.Delivered = true;
        Console.WriteLine($"  ⚑ Directive de {config.ApproverName} → {role} : {Truncate(d.Text, 80)}");
        EventEmitter.Emit("directive-delivered", new { target = role.ToString(), text = d.Text });
    }
    try { SprintStateManager.SaveAsync(stateFile, sprintState).GetAwaiter().GetResult(); } catch (IOException) { }

    return string.Join("\n\n", due.Select(d =>
        d.TargetActor is not null || d.TargetGroup is not null
            ? $"(adressée à {d.TargetActor ?? d.TargetGroup} — c'est toi) {d.Text}"
            : d.Text));
}

static bool DirectiveTargets(PendingDirective d, ActorRole role)
{
    if (d.TargetActor is { Length: > 0 } a)
        return Enum.TryParse<ActorRole>(a, ignoreCase: true, out var r) && r == role;
    if (d.TargetGroup is { Length: > 0 } g)
        return Enum.TryParse<ActorGroup>(g, ignoreCase: true, out var gr) && role.GetGroup() == gr;
    return true; // non adressée : pour le prochain acteur qui parle
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..max] + "…";

// Concatène des blocs de directives non vides (directive de groupe + directives adressées).
static string? Join(params string?[] parts)
{
    var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
    return kept.Length == 0 ? null : string.Join("\n\n", kept);
}

// ─── Registre des décisions : toute décision donnée EN COURS DE RUN y entre ────
// immédiatement (mémoire + fichier decisions-registry.md). Les prompts de TOUS
// les acteurs suivants du même run la reçoivent — garantie qu'une décision déjà
// donnée n'est jamais redemandée, sans attendre le rescan Jira du prochain run.
async Task RecordDecisionInRegistryAsync(string usKey, string text)
{
    var entry = $"### [{usKey}] (donnée en cours de run, {DateTime.Now:dd/MM HH:mm})\n{text}";
    builder.DecisionsRegistry = builder.DecisionsRegistry is null ? entry : builder.DecisionsRegistry + "\n\n" + entry;
    try
    {
        await File.WriteAllTextAsync(Path.Combine(artifactsDir, "decisions-registry.md"),
            $"# Décisions actées par {config.ApproverName} — sprint {sprintTag}\n\n{builder.DecisionsRegistry}\n");
    }
    catch (IOException) { } // fichier momentanément verrouillé — la mémoire reste à jour
}

// ─── Directives de Hajar : STOCKER puis PUBLIER quand l'acteur commente le sujet ──
// Choix de Hajar (2026-07) : le champ d'intervention ne publie PAS directement sur
// Jira. La directive est stockée (registre injecté aux acteurs + onglet DÉCISIONS) et
// n'arrive sur Jira que portée par le commentaire de l'acteur qui traite l'US liée.
async Task AddDirectiveAsync(string subjectKey, string text)
{
    // « @GptImplementation tu as indiqué … pourquoi ? » → cible + texte utile.
    var address = DirectiveAddressing.Parse(text);
    var clean = address.Text.Trim();
    if (clean.Length == 0) return;

    sprintState.PendingDirectives.Add(new PendingDirective
    {
        SubjectKey = subjectKey, Text = clean,
        CreatedAt = DateTimeOffset.UtcNow, Published = false,
        TargetActor = address.Actor?.ToString(),
        TargetGroup = address.Group?.ToString(),
        Delivered = false,
    });

    var registryEntry = address.IsTargeted ? $"→ {address.TargetLabel} : {clean}" : clean;
    await RecordDecisionInRegistryAsync(subjectKey, registryEntry); // registre + fichier (tab + prompts)
    try { await SprintStateManager.SaveAsync(stateFile, sprintState); } catch (IOException) { }

    Console.WriteLine(address.IsTargeted
        ? $"  ⚑ Directive de {config.ApproverName} stockée pour [{subjectKey}] → destinataire : {address.TargetLabel} (appliquée dès qu'il prend la parole)."
        : $"  ⚑ Directive de {config.ApproverName} stockée pour [{subjectKey}] — appliquée au prochain acteur, publiée dès qu'un acteur commentera ce sujet.");
    EventEmitter.Emit("directive-stored", new { subject = subjectKey, target = address.TargetLabel, text = clean });
}

// ─── Dispatch de remédiation : traite une liste d'écarts/actions par US ─────────
// Utilisé par le pilotage de redressement (actions décidées par le comité) ET par
// la boucle QA (écarts du verdict). Répartition front→codex / back→ccode, puis les
// DEUX moteurs travaillent EN PARALLÈLE, chacun séquentiel sur ses US (retour de
// Hajar : ccode doit avancer sur le backend pendant que codex traite le front).
async Task RemediateEcartsAsync(
    IReadOnlyList<(string Key, string Description)> ecartList, string? directive, CancellationToken ct)
{
    var remLock = new SemaphoreSlim(1, 1);
    var perEngine = new Dictionary<ActorRole, List<(SprintLauncher.Jira.JiraIssue Issue, List<string> Descs)>>();

    foreach (var byUs in ecartList.GroupBy(e => e.Key))
    {
        var targetKey = byUs.Key is "GLOBAL" or "TRANSVERSE" ? pilotageKey : byUs.Key;
        var issue = issues.FirstOrDefault(i => i.Key == targetKey) ?? issues[0];
        var usType = UsTypeClassifier.Classify(issue.Summary, issue.Description);
        var engineName = ImplementationRotation.PickEngineForUs(
            usType, sprintState.LastImplementer, sprintState.QuotaExhaustedEngines,
            config.EngineFront, config.EngineBack);
        if (engineName is null && await WaitForQuotaWindowAsync(sprintState, stateFile, ct))
            engineName = ImplementationRotation.PickEngineForUs(
                usType, sprintState.LastImplementer, sprintState.QuotaExhaustedEngines,
                config.EngineFront, config.EngineBack);
        if (engineName is null)
        {
            Console.WriteLine($"  ⚠ [{issue.Key}] aucun moteur disponible (quota) — au prochain cycle.");
            continue;
        }
        var engine = Enum.Parse<ActorRole>(engineName);
        if (!perEngine.TryGetValue(engine, out var list)) perEngine[engine] = list = [];
        list.Add((issue, byUs.Select(e => e.Description).ToList()));
    }

    if (perEngine.Count > 1)
        Console.WriteLine($"  ⇉ Remédiation en parallèle : {string.Join(" + ", perEngine.Select(kv => $"{kv.Key} ({kv.Value.Count} US)"))}");

    await Task.WhenAll(perEngine.Select(kv => Task.Run(async () =>
    {
        var (engine, workItems) = (kv.Key, kv.Value);
        foreach (var (issue, descs) in workItems)
        {
            if (ct.IsCancellationRequested) break;
            Console.WriteLine($"  ▶ Remédiation {issue.Key} → {engine}");
            EventEmitter.Emit("implementation-us", new { key = issue.Key, engine = engine.ToString(), relief = false, remediation = true });

            var remPrompt = builder.BuildRemediation(engine, issue, descs, directive);
            var remResult = await RunDialogueTurnAsync(engine, remPrompt, artifactsDir, runner, ct);

            if (remResult.IsQuotaExhausted)
            {
                await remLock.WaitAsync(CancellationToken.None);
                try
                {
                    sprintState.QuotaExhaustedEngines.Add(engine.ToString());
                    await SprintStateManager.SaveAsync(stateFile, sprintState);
                }
                finally { remLock.Release(); }
                EventEmitter.Emit("quota", new { engine = engine.ToString(), key = issue.Key });
                Console.WriteLine($"  ⚠ Quota {engine} épuisé — ses US restantes passeront au prochain cycle.");
                break;
            }
            if (remResult.Success && !ImplementationOutputGuard.IsAwaitingGo(remResult.Output))
            {
                // Une sortie vide/vague n'est pas publiable — c'est un échec de l'US,
                // pas une raison de tuer tout le run (constaté en test stub).
                try
                {
                    var remPub = await publisher.PublishAsync(issue.Key, remResult, ct);
                    PrintPublishResult(issue.Key, engine, remPub);
                }
                catch (VagueCommentException ex)
                {
                    Console.WriteLine($"  ✗ [{issue.Key}] {engine} : sortie vide/vague — non publiée ({ex.Message}). Au prochain cycle.");
                    continue;
                }
                await remLock.WaitAsync(CancellationToken.None);
                try
                {
                    await FlushDirectivesForAsync(issue.Key, ct);
                    // Revue croisée due sur la remédiation — prérequis DoD (retour Hajar).
                    if (config.CrossReviewEnabled && !sprintState.PendingReviews.Any(p => p.Key == issue.Key && p.Implementer == engine.ToString()))
                        sprintState.PendingReviews.Add(new PendingReview(issue.Key, engine.ToString(), null));
                    sprintState.LastImplementer = engine.ToString();
                    await SprintStateManager.SaveAsync(stateFile, sprintState);
                }
                finally { remLock.Release(); }
            }
        }
    })));
}

// Publie sur Jira les directives en attente liées à un sujet, au moment où un acteur
// vient d'y publier son commentaire. Marque Published uniquement si l'écriture a
// abouti (Posted) ou est déjà présente (Skipped) ; laisse en attente en dry-run/échec.
async Task FlushDirectivesForAsync(string subjectKey, CancellationToken ct)
{
    var due = sprintState.PendingDirectives.Where(d => !d.Published && d.SubjectKey == subjectKey).ToList();
    if (due.Count == 0) return;
    var changed = false;
    foreach (var d in due)
    {
        var pub = await publisher.PublishDecisionAsync(subjectKey, config.ApproverName, d.Text, ct);
        Console.WriteLine($"  {(pub.Status == PublishStatus.Posted ? "✓" : "~")} [{subjectKey}] directive de {config.ApproverName} publiée (portée par le commentaire acteur) — {pub.Status}");
        EventEmitter.Emit("publish", new { key = subjectKey, actor = "DirectiveHajar", status = pub.Status.ToString() });
        if (pub.Status is PublishStatus.Posted or PublishStatus.Skipped) { d.Published = true; changed = true; }
    }
    if (changed) { try { await SprintStateManager.SaveAsync(stateFile, sprintState); } catch (IOException) { } }
}

static ActorPrompt WithDirective(ActorPrompt prompt, string? directive, string approverName) =>
    string.IsNullOrWhiteSpace(directive)
        ? prompt
        : prompt with { UserPrompt = prompt.UserPrompt + $"\n\n## Directive de {approverName} — à respecter\n{directive}" };

// Réponse d'un checkpoint lue sur stdin : on retire le BOM (U+FEFF) qu'un
// producteur stdin pourrait préfixer — sinon un « GO » (ligne vide) deviendrait
// « ﻿ », non vide, donc interprété comme une directive au lieu d'un GO.
static string? CleanLine(string? s) => s?.Trim().Trim('﻿').Trim();

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
    // ── Mode parallèle : les deux moteurs travaillent en même temps, chacun sur
    // sa file front/back (périmètres disjoints = pas de chevauchement de code).
    if (config.ParallelImplementation)
    {
        await RunImplementationParallelAsync(issues, artifactsDir, state, stateFile, builder, runner, publisher, ct);
        return;
    }

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
            // Attente automatique de la réouverture de fenêtre plutôt qu'un abandon
            if (await WaitForQuotaWindowAsync(state, stateFile, ct))
                engineName = ImplementationRotation.PickEngineForUs(
                    usType, state.LastImplementer, state.QuotaExhaustedEngines,
                    config.EngineFront, config.EngineBack);
            if (engineName is null)
            {
                Console.WriteLine("  ⚠ US restantes en attente — reprise via --resume.");
                break;
            }
        }

        var engine = Enum.Parse<ActorRole>(engineName);
        ActorRole? reliefFrom = null; // renseigné si l'US est reprise par l'autre moteur en cours de route
        var typeLabel = usType switch { UsType.Front => " [front]", UsType.Backend => " [backend]", _ => "" };
        Console.WriteLine($"  ▶ {issue.Key}{typeLabel} → {engine}");
        EventEmitter.Emit("implementation-us", new { key = issue.Key, engine = engine.ToString(), relief = false, usType = usType.ToString() });

        var prompt = builder.Build(engine, [issue], issue.Key, mode: sessionMode, frameworks: frameworks, memory: agentMemory);
        await IngestPendingDirectivesAsync(artifactsDir, ct);
        prompt = WithDirective(prompt, DirectivesForActor(engine), config.ApproverName);
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
            reliefFrom = engine; // la revue devra couvrir les DEUX contributions
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
            await FlushDirectivesForAsync(issue.Key, ct);

            // Revue croisée due — PERSISTÉE (survit aux interruptions), exécutée en fin de phase
            if (config.CrossReviewEnabled)
                state.PendingReviews.Add(new PendingReview(issue.Key, engine.ToString(), reliefFrom?.ToString()));

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

    if (config.CrossReviewEnabled)
        await ProcessPendingReviewsAsync(issues, artifactsDir, state, stateFile, builder, runner, publisher, ct);
}

// ─── Revues croisées en attente (persistées dans le state — lot 8) ─────────────
async Task ProcessPendingReviewsAsync(
    IReadOnlyList<SprintLauncher.Jira.JiraIssue> issues,
    string artifactsDir,
    SprintState state,
    string stateFile,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct)
{
    if (state.PendingReviews.Count == 0) return;
    Console.WriteLine($"  [revues croisées : {state.PendingReviews.Count} en attente]");

    foreach (var pending in state.PendingReviews.ToList())
    {
        if (ct.IsCancellationRequested) break;

        var issue = issues.FirstOrDefault(i => i.Key == pending.Key);
        if (issue is null)
        {
            state.PendingReviews.Remove(pending);
            continue;
        }

        var implementer = Enum.Parse<ActorRole>(pending.Implementer);
        var outputFile = Path.Combine(artifactsDir, $"output-{pending.Implementer}-{pending.Key}.txt");
        var implOutput = File.Exists(outputFile)
            ? await File.ReadAllTextAsync(outputFile, ct)
            : "(sortie d'implémentation indisponible — revue basée sur l'état réel du dépôt : git log/diff)";
        ActorRole? reliefFrom = pending.ReliefFrom is null ? null : Enum.Parse<ActorRole>(pending.ReliefFrom);

        var done = await RunCrossReviewAsync(issue, implementer, implOutput, artifactsDir, state, stateFile, builder, runner, publisher, ct, reliefFrom);
        if (done && !ct.IsCancellationRequested)
        {
            state.PendingReviews.Remove(pending);
            await SprintStateManager.SaveAsync(stateFile, state);
        }
    }
}

// ─── Implémentation PARALLÈLE : moteur front et moteur back en même temps ──────
// Chaque moteur déroule SA file d'US (périmètres disjoints par classification).
// L'état partagé (state.json, rapport) est protégé par un verrou ; les revues
// croisées sont exécutées en fin de phase, quand les deux moteurs sont libres.
async Task RunImplementationParallelAsync(
    IReadOnlyList<SprintLauncher.Jira.JiraIssue> issues,
    string artifactsDir,
    SprintState state,
    string stateFile,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct)
{
    Console.WriteLine("  [implémentation PARALLÈLE — front et backend en simultané, revues croisées en fin de phase]");

    var frontQueue = new Queue<SprintLauncher.Jira.JiraIssue>();
    var backQueue = new Queue<SprintLauncher.Jira.JiraIssue>();
    bool toggle = false;
    foreach (var issue in issues)
    {
        if (state.CompletedUsImplementations.Contains(issue.Key))
        {
            Console.WriteLine($"  ○ {issue.Key,-28} [déjà implémentée — ignorée]");
            continue;
        }
        var type = UsTypeClassifier.Classify(issue.Summary, issue.Description);
        var target = type switch
        {
            UsType.Front   => frontQueue,
            UsType.Backend => backQueue,
            _              => (toggle = !toggle) ? backQueue : frontQueue, // non typées réparties
        };
        target.Enqueue(issue);
        Console.WriteLine($"  · {issue.Key} [{(target == frontQueue ? "front" : "backend")}] → file {(target == frontQueue ? config.EngineFront : config.EngineBack)}");
    }

    var stateLock = new SemaphoreSlim(1, 1);

    async Task WorkerAsync(Queue<SprintLauncher.Jira.JiraIssue> queue, ActorRole engine)
    {
        foreach (var issue in queue)
        {
            if (ct.IsCancellationRequested) return;
            if (state.QuotaExhaustedEngines.Contains(engine.ToString()))
            {
                // Jamais de retrait silencieux : Hajar doit voir pourquoi un moteur ne travaille pas.
                Console.WriteLine($"  ⚠ Worker {engine} retiré (quota épuisé) — sa file sera reprise par l'autre moteur en fin de phase.");
                EventEmitter.Emit("quota", new { engine = engine.ToString(), key = issue.Key });
                return;
            }

            Console.WriteLine($"  ▶ {issue.Key} → {engine} (parallèle)");
            EventEmitter.Emit("implementation-us", new { key = issue.Key, engine = engine.ToString(), relief = false, parallel = true });

            var prompt = builder.Build(engine, [issue], issue.Key, mode: sessionMode, frameworks: frameworks, memory: agentMemory);
            await IngestPendingDirectivesAsync(artifactsDir, ct);
            prompt = WithDirective(prompt, DirectivesForActor(engine), config.ApproverName);
            var result = await RunDialogueTurnAsync(engine, prompt, artifactsDir, runner, ct);

            if (result.IsQuotaExhausted)
            {
                await stateLock.WaitAsync(CancellationToken.None);
                try { state.QuotaExhaustedEngines.Add(engine.ToString()); await SprintStateManager.SaveAsync(stateFile, state); }
                finally { stateLock.Release(); }
                Console.WriteLine($"  ⚠ {engine} à quota épuisé — sa file sera reprise en séquentiel par l'autre moteur.");
                EventEmitter.Emit("quota", new { engine = engine.ToString(), key = issue.Key });
                return;
            }

            if (result.Success && ImplementationOutputGuard.IsAwaitingGo(result.Output))
            {
                Console.WriteLine($"  ! {issue.Key} — {engine} s'est arrêté à une demande de GO : US NON implémentée.");
                EventEmitter.Emit("implementation-blocked", new { key = issue.Key, engine = engine.ToString() });
                continue;
            }

            if (!result.Success)
            {
                if (ct.IsCancellationRequested) return;
                Console.WriteLine($"  ✗ {issue.Key} — échec {engine} (non-quota). US suivante ; --resume rejouera celle-ci.");
                continue;
            }

            await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-{engine}-{issue.Key}.txt"), result.Output);
            var pub = await publisher.PublishAsync(issue.Key, result, ct);
            PrintPublishResult(issue.Key, engine, pub);
            await FlushDirectivesForAsync(issue.Key, ct);

            await stateLock.WaitAsync(CancellationToken.None);
            try
            {
                state.CompletedUsImplementations.Add(issue.Key);
                state.LastImplementer = engine.ToString();
                if (config.CrossReviewEnabled)
                    state.PendingReviews.Add(new PendingReview(issue.Key, engine.ToString(), null));
                await SprintStateManager.SaveAsync(stateFile, state);
            }
            finally { stateLock.Release(); }
        }
    }

    var engineFront = Enum.Parse<ActorRole>(config.EngineFront);
    var engineBack = Enum.Parse<ActorRole>(config.EngineBack);
    await Task.WhenAll(WorkerAsync(frontQueue, engineFront), WorkerAsync(backQueue, engineBack));

    // File(s) restante(s) après épuisement d'un moteur → reprise séquentielle avec relève
    var leftovers = frontQueue.Concat(backQueue)
        .Where(i => !state.CompletedUsImplementations.Contains(i.Key))
        .ToList();
    foreach (var issue in leftovers)
    {
        if (ct.IsCancellationRequested) break;
        var reliefName = ImplementationRotation.PickEngine(state.LastImplementer, state.QuotaExhaustedEngines);
        if (reliefName is null)
        {
            if (await WaitForQuotaWindowAsync(state, stateFile, ct))
                reliefName = ImplementationRotation.PickEngine(state.LastImplementer, state.QuotaExhaustedEngines);
            if (reliefName is null)
            {
                Console.WriteLine("  ⚠ US restantes en attente — reprise via --resume.");
                break;
            }
        }
        var relief = Enum.Parse<ActorRole>(reliefName);
        Console.WriteLine($"  ▶ {issue.Key} → {relief} (reprise de file)");
        EventEmitter.Emit("implementation-us", new { key = issue.Key, engine = relief.ToString(), relief = true });
        var prompt = builder.Build(relief, [issue], issue.Key, mode: sessionMode, frameworks: frameworks, memory: agentMemory);
        var result = await RunDialogueTurnAsync(relief, prompt, artifactsDir, runner, ct);
        if (result.Success && !ImplementationOutputGuard.IsAwaitingGo(result.Output))
        {
            await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-{relief}-{issue.Key}.txt"), result.Output);
            var pub = await publisher.PublishAsync(issue.Key, result, ct);
            PrintPublishResult(issue.Key, relief, pub);
            await FlushDirectivesForAsync(issue.Key, ct);
            state.CompletedUsImplementations.Add(issue.Key);
            state.LastImplementer = relief.ToString();
            if (config.CrossReviewEnabled)
                state.PendingReviews.Add(new PendingReview(issue.Key, relief.ToString(), null));
            await SprintStateManager.SaveAsync(stateFile, state);
        }
        else if (result.IsQuotaExhausted)
        {
            state.QuotaExhaustedEngines.Add(relief.ToString());
            await SprintStateManager.SaveAsync(stateFile, state);
        }
    }

    // ── Phase revues croisées (les deux moteurs sont libres) — persistées ──────
    if (config.CrossReviewEnabled)
        await ProcessPendingReviewsAsync(issues, artifactsDir, state, stateFile, builder, runner, publisher, ct);
}

// ─── Revue croisée post-dev (lot 7) : observations par l'autre moteur, ─────────
// correctifs chez l'implémenteur, intervention de Hajar possible entre les deux.
// Retourne false si la revue n'a pas pu être faite (réviseur indisponible/échec) —
// elle reste alors en attente persistée pour un prochain passage.
async Task<bool> RunCrossReviewAsync(
    SprintLauncher.Jira.JiraIssue issue,
    ActorRole implementer,
    string implementationOutput,
    string artifactsDir,
    SprintState state,
    string stateFile,
    PromptBuilder builder,
    ActorRunner runner,
    JiraCommentPublisher publisher,
    CancellationToken ct,
    ActorRole? reliefFrom = null)
{
    var reviewerName = ImplementationRotation.PickRelief(implementer.ToString(), state.QuotaExhaustedEngines);
    if (reviewerName is null)
    {
        Console.WriteLine($"  ○ {issue.Key} — revue croisée reportée (réviseur à quota épuisé) — reste en attente.");
        return false;
    }

    var reviewer = Enum.Parse<ActorRole>(reviewerName);
    Console.WriteLine($"  ⟲ {issue.Key} — revue croisée : {reviewer} relit {implementer}");

    var reviewPrompt = builder.BuildCrossReview(reviewer, issue, implementer, implementationOutput, reliefFrom);
    var review = await RunDialogueTurnAsync(reviewer, reviewPrompt, artifactsDir, runner, ct);
    if (!review.Success)
    {
        if (review.IsQuotaExhausted)
        {
            state.QuotaExhaustedEngines.Add(reviewer.ToString());
            await SprintStateManager.SaveAsync(stateFile, state);
        }
        Console.WriteLine($"  ○ {issue.Key} — revue croisée reportée (échec {reviewer}), l'implémentation reste valide.");
        return false;
    }

    await File.WriteAllTextAsync(Path.Combine(artifactsDir, $"output-Review-{issue.Key}.txt"), review.Output);
    EventEmitter.Emit("turn", new { group = $"RevueCroisee-{issue.Key}", speaker = reviewer.ToString(), round = 1, isIntervention = false, content = review.Output.Trim() });

    // Checkpoint : Hajar peut orienter les correctifs (ou les sauter)
    string? directive = null;
    if (interactive)
    {
        EventEmitter.Emit("checkpoint", new { kind = "review", group = $"RevueCroisee-{issue.Key}", round = 1 });
        Console.Write($"  Revue {issue.Key} reçue — [Entrée=appliquer les correctifs / texte=directive / n=passer] > ");
        var answer = CleanLine(Console.ReadLine());
        if (answer?.ToLowerInvariant() is "n" or "non" or "no")
        {
            Console.WriteLine("  Correctifs sautés sur décision de Hajar — revue publiée telle quelle.");
            var reviewOnly = $"## Revue croisée {issue.Key}\n\n### Observations ({reviewer})\n\n{review.Output.Trim()}\n\n_Correctifs non appliqués (décision de Hajar)._";
            var pubR = await publisher.PublishAsync(issue.Key, new ActorRunResult(implementer, true, reviewOnly, null, 0, false), ct);
            PrintPublishResult(issue.Key, implementer, pubR);
            await FlushDirectivesForAsync(issue.Key, ct);
            return true;
        }
        if (!string.IsNullOrEmpty(answer))
        {
            directive = answer;
            // Stockée + injectée ; publiée sur Jira portée par le commentaire de
            // correction de l'acteur sur cette US (FlushDirectivesForAsync).
            await AddDirectiveAsync(issue.Key, answer);
        }
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
    // La directive donnée au checkpoint de revue part sur Jira portée par ce commentaire.
    await FlushDirectivesForAsync(issue.Key, ct);
    return true;
}

// Le handoff donne au moteur de relève l'état réel : travail partiel + consigne
// de vérifier le dépôt avant d'agir — il ne repart pas de zéro.
static ActorPrompt BuildHandoffPrompt(ActorPrompt basePrompt, ActorRole failedEngine, string partialOutput)
{
    var handoffSection =
        "\n\n## RELÈVE — contexte de handoff\n" +
        $"Le moteur précédent ({failedEngine}) a été interrompu sur cette US. " +
        "AVANT TOUTE ÉCRITURE : fais l'inventaire complet de son travail — `git status`, `git diff`, " +
        "`git log -5` — et liste ce qui est déjà fait. INTERDICTIONS STRICTES (chevauchement constaté au sprint 6) : " +
        "ne réimplémente PAS ce qui existe déjà, ne crée pas de doublon de fichier/classe/service, " +
        "ne supprime ni n'écrase son travail. COMPLÈTE uniquement ce qui manque, en réutilisant ses structures. " +
        "Si son travail partiel est incohérent, répare a minima en le signalant explicitement dans ta sortie.\n" +
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
    string? approverDirective,
    string? qaContext,
    CancellationToken ct)
{
    var transcriptBase = Path.Combine(artifactsDir, $"dialogue-{group}");

    // Directives déposées à tout moment : versées au stock à l'entrée du groupe, puis
    // relues et livrées AU TOUR DE CHAQUE ACTEUR (cf. buildPrompt) — une directive
    // adressée à un acteur précis doit atteindre CET acteur, pas le premier venu.
    await IngestPendingDirectivesAsync(artifactsDir, ct);

    // Reprise : le transcript persisté est rechargé, la discussion continue au tour suivant.
    var resumedTurns = resume ? await DialogueEngine.TryLoadTranscriptAsync(transcriptBase, ct) : null;
    if (resumedTurns is { Count: > 0 })
    {
        Console.WriteLine($"  Reprise discussion — {resumedTurns.Count} tour(s) déjà joué(s)");
        foreach (var t in resumedTurns)
            EventEmitter.Emit("turn", new { group = group.ToString(), speaker = t.Speaker, round = t.Round, isIntervention = t.IsIntervention, content = t.Content, resumed = true });
    }

    // Directive du checkpoint précédent : injectée en tête de discussion avec autorité.
    if (!string.IsNullOrWhiteSpace(approverDirective))
    {
        var directiveTurn = new DialogueTurn(config.ApproverName, approverDirective, DateTimeOffset.UtcNow, 1, IsIntervention: true);
        resumedTurns = (resumedTurns ?? []).Append(directiveTurn).ToList();
    }

    var engine = new DialogueEngine(config.MaxDialogueRounds, config.ApproverName, config.InterventionEveryTurn,
        blindFirstRound: config.BlindFirstRound);
    if (config.BlindFirstRound)
        Console.WriteLine("  (round 1 à l'aveugle : chaque membre produit son analyse propre avant de lire les autres)");

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
            // Round 1 en mode aveugle : le moteur a déjà masqué les pairs du transcript —
            // le prompt doit le dire à l'acteur, sinon il croit ouvrir la discussion.
            var isBlind = config.BlindFirstRound && round == 1 && !isFinal && roles.Length > 1;
            var p = builder.BuildDialogueTurn(role, issues, publishKey, transcript, round,
                config.MaxDialogueRounds, isFinal, sessionMode, frameworks, agentMemory, blindRound: isBlind);
            if (qaContext is not null)
                p = p with { UserPrompt = p.UserPrompt + "\n\n---\n\n## LOGS D'EXÉCUTION RÉELS (QA_COMMAND) + AUDIT DES PREUVES\n" + qaContext };

            // Directives déposées pendant que le groupe parlait : relues à chaque tour et
            // livrées à leur destinataire. Pas de contexte de synchro en console → pas de
            // deadlock sur ce GetResult ; l'IO est locale et brève.
            // (La directive de groupe, elle, est déjà entrée dans le transcript ci-dessus.)
            IngestPendingDirectivesAsync(artifactsDir, ct).GetAwaiter().GetResult();
            var turnDirectives = DirectivesForActor(role);
            if (!string.IsNullOrWhiteSpace(turnDirectives))
                p = p with { UserPrompt = p.UserPrompt + $"\n\n---\n\n## Directive de {config.ApproverName} — autorité, à respecter\n{turnDirectives}" };
            return p;
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
        // Garde de complétude : analyse → une section '## ANALYSE <KEY>' par US ;
        // QA → chaque US du sprint doit être mentionnée dans le verdict (sinon tour
        // de rattrapage). Empêche la QA de ne verdicter qu'une US (retour de Hajar).
        validateConclusion: group switch
        {
            ActorGroup.Analysis => text => AnalysisSections.ValidateCoverage(text, issues.Select(i => i.Key).ToList()),
            ActorGroup.Qa       => text => AnalysisSections.ValidateQaCoverage(text, issues.Select(i => i.Key).ToList()),
            _                   => null
        },
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
            await FlushDirectivesForAsync(usKey, ct);
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
    // Directives en attente sur l'US de pilotage (interventions de discussion,
    // checkpoints de groupe) : publiées ici, portées par le commentaire collectif.
    if (pubResult.Status != PublishStatus.Failed)
        await FlushDirectivesForAsync(publishKey, ct);

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

    // Verrouillé : en implémentation parallèle, deux moteurs ajoutent en concurrence.
    lock (reportEntries)
    {
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
    }

    return runResult;
}

// ─── Checkpoint d'intervention entre rounds (mode --interactive) ──────────────
// « GO pour continuer » est le motif reconnu par l'UI pour afficher le panneau GO/ARRÊT.
async Task<DialogueIntervention> RequestInterventionAsync(ActorGroup group, int round)
{
    Console.WriteLine();
    Console.WriteLine($"  ▶ Round {round - 1} terminé — discussion {group.GetGroupLabel()}");
    EventEmitter.Emit("checkpoint", new { kind = "round", group = group.ToString(), round });
    Console.Write("  GO pour continuer la discussion ? [Entrée=oui / n=arrêt / fin=conclure / texte=intervention] > ");
    var answer = CleanLine(Console.ReadLine());

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
        // Stockée + injectée aux acteurs ; publiée sur Jira portée par le commentaire
        // collectif du groupe sur l'US de pilotage (FlushDirectivesForAsync).
        await AddDirectiveAsync(pilotageKey, intervention.Text!);
    }
    if (intervention.Kind == InterventionKind.Stop)
        Console.WriteLine("  Arrêt demandé. Utilisez --resume pour reprendre la discussion.");

    return intervention;
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

    // Les interventions de l'approbatrice font partie de la traçabilité publiée
    var interventionTurns = outcome.Turns.Where(t => t.IsIntervention && t.Speaker == approverName).ToList();
    if (interventionTurns.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine($"### Interventions de {approverName}");
        sb.AppendLine();
        foreach (var iv in interventionTurns)
            sb.AppendLine($"- (round {iv.Round}) {iv.Content}");
    }

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
    // Mode UI : battement de cœur par minute — un acteur silencieux (réflexion
    // longue) ne doit plus ressembler à un blocage.
    if (Console.IsOutputRedirected)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(60_000, ct);
                    EventEmitter.Emit("actor-heartbeat", new { role = label, seconds = (int)sw.Elapsed.TotalSeconds });
                }
            }
            catch (TaskCanceledException) { }
        }, ct);
    }

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
