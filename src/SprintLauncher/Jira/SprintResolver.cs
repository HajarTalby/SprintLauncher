using System.Text.Json;

namespace SprintLauncher.Jira;

/// <summary>
/// Resolves sprint tickets from Jira (source of truth) with a local sprints.json as cache.
/// First run fetches from Jira and writes the cache. Subsequent runs detect any diff and
/// re-fetch automatically — no manual file editing needed.
/// Sprint name format defaults to "Sprint {0}" (e.g., --sprint 6 → "Sprint 6").
/// Override via SPRINT_NAME_FORMAT env var.
/// </summary>
public sealed class SprintResolver
{
    private readonly JiraClient _client;
    private readonly string _projectKey;
    private readonly string _sprintNameFormat;

    public SprintResolver(JiraClient client, string projectKey, string? sprintNameFormat = null)
    {
        _client = client;
        _projectKey = projectKey;
        _sprintNameFormat = sprintNameFormat
            ?? Environment.GetEnvironmentVariable("SPRINT_NAME_FORMAT")
            ?? "Sprint {0}";
    }

    public async Task<string[]> ResolveAsync(
        string sprintId,
        bool noCache = false,
        string cacheFilePath = "sprints.json",
        CancellationToken ct = default)
    {
        var sprintName = string.Format(_sprintNameFormat, sprintId);

        var cached = noCache ? null : TryReadCache(cacheFilePath, sprintId);

        if (cached is not null)
        {
            // Differential check: compare live Jira list with cache
            var live = await FetchFromJiraAsync(sprintName, ct);
            if (live.Length == cached.Length && live.SequenceEqual(cached))
            {
                Console.WriteLine($"  Sprint {sprintId} : {cached.Length} ticket(s) [cache — inchangé]");
                return cached;
            }

            Console.WriteLine($"  Sprint {sprintId} : diff détectée ({cached.Length} → {live.Length}) — cache mis à jour");
            SaveCache(cacheFilePath, sprintId, live);
            return live;
        }

        // No cache or --no-cache: fetch from Jira
        var keys = await FetchFromJiraAsync(sprintName, ct);
        if (keys.Length == 0)
            throw new ArgumentException(
                $"Sprint '{sprintId}' (JQL : project = {_projectKey} AND sprint = \"{sprintName}\") : aucun ticket trouvé dans Jira. " +
                $"Vérifiez le numéro de sprint ou définissez SPRINT_NAME_FORMAT dans .env.");

        SaveCache(cacheFilePath, sprintId, keys);
        Console.WriteLine($"  Sprint {sprintId} : {keys.Length} ticket(s) [Jira → cache créé]");
        return keys;
    }

    private async Task<string[]> FetchFromJiraAsync(string sprintName, CancellationToken ct)
    {
        var jql = $"project = {_projectKey} AND sprint = \"{sprintName}\" ORDER BY key ASC";
        return await _client.SearchKeysAsync(jql, ct);
    }

    private static string[]? TryReadCache(string path, string sprintId)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(sprintId, out var arr))
                return arr.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }
        catch { }
        return null;
    }

    private static void SaveCache(string path, string sprintId, string[] keys)
    {
        var dict = new Dictionary<string, string[]>();
        if (File.Exists(path))
        {
            try
            {
                using var existing = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in existing.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.EnumerateArray().Select(e => e.GetString()!).ToArray();
            }
            catch { }
        }
        dict[sprintId] = keys;
        File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
    }
}
