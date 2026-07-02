using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SprintLauncher.Jira;

public sealed record FrameworkContext(
    IReadOnlyDictionary<string, string> Content,
    bool HasChanges);

/// <summary>
/// Reads SERZENIA framework tickets from Jira at startup, detects description changes
/// via SHA-256 hash, and provides up-to-date content for prompt injection.
/// Framework tickets: SERZENIA-70 (permissions), SERZENIA-89 (US template), SERZENIA-91 (validation).
/// </summary>
public sealed class FrameworkSync
{
    private static readonly string[] DefaultFrameworkKeys = ["SERZENIA-70", "SERZENIA-89", "SERZENIA-91"];
    private const string CacheFileName = "framework-cache.json";

    private readonly JiraClient _client;
    private readonly string[] _frameworkKeys;

    public FrameworkSync(JiraClient client, string[]? frameworkKeys = null)
    {
        _client = client;
        _frameworkKeys = frameworkKeys is { Length: > 0 } ? frameworkKeys : DefaultFrameworkKeys;
    }

    public async Task<FrameworkContext> SyncAsync(string artifactsRoot = "artifacts", CancellationToken ct = default)
    {
        var cacheFile = Path.Combine(artifactsRoot, CacheFileName);
        var cached = TryLoadCache(cacheFile);

        var result = new Dictionary<string, string>();
        bool hasChanges = false;

        foreach (var key in _frameworkKeys)
        {
            try
            {
                var (summary, description) = await _client.GetSummaryAndDescriptionAsync(key, ct);
                var content = string.IsNullOrWhiteSpace(description)
                    ? $"[{key}] {summary}"
                    : $"[{key}] {summary}\n\n{description}";
                var hash = ComputeHash(content);

                if (!cached.TryGetValue(key, out var entry) || entry.Hash != hash)
                {
                    cached[key] = new CacheEntry(hash, DateTimeOffset.UtcNow);
                    hasChanges = true;
                    Console.WriteLine($"  Frameworks — {key} : mis à jour");
                }
                else
                {
                    Console.WriteLine($"  Frameworks — {key} : inchangé");
                }

                result[key] = content;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Frameworks — {key} : erreur ({ex.Message[..Math.Min(60, ex.Message.Length)]}) — ignoré");
                // If we have a cached version, use it even if fetch failed
                if (cached.TryGetValue(key, out _))
                    Console.WriteLine($"  Frameworks — {key} : utilisation du cache local");
            }
        }

        if (hasChanges)
        {
            Directory.CreateDirectory(artifactsRoot);
            var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cacheFile, json, ct);
        }

        return new FrameworkContext(result, hasChanges);
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16];
    }

    private static Dictionary<string, CacheEntry> TryLoadCache(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json) ?? [];
        }
        catch { return []; }
    }

    private sealed record CacheEntry(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("cachedAt")] DateTimeOffset CachedAt);
}
