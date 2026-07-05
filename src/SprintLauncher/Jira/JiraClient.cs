using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SprintLauncher.Jira;

public sealed class JiraClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly AuthenticationHeaderValue _auth;

    public JiraClient(HttpClient http, string baseUrl, string email, string apiToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{email}:{apiToken}")));
    }

    public async Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}" +
                  "?fields=summary,description,attachment";
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var fields = doc.RootElement.GetProperty("fields");

        var summary = fields.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
        var descEl = fields.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null
            ? d : (JsonElement?)null;
        var description = AdfConverter.ToText(descEl);

        var comments = await GetAllCommentsAsync(issueKey, ct);
        return new JiraIssue(issueKey, summary, description, comments);
    }

    public async Task<List<JiraComment>> GetAllCommentsAsync(string issueKey, CancellationToken ct = default)
    {
        var comments = new List<JiraComment>();
        int startAt = 0;
        int? expectedTotal = null;

        while (true)
        {
            var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}" +
                      $"/comment?startAt={startAt}&maxResults=100&orderBy=created";
            using var resp = await SendAsync(HttpMethod.Get, url, ct);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var page = doc.RootElement;

            var total = page.GetProperty("total").GetInt32();
            expectedTotal ??= total;

            // Guard: total must not change between pages (indicates concurrent modification)
            if (total != expectedTotal)
                throw new JiraTruncationException(issueKey, expectedTotal.Value, total);

            var pageItems = page.GetProperty("comments").EnumerateArray().ToArray();
            foreach (var c in pageItems)
            {
                var id = c.GetProperty("id").GetString()!;
                var author = c.TryGetProperty("author", out var a) && a.TryGetProperty("displayName", out var dn)
                    ? dn.GetString() ?? "unknown" : "unknown";
                var created = c.TryGetProperty("created", out var cr) ? cr.GetString() ?? "" : "";
                var bodyEl = c.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null
                    ? b : (JsonElement?)null;
                comments.Add(new JiraComment(id, author, created, AdfConverter.ToText(bodyEl)));
            }

            startAt += pageItems.Length;
            if (pageItems.Length == 0 || startAt >= total) break;
        }

        // Anti-troncature guard: total fetched must match declared total
        if (expectedTotal.HasValue && comments.Count != expectedTotal.Value)
            throw new JiraTruncationException(issueKey, expectedTotal.Value, comments.Count);

        // Re-read the live counter after pagination. A concurrent Jira update must
        // never leave the caller with a silently stale or incomplete context.
        var finalTotal = await GetCommentTotalAsync(issueKey, ct);
        if (finalTotal != comments.Count)
            throw new JiraTruncationException(issueKey, finalTotal, comments.Count);

        return comments;
    }

    public async Task<(string Summary, string Description)> GetSummaryAndDescriptionAsync(string issueKey, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}" +
                  "?fields=summary,description";
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var fields = doc.RootElement.GetProperty("fields");

        var summary = fields.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
        var descEl = fields.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null
            ? d : (JsonElement?)null;
        var description = AdfConverter.ToText(descEl);

        return (summary, description);
    }

    // Returns all issue keys matching a JQL query (sprint resolution, etc.)
    public async Task<string[]> SearchKeysAsync(string jql, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/3/search" +
                  $"?jql={Uri.EscapeDataString(jql)}&fields=summary&maxResults=100";
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("issues")
            .EnumerateArray()
            .Select(i => i.GetProperty("key").GetString()!)
            .ToArray();
    }

    public async Task<int> GetCommentTotalAsync(string issueKey, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}" +
                  "/comment?startAt=0&maxResults=1";
        using var resp = await SendAsync(HttpMethod.Get, url, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("total").GetInt32();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        HttpResponseMessage? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);

            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = _auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            last = await _http.SendAsync(req, ct);
            var status = (int)last.StatusCode;
            if (status != 429 && status < 500) return last;
        }
        return last!;
    }
}
