using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace SprintLauncher.Notifications;

/// <summary>
/// Émission des notifications Slack des acteurs du Sprint Launcher (SERZENIA-146).
/// Un canal par acteur (ccode / ag / codex) plus un canal <c>sl</c> pour l'orchestrateur.
/// Portée « Signal + suivi » (choix Hajar 2026-07-22) : début + fin de chaque tour d'acteur,
/// plus quota / blocages en priorité, et run démarré / terminé sur le canal <c>sl</c>.
///
/// L'émission passe par l'outil <c>notify.exe</c> (résolution .env, POST webhook, retries) :
/// tout le réseau et la lecture des secrets restent dans cet outil déjà testé. Ici on se
/// contente de lancer le process en fire-and-forget et de ne jamais bloquer le pipeline.
///
/// Garde-fou test : désactivé sous testhost — sans quoi un <c>dotnet test</c> exécuté après
/// que Hajar a rempli les webhooks enverrait de vrais messages Slack.
/// </summary>
public static class SlackSink
{
    private static readonly ConcurrentBag<Process> Started = new();
    private static readonly ConcurrentDictionary<string, string> ActorModels = new();
    private static readonly Lazy<string?> NotifierPath = new(ResolveNotifier);
    private static readonly Lazy<bool> EnabledFlag = new(ComputeEnabled);

    private static bool _runStarted;
    private static string _runLabel = "run";

    /// <summary>Vrai si notify.exe est présent, au moins un webhook est configuré, et on n'est pas sous test.</summary>
    public static bool Enabled => EnabledFlag.Value;

    /// <summary>Signale le début du run sur le canal <c>sl</c> (une seule fois).</summary>
    public static void RunStarted(string label)
    {
        if (!Enabled) return;
        _runStarted = true;
        _runLabel = label;
        Notify("sl", "info", $"Run {label} démarré");
    }

