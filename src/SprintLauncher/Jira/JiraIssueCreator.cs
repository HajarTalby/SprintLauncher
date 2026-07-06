using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SprintLauncher.Cadrage;

namespace SprintLauncher.Jira;

/// <summary>
/// Crée les US validées par l'approbatrice (SERZENIA-143 lot 3).
/// Dry-run par défaut : affiche ce qui serait créé sans écrire.
/// En écriture réelle : crée la Story puis la lie ("Relates") au ticket de référence.
/// </summary>
public sealed class JiraIssueCreator
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly AuthenticationHeaderValue _auth;
    private readonly bool _dryRun;

    public JiraIssueCreator(HttpClient http, string baseUrl, string email, string apiToken, bool dryRun = true)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{email}:{apiToken}")));
        _dryRun = dryRun;
    }

    public sealed record CreateResult(bool Created, string? Key, string? Error)
    {
        public static CreateResult DryRun() => new(false, null, null);
        public static CreateResult Ok(string key) => new(true, key, null);
        public static CreateResult Failed(string error) => new(false, null, error);
    }

    public async Task<CreateResult> CreateAsync(
        UsProposal proposal, string projectKey, string? linkToKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(proposal.Summary) || string.IsNullOrWhiteSpace(proposal.Description))
            throw new InvalidOperationException("Garde US vague : summary ou description vide. Refus de créer.");

        if (_dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Créerait l'US : {proposal.Summary}");
            Console.WriteLine($"          Projet {projectKey}, type Story{(linkToKey is null ? "" : $", liée à {linkToKey}")}");
            return CreateResult.DryRun();
        }

        var payload = new
        {
            fields = new
            {
                project = new { key = projectKey },
                issuetype = new { name = "Story" },
                summary = proposal.Summary,
                description = MarkdownToAdf.Convert(proposal.Description),
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/api/3/issue");
        req.Headers.Authorization = _auth;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return CreateResult.Failed($"HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

        using var doc = JsonDocument.Parse(body);
        var key = doc.RootElement.GetProperty("key").GetString()!;

        if (linkToKey is not null)
            await TryLinkAsync(key, linkToKey, ct);

        return CreateResult.Ok(key);
    }

    // Lien "Relates" vers le ticket de référence — best-effort, l'US créée prime.
    private async Task TryLinkAsync(string newKey, string refKey, CancellationToken ct)
    {
        var payload = new
        {
            type = new { name = "Relates" },
            inwardIssue = new { key = newKey },
            outwardIssue = new { key = refKey },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/rest/api/3/issueLink");
        req.Headers.Authorization = _auth;
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            Console.Error.WriteLine($"  ! Lien {newKey} → {refKey} non créé (HTTP {(int)resp.StatusCode}) — à faire manuellement.");
    }
}
