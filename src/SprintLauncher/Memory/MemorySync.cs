using System.Text;

namespace SprintLauncher.Memory;

public sealed record AgentMemoryContext(
    IReadOnlyList<AgentMemoryEntry> Entries,
    bool HasEntries)
{
    /// Savoir transverse : regles, bonnes pratiques, limitations apprises.
    public IReadOnlyList<AgentMemoryEntry> Shared =>
        Entries.Where(e => e.IsShared).ToList();

    /// Etat de dev propre a un contexte : tickets, avancement, decisions en cours.
    public IReadOnlyList<AgentMemoryEntry> Scoped =>
        Entries.Where(e => !e.IsShared).ToList();
}

public sealed record AgentMemoryEntry(string Name, string Type, string Scope, string Body)
{
    public bool IsShared => string.Equals(Scope, MemorySync.SharedScope, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Lit les fichiers de memoire de session (memory/*.md) et selectionne ce qui part
/// dans les prompts d'acteurs, sur deux axes distincts (demande de Hajar, 2026-07-19) :
///
///   - Le SAVOIR (regles, bonnes pratiques, consignes, limitations) est un capital
///     commun : il est injecte quel que soit le contexte, pour que ce qu'un acteur
///     apprend serve aux suivants.
///   - L'ETAT DE DEV (tickets en cours, avancement, decisions d'un chantier) est
///     propre a un contexte et ne doit PAS fuiter ailleurs. Au run sprint 6 du
///     2026-07-19, l'etat du dev du launcher se retrouvait dans les prompts des
///     acteurs qui analysaient des tickets produit.
///
/// L'axe est porte par <c>scope:</c> dans le frontmatter : <c>commun</c> pour le
/// savoir partage, sinon un identifiant de contexte (ex. <c>serzenia</c>,
/// <c>sprint-launcher</c>). A defaut de <c>scope:</c> explicite, le type tranche :
/// feedback/reference/user = savoir commun ; project = etat de dev, donc exclu tant
/// qu'il n'a pas de scope (defaut sur : mieux vaut manquer de contexte que fuiter).
/// </summary>
public static class MemorySync
{
    private const string SerzeniaClaudeProjectSlug = "c--Users-najwa-OneDrive-Desktop-SERZENIA";

    public const string SharedScope = "commun";

    /// Contexte du run courant, surchargeable pour les tests et les repos tiers.
    public const string ContextEnvVar = "SPRINTLAUNCHER_CONTEXT";

    public static AgentMemoryContext Load(string? projectRoot = null, string? context = null)
    {
        var memDir = FindMemoryDir(projectRoot);
        if (memDir is null) return new AgentMemoryContext([], false);

        context = ResolveContext(context);
        var entries = new List<AgentMemoryEntry>();

        foreach (var file in Directory.GetFiles(memDir, "*.md").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name == "MEMORY") continue; // skip index

            try
            {
                var content = File.ReadAllText(file);
                if (!TryParseFrontmatter(content, out var type, out var declaredScope, out var injectFlag, out var body))
                    continue;

                var scope = ResolveScope(type, declaredScope, injectFlag);
                if (scope is null) continue;

                // Commun partout ; sinon uniquement dans son propre contexte.
                if (!string.Equals(scope, SharedScope, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(scope, context, StringComparison.OrdinalIgnoreCase))
                    continue;

                entries.Add(new AgentMemoryEntry(name, type, scope, body.Trim()));
            }
            catch { /* skip unreadable files */ }
        }

        return new AgentMemoryContext(entries, entries.Count > 0);
    }

    /// Retourne le scope effectif, ou null si l'entree ne doit pas etre injectee.
    private static string? ResolveScope(string type, string declaredScope, bool injectFlag)
    {
        if (!string.IsNullOrWhiteSpace(declaredScope)) return declaredScope;

        // Savoir transverse : commun par nature.
        if (type is "feedback" or "reference" or "user") return SharedScope;

        // inject_to_agents sans scope explicite = savoir que Hajar veut partout.
        if (injectFlag) return SharedScope;

        // type=project sans scope : etat de dev non rattache -> exclu (defaut sur).
        return null;
    }

    private static string? ResolveContext(string? context)
    {
        // La variable d'environnement l'emporte : elle permet de rejouer un run sur un
        // autre chantier sans toucher au code.
        var fromEnv = Environment.GetEnvironmentVariable(ContextEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        return string.IsNullOrWhiteSpace(context) ? null : context;
    }

    private static string? FindMemoryDir(string? projectRoot)
    {
        var candidates = new List<string>();

        if (projectRoot is not null)
        {
            var projectMemoryDir = Path.Combine(projectRoot, "memory");
            return Directory.Exists(projectMemoryDir) ? projectMemoryDir : null;
        }

        var configuredMemoryDir = Environment.GetEnvironmentVariable("SPRINTLAUNCHER_MEMORY_DIR");
        if (!string.IsNullOrWhiteSpace(configuredMemoryDir))
            candidates.Add(configuredMemoryDir);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(
                userProfile,
                ".claude",
                "projects",
                SerzeniaClaudeProjectSlug,
                "memory"));
        }

        // Walk up from AppContext.BaseDirectory looking for a memory/ sibling
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "memory");
            candidates.Add(candidate);
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }

        // Also check current working directory
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "memory"));

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static bool TryParseFrontmatter(string content, out string type, out string scope, out bool injectToAgents, out string body)
    {
        type = "";
        scope = "";
        injectToAgents = false;
        body = content;

        if (!content.StartsWith("---")) return false;

        var end = content.IndexOf("\n---", 3);
        if (end < 0) return false;

        var frontmatter = content[3..end];
        body = content[(end + 4)..];

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');

            if (string.Equals(key, "type", StringComparison.OrdinalIgnoreCase))
                type = value;
            else if (string.Equals(key, "scope", StringComparison.OrdinalIgnoreCase))
                scope = value;
            else if (string.Equals(key, "inject_to_agents", StringComparison.OrdinalIgnoreCase) &&
                bool.TryParse(value, out var parsed))
                injectToAgents = parsed;
        }

        return !string.IsNullOrEmpty(type) || injectToAgents;
    }

    public static string BuildPromptSection(AgentMemoryContext ctx)
    {
        if (!ctx.HasEntries) return "";

        var sb = new StringBuilder();

        // Les deux natures restent separees dans le prompt : un acteur doit voir ce
        // qui est une regle durable et ce qui est l'etat d'un chantier en cours.
        AppendSection(sb, "## Regles et pratiques acquises (capital commun)", ctx.Shared);
        AppendSection(sb, "## Contexte du chantier en cours", ctx.Scoped);

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<AgentMemoryEntry> entries)
    {
        if (entries.Count == 0) return;

        sb.AppendLine(title);
        sb.AppendLine();
        foreach (var entry in entries)
        {
            sb.AppendLine($"### {entry.Name}");
            sb.AppendLine(entry.Body);
            sb.AppendLine();
        }
    }
}