    /// <summary>Signale la fin du run et attend l'envoi des messages en vol. À appeler à la sortie.</summary>
    public static void RunFinished()
    {
        if (!Enabled || !_runStarted) return;
        Notify("sl", "info", $"Run {_runLabel} terminé");
        Drain(TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Traduit un événement du pipeline (type + payload JSON de l'EventEmitter) en message Slack.
    /// Ignore silencieusement les types non pertinents pour la portée « Signal + suivi ».
    /// </summary>
    public static void Forward(string type, JsonElement data)
    {
        if (!Enabled) return;

        var role = GetString(data, "role");
        var engine = GetString(data, "engine");
        var actor = role is not null ? ActorFromRole(role) : ActorFromEngine(engine);

        // Hajar (2026-07-22) : chaque notif d'acteur indique le modèle utilisé. Seul
        // actor-start porte le champ model ; on le mémorise par acteur pour l'ajouter aux
        // messages suivants (fin, quota, blocage).
        var model = GetString(data, "model");
        if (!string.IsNullOrWhiteSpace(model)) ActorModels[actor] = model!;

        switch (type)
        {
            case "actor-start":
                Notify(actor, "info", WithModel($"{role} démarre", actor));
                break;

            case "actor-done":
                if (GetBool(data, "success"))
                    Notify(actor, "info", WithModel($"{role} terminé ({GetInt(data, "seconds")}s, {GetInt(data, "chars")} chars)", actor));
                else
                    Notify(actor, "warn", WithModel($"{role} échec (exit {GetInt(data, "exitCode")})", actor), GetString(data, "error"));
                break;

            case "quota":
                Notify(actor, "blocked", WithModel($"Quota épuisé — {GetString(data, "key")}", actor));
                break;

            case "implementation-blocked":
                Notify(actor, "blocked", WithModel($"{GetString(data, "key")} bloqué (implémentation)", actor));
                break;
        }
    }

    private static string WithModel(string text, string actor) =>
        ActorModels.TryGetValue(actor, out var model) && !string.IsNullOrWhiteSpace(model)
            ? $"{text} · modèle: {model}"
            : text;

    /// <summary>Lance notify.exe pour un acteur donné, sans bloquer. Toute erreur est avalée.</summary>
    public static void Notify(string actor, string level, string text, string? context = null)
    {
        var notifier = NotifierPath.Value;
        if (notifier is null || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = notifier,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--actor");
            psi.ArgumentList.Add(actor);
            psi.ArgumentList.Add("--level");
            psi.ArgumentList.Add(level);
            psi.ArgumentList.Add("--text");
            psi.ArgumentList.Add(text);
            if (!string.IsNullOrWhiteSpace(context))
            {
                psi.ArgumentList.Add("--context");
                psi.ArgumentList.Add(context.Length > 300 ? context[..300] : context);
            }

            var process = Process.Start(psi);
            if (process is not null) Started.Add(process);
        }
        catch
        {
            // Slack ne doit jamais casser un run : on ignore toute défaillance d'émission.
        }
    }

    private static void Drain(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        foreach (var process in Started)
        {
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) { if (!process.HasExited) break; }
                else process.WaitForExit((int)remaining.TotalMilliseconds);
            }
            catch
            {
                // process déjà nettoyé — rien à attendre.
            }
        }
    }

    // ── Mapping rôle/moteur → canal d'acteur ─────────────────────────────────────
    // Casse volontairement sensible : "Ag" (AgImplementation) sans capturer "Arbitrage".
    internal static string ActorFromRole(string role)
    {
        if (role.Contains("Claude", StringComparison.Ordinal) || role.Contains("Ccode", StringComparison.Ordinal))
            return "ccode";
        if (role.Contains("Gpt", StringComparison.Ordinal) || role.Contains("Codex", StringComparison.Ordinal))
            return "codex";
        if (role.Contains("Ag", StringComparison.Ordinal))
            return "ag";
        return "sl";
    }

    internal static string ActorFromEngine(string? engine) => engine switch
    {
        not null when engine.Contains("Claude", StringComparison.OrdinalIgnoreCase) => "ccode",
        not null when engine.Contains("Codex", StringComparison.OrdinalIgnoreCase) => "codex",
        not null when engine.Contains("Agy", StringComparison.OrdinalIgnoreCase) => "ag",
        _ => "sl",
    };

    // ── JSON helpers ─────────────────────────────────────────────────────────────
    private static string? GetString(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static bool GetBool(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)
            ? v
            : 0;

    // ── Résolution notify.exe et activation ──────────────────────────────────────
    private static string? ResolveNotifier()
    {
        // 1. Surcharge explicite.
        var explicitPath = Environment.GetEnvironmentVariable("SPRINTLAUNCHER_NOTIFY");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        // 2. À côté de l'exe (layout release).
        var beside = Path.Combine(AppContext.BaseDirectory, "notify.exe");
        if (File.Exists(beside)) return beside;

        // 3. Dépôt de dev : remonter jusqu'au .sln puis tools/notify/published.
        var root = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
        if (root is not null)
        {
            var published = Path.Combine(root, "tools", "notify", "published", "notify.exe");
            if (File.Exists(published)) return published;
        }

        return null;
    }

    private static bool ComputeEnabled()
    {
        if (IsTestHost()) return false;
        if (NotifierPath.Value is null) return false;
        return HasAnyWebhook();
    }

    private static bool IsTestHost()
    {
        var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;
        return entry.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyWebhook()
    {
        var envPath = ResolveEnvPath();
        if (envPath is null || !File.Exists(envPath)) return false;

        foreach (var line in File.ReadLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            if (key.StartsWith("SLACK_WEBHOOK_", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                return true;
        }

        return false;
    }

    private static string? ResolveEnvPath()
    {
        var home = Environment.GetEnvironmentVariable("SPRINTLAUNCHER_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var p = Path.Combine(Environment.ExpandEnvironmentVariables(home.Trim()), ".env");
            if (File.Exists(p)) return p;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, ".env");
        if (File.Exists(beside)) return beside;

        var root = FindRepoRoot(AppContext.BaseDirectory) ?? FindRepoRoot(Directory.GetCurrentDirectory());
        return root is null ? null : Path.Combine(root, ".env");
    }

    private static string? FindRepoRoot(string start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SprintLauncher.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
