using System.Text.Json;
using System.Text.Json.Serialization;

namespace SprintLauncher.Jira;

public sealed record CachedIssue(
    string Key,
    string Summary,
    string Description,
    int CommentTotal,
    List<JiraComment> Comments);

public sealed class JiraCacheEntry
{
    public DateTimeOffset SavedAt { get; init; }
    public string[] Keys { get; init; } = [];
    public Dictionary<string, CachedIssue> Issues { get; init; } = [];
}

public static class JiraCache
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static async Task SaveAsync(string[] keys, IReadOnlyList<JiraIssue> issues,
        string cacheDir = "artifacts/jira-cache", CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var slug = string.Join("-", keys);
        var path = Path.Combine(cacheDir, $"{ts}-{slug}.json");

        var entry = new JiraCacheEntry
        {
            SavedAt = DateTimeOffset.UtcNow,
            Keys = keys,
            Issues = issues.ToDictionary(
                i => i.Key,
                i => new CachedIssue(i.Key, i.Summary, i.Description, i.Comments.Count,
                    i.Comments.ToList()))
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, _json), ct);
    }

    public static async Task<JiraCacheEntry?> TryLoadLatestAsync(string[] keys,
        string cacheDir = "artifacts/jira-cache", CancellationToken ct = default)
    {
        if (!Directory.Exists(cacheDir)) return null;
        var slug = string.Join("-", keys);
        var latest = Directory.GetFiles(cacheDir, $"*-{slug}.json")
            .OrderByDescending(f => f)
            .FirstOrDefault();
        if (latest is null) return null;

        var json = await File.ReadAllTextAsync(latest, ct);
        return JsonSerializer.Deserialize<JiraCacheEntry>(json, _json);
    }
}
