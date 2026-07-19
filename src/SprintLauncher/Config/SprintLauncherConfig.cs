namespace SprintLauncher.Config;

public sealed class SprintLauncherConfig
{
    public required string JiraBaseUrl { get; init; }
    public required string JiraEmail { get; init; }
    public required string JiraApiToken { get; init; }
    public string ClaudeModel { get; init; } = "sonnet-5";
    public string CodexModel { get; init; } = "gpt-5.5";
    public int ActorTimeoutSeconds { get; init; } = 600;
    // Timeout dédié aux acteurs d'implémentation (un vrai dev prend 15-45 min).
    public int ImplementationTimeoutSeconds { get; init; } = 3600;
    // Nombre maximum d'allers-retours d'une discussion multi-tours (SERZENIA-143).
    public int MaxDialogueRounds { get; init; } = 3;
    // Spécialisation front/backend des moteurs d'implémentation — les US UI vont
    // au moteur front, les US backend au moteur back : pas de chevauchement de code.
    public string EngineFront { get; init; } = "GptImplementation";
    public string EngineBack { get; init; } = "ClaudeImplementation";
    // Revue croisée post-dev : l'autre moteur relit chaque US implémentée
    // (observations), les correctifs restent chez l'implémenteur. CROSS_REVIEW=false pour couper.
    public bool CrossReviewEnabled { get; init; } = true;
    // true : checkpoint d'intervention après chaque prise de parole (INTERVENTION_MODE=turn) ;
    // false (défaut) : entre les rounds seulement.
    public bool InterventionEveryTurn { get; init; }
    // Round 1 « à l'aveugle » : chaque membre d'une discussion produit SON analyse sans
    // voir celle des autres, puis les tours suivants croisent (accords/désaccords/
    // différentiel). Sans ça, le second membre se contente de valider le premier
    // (retour de Hajar, 2026-07-16). BLIND_FIRST_ROUND=false pour couper.
    public bool BlindFirstRound { get; init; } = true;
    // Chat live (SL 2026-07-16) : injection d'interventions PENDANT le tour d'un acteur
    // (claude --input-format stream-json / codex app-server turn/steer). DÉSACTIVÉ par
    // défaut — protocole non encore validé contre les binaires (quota épuisé à
    // l'écriture). LIVE_CHAT=true pour l'activer une fois le smoke live vert.
    public bool LiveChatEnabled { get; init; }
    // Implémentation parallèle : les deux moteurs avancent EN MÊME TEMPS, chacun sur
    // sa file (front/back) — possible car les périmètres de code sont disjoints.
    // Les revues croisées sont faites en fin de phase (le réviseur est occupé pendant).
    public bool ParallelImplementation { get; init; }
    // GptPilotage automatique via codex (défaut) ; GPT_PILOTAGE=semi-manual pour
    // revenir au flux copier/coller ChatGPT web historique.
    public bool GptPilotageAuto { get; init; } = true;
    // Quand les DEUX moteurs sont à quota épuisé : l'outil reste ouvert et retente
    // après QUOTA_WAIT_MINUTES (les fenêtres d'abonnement se rouvrent d'elles-mêmes).
    public bool QuotaWaitEnabled { get; init; } = true;
    public int QuotaWaitMinutes { get; init; } = 30;
    // QA outillée : commande exécutée réellement avant le verdict QA, logs injectés.
    public string QaCommand { get; init; } = "dotnet build --nologo && dotnet test --nologo";
    // Smoke E2E : génération de la release applicative + lancement réel avec
    // vidéo/captures injectés au verdict QA. Non configuré = écart DoD signalé.
    public string? ReleaseCommand { get; init; }
    public string? AppExe { get; init; }
    public string? FfmpegPath { get; init; }
    // Boucle de remédiation : cycles max de traitement des écarts avant décision Hajar.
    public int MaxRemediationCycles { get; init; } = 2;
    public string ProjectName { get; init; } = "SERZENIA";
    public string ApproverName { get; init; } = "Hajar";
    public string[] FrameworkKeys { get; init; } = ["SERZENIA-70", "SERZENIA-89", "SERZENIA-91"];
    // Root of the source repo — passed as --dir to claude.exe so agents find the code
    // regardless of where the launcher is launched from.
    public string? SerzeniaRepoRoot { get; init; }

    /// <summary>
    /// Chargement léger pour les modes hors-Jira (--smoke-live) : modèles + repo,
    /// sans exiger JIRA_BASE_URL/EMAIL/TOKEN. Lit le .env s'il existe.
    /// </summary>
    public static SprintLauncherConfig LoadModelsOnly(string? envFilePath = null)
    {
        EnvFileLoader.Load(envFilePath ?? FindEnvFile());
        return new SprintLauncherConfig
        {
            JiraBaseUrl = "", JiraEmail = "", JiraApiToken = "",
            ClaudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "sonnet-5",
            CodexModel = Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-5.5",
            SerzeniaRepoRoot = Environment.GetEnvironmentVariable("SERZENIA_REPO_ROOT"),
        };
    }

