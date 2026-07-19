using System.Text;

namespace SprintLauncher.Memory;

public sealed record AgentMemoryContext(
    IReadOnlyList<AgentMemoryEntry> Entries,
    bool HasEntries);

public sealed record AgentMemoryEntry(string Name, string Type, string Body);

/// <summary>
/// Reads Claude Code session memory files (memory/*.md) and surfaces entries
/// tagged for agent injection. Only entries with metadata.type=project or
/// metadata.inject_to_agents=true are included — feedback memories specific
/// to Claude Code's own behavior are excluded.
/// </summary>
public static class MemorySync
{
    private const string SerzeniaClaudeProjectSlug = "c--Users-najwa-OneDrive-Desktop-SERZENIA";

    public static AgentMemoryContext Load(string? projectRoot = null)
    {
        var memDir = FindMemoryDir(projectRoot);
        if (memDir is null) return new AgentMemoryContext([], false);

        var entries = new List<AgentMemoryEntry>();

        foreach (var file in Directory.GetFiles(memDir, "*.md").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name == "MEMORY") continue; // skip index

            try
            {
                var content = File.ReadAllText(file);
                if (!TryParseFrontmatter(content, out var type, out var injectFlag, out var body))
                    continue;

                // Include: explicit inject_to_agents flag, or type=project.
                if (!injectFlag && !string.Equals(type, "project", StringComparison.OrdinalIgnoreCase))
                    continue;

                entries.Add(new AgentMemoryEntry(name, type, body.Trim()));
            }
            catch { /* skip unreadable files */ }
        }

        return new AgentMemoryContext(entries, entries.Count > 0);
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

    private static bool TryParseFrontmatter(string content, out string type, out bool injectToAgents, out string body)
    {
        type = "";
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
        sb.AppendLine("## Mémoire projet SERZENIA (contexte opérationnel)");
        sb.AppendLine();
        foreach (var entry in ctx.Entries)
        {
            sb.AppendLine($"### {entry.Name}");
            sb.AppendLine(entry.Body);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
