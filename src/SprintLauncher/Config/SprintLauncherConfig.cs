namespace SprintLauncher.Config;

public sealed class SprintLauncherConfig
{
    public required string JiraBaseUrl { get; init; }
    public required string JiraEmail { get; init; }
    public required string JiraApiToken { get; init; }
    public string ClaudeModel { get; init; } = "claude-opus-4-8";
    public string CodexModel { get; init; } = "gpt-5.5";
    public int ActorTimeoutSeconds { get; init; } = 600;
    // Nombre maximum d'allers-retours d'une discussion multi-tours (SERZENIA-143).
    public int MaxDialogueRounds { get; init; } = 3;
    // Spécialisation front/backend des moteurs d'implémentation — les US UI vont
    // au moteur front, les US backend au moteur back : pas de chevauchement de code.
    public string EngineFront { get; init; } = "GptImplementation";
    public string EngineBack { get; init; } = "ClaudeImplementation";
    // Revue croisée post-dev : l'autre moteur relit chaque US implémentée
    // (observations), les correctifs restent chez l'implémenteur. CROSS_REVIEW=false pour couper.
    public bool CrossReviewEnabled { get; init; } = true;
    public string ProjectName { get; init; } = "SERZENIA";
    public string ApproverName { get; init; } = "Hajar";
    public string[] FrameworkKeys { get; init; } = ["SERZENIA-70", "SERZENIA-89", "SERZENIA-91"];
    // Root of the source repo — passed as --dir to claude.exe so agents find the code
    // regardless of where the launcher is launched from.
    public string? SerzeniaRepoRoot { get; init; }

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
            ClaudeModel = Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "claude-opus-4-8",
            CodexModel = Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-5.5",
            ActorTimeoutSeconds = ReadPositiveInt("ACTOR_TIMEOUT_SECONDS", 600),
            MaxDialogueRounds = ReadPositiveInt("MAX_DIALOGUE_ROUNDS", 3),
            EngineFront = Environment.GetEnvironmentVariable("ENGINE_FRONT") ?? "GptImplementation",
            EngineBack = Environment.GetEnvironmentVariable("ENGINE_BACK") ?? "ClaudeImplementation",
            CrossReviewEnabled = !string.Equals(Environment.GetEnvironmentVariable("CROSS_REVIEW"), "false", StringComparison.OrdinalIgnoreCase),
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