    public static SprintLauncherConfig Load(string? envFilePath = null)
    {
        EnvFileLoader.Load(envFilePath ?? FindEnvFile());

        var baseUrl = Require("JIRA_BASE_URL").TrimEnd('/');
        var email = Require("JIRA_EMAIL");
        var token = Require("JIRA_API_TOKEN");

        return new SprintLauncherConfig
        {
            JiraBaseUrl = baseUrl,
            JiraEmail = email,
            JiraApiToken = token,
            ClaudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "sonnet-5",
            CodexModel = Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-5.5",
            ActorTimeoutSeconds = ReadPositiveInt("ACTOR_TIMEOUT_SECONDS", 600),
            MaxDialogueRounds = ReadPositiveInt("MAX_DIALOGUE_ROUNDS", 3),
            ImplementationTimeoutSeconds = ReadPositiveInt("IMPL_TIMEOUT_SECONDS", 3600),
            EngineFront = Environment.GetEnvironmentVariable("ENGINE_FRONT") ?? "GptImplementation",
            EngineBack = Environment.GetEnvironmentVariable("ENGINE_BACK") ?? "ClaudeImplementation",
            CrossReviewEnabled = !string.Equals(Environment.GetEnvironmentVariable("CROSS_REVIEW"), "false", StringComparison.OrdinalIgnoreCase),
            InterventionEveryTurn = string.Equals(Environment.GetEnvironmentVariable("INTERVENTION_MODE"), "turn", StringComparison.OrdinalIgnoreCase),
            BlindFirstRound = !string.Equals(Environment.GetEnvironmentVariable("BLIND_FIRST_ROUND"), "false", StringComparison.OrdinalIgnoreCase),
            LiveChatEnabled = string.Equals(Environment.GetEnvironmentVariable("LIVE_CHAT"), "true", StringComparison.OrdinalIgnoreCase),
            ParallelImplementation = string.Equals(Environment.GetEnvironmentVariable("PARALLEL_IMPLEMENTATION"), "true", StringComparison.OrdinalIgnoreCase),
            GptPilotageAuto = !(Environment.GetEnvironmentVariable("GPT_PILOTAGE") ?? "auto").StartsWith("semi", StringComparison.OrdinalIgnoreCase),
            QuotaWaitEnabled = !string.Equals(Environment.GetEnvironmentVariable("QUOTA_WAIT"), "false", StringComparison.OrdinalIgnoreCase),
            QuotaWaitMinutes = ReadPositiveInt("QUOTA_WAIT_MINUTES", 30),
            QaCommand = Environment.GetEnvironmentVariable("QA_COMMAND") ?? "dotnet build --nologo && dotnet test --nologo",
            ReleaseCommand = Environment.GetEnvironmentVariable("RELEASE_COMMAND"),
            AppExe = Environment.GetEnvironmentVariable("APP_EXE"),
            FfmpegPath = Environment.GetEnvironmentVariable("FFMPEG"),
            MaxRemediationCycles = ReadPositiveInt("MAX_REMEDIATION_CYCLES", 2),
            ProjectName = Environment.GetEnvironmentVariable("PROJECT_NAME") ?? "SERZENIA",
            ApproverName = Environment.GetEnvironmentVariable("APPROVER_NAME") ?? "Hajar",
            FrameworkKeys = (Environment.GetEnvironmentVariable("FRAMEWORK_KEYS") ?? "SERZENIA-70,SERZENIA-89,SERZENIA-91")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            SerzeniaRepoRoot = ResolveRepoRoot(),
        };
    }

    // Priority: SERZENIA_REPO env var → walk up from BaseDirectory → walk up from CWD.
    // If none found, returns null (agents will warn but still run with CWD as context).
    private static string? ResolveRepoRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SERZENIA_REPO");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;

        return FindGitRoot(AppContext.BaseDirectory)
            ?? FindGitRoot(Directory.GetCurrentDirectory());
    }

    private static string? FindGitRoot(string? start)
    {
        var d = start is null ? null : new DirectoryInfo(start);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Required env var '{key}' is not set. Copy .env.example to .env and fill in the values.");

    private static int ReadPositiveInt(string key, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Environment variable '{key}' must be a positive integer.");
    }

    private static string FindEnvFile()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(baseDir, ".env"),
            Path.Combine(Directory.GetParent(baseDir)?.FullName ?? baseDir, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "sprint-launcher", ".env"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
