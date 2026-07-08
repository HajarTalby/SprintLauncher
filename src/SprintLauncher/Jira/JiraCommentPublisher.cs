using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;

namespace SprintLauncher.Jira;

/// <summary>
/// Posts actor results as signed Jira comments.
/// Dry-run by default: prints the comment text without writing to Jira.
/// Refuses to post if commentBody is null, whitespace, or a placeholder.
/// </summary>
public sealed class JiraCommentPublisher
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _email;
    private readonly string _apiToken;
    private readonly AuthenticationHeaderValue _auth;
    private readonly bool _dryRun;

    public JiraCommentPublisher(HttpClient http, string baseUrl, string email, string apiToken, bool dryRun = true)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _email = email;
        _apiToken = apiToken;
        _auth = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{email}:{apiToken}")));
        _dryRun = dryRun;
    }

    public async Task<PublishResult> PublishAsync(
        string issueKey,
        ActorRunResult runResult,
        CancellationToken ct = default)
    {
        if (!runResult.Success)
            return PublishResult.Skipped($"Actor {runResult.Role} failed (exit {runResult.ExitCode}). Not publishing.");

        if (runResult.IsSemiManual)
            return PublishResult.Skipped(
                $"[SEMI-MANUAL] {runResult.Role}: prompt ready for manual ChatGPT session. " +
                $"Provide the response text via --publish-manual to post it.");

        var body = runResult.Output.Trim();
        GuardAgainstVagueComment(runResult.Role, body);

        var signed = AppendSignature(body, runResult.Role, issueKey);

        if (_dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Would post to {issueKey} for {runResult.Role}:");
            Console.WriteLine(signed);
            Console.WriteLine();
            return PublishResult.DryRun(signed);
        }

        if (await CommentAlreadyExistsAsync(issueKey, signed, ct))
            return PublishResult.Skipped("An identical signed Jira comment already exists.");

        return await PostCommentAsync(issueKey, signed, ct);
    }

    /// <summary>
    /// Posts a manually-provided GPT response (semi-manual flow).
    /// The caller supplies the text from the ChatGPT web session.
    /// Same guards apply: no vague/empty comment.
    /// </summary>
    public async Task<PublishResult> PublishManualAsync(
        string issueKey,
        ActorRole role,
        string responseText,
        CancellationToken ct = default)
    {
        var body = responseText.Trim();
        GuardAgainstVagueComment(role, body);
        var signed = AppendSignature(body, role, issueKey);

        if (_dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Would post manual response to {issueKey} for {role}:");
            Console.WriteLine(signed);
            return PublishResult.DryRun(signed);
        }

        if (await CommentAlreadyExistsAsync(issueKey, signed, ct))
            return PublishResult.Skipped("An identical signed Jira comment already exists.");

        return await PostCommentAsync(issueKey, signed, ct);
    }

    private async Task<bool> CommentAlreadyExistsAsync(
        string issueKey,
        string signedBody,
        CancellationToken ct)
    {
        var reader = new JiraClient(_http, _baseUrl, _email, _apiToken);
        var comments = await reader.GetAllCommentsAsync(issueKey, ct);
        return comments.Any(comment =>
            string.Equals(
                NormalizeComment(comment.Body),
                NormalizeComment(signedBody),
                StringComparison.Ordinal));
    }

    private static string NormalizeComment(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    private static void GuardAgainstVagueComment(ActorRole role, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new VagueCommentException(role, "Comment body is empty.");

        // Reject obvious placeholders / stub outputs
        var lower = body.ToLowerInvariant();
        var vaguePatterns = new[] { "todo", "placeholder", "...", "lorem ipsum", "test comment" };
        foreach (var p in vaguePatterns)
        {
            if (lower == p || lower.Length < 20)
                throw new VagueCommentException(role,
                    $"Comment body is too short or matches a placeholder pattern ('{body}'). Provide real content.");
        }
    }

    /// <summary>
    /// Posts a combined collective deliberation comment (committee or QA verdict).
    /// The combined body already contains all contributions; the group signature is appended.
    /// </summary>
    public async Task<PublishResult> PublishCollectiveAsync(
        string issueKey,
        ActorGroup group,
        string combinedBody,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(combinedBody) || combinedBody.Trim().Length < 20)
            throw new InvalidOperationException(
                $"Garde commentaire vague [collectif {group}]: corps vide ou trop court. Refus d'écrire.");

        var tag = group.ToGroupSignatureTag();
        var separator = combinedBody.EndsWith('\n') ? "" : "\n";
        var signed = $"{combinedBody}{separator}\n[collectif: {tag} | us: {issueKey}]";

        if (_dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Would post collective {group} to {issueKey}:");
            Console.WriteLine(signed);
            Console.WriteLine();
            return PublishResult.DryRun(signed);
        }

        if (await CommentAlreadyExistsAsync(issueKey, signed, ct))
            return PublishResult.Skipped("An identical signed collective comment already exists.");

        return await PostCommentAsync(issueKey, signed, ct);
    }

    /// <summary>
    /// Publie une décision de l'approbatrice saisie à un checkpoint (SERZENIA-143) :
    /// c'est l'OUTIL qui trace les décisions sur Jira, pas une session externe.
    /// </summary>
    public async Task<PublishResult> PublishDecisionAsync(
        string issueKey, string approverName, string decisionText, CancellationToken ct = default)
    {
        var body = decisionText.Trim();
        if (body.Length < 10)
            return PublishResult.Skipped("Décision trop courte pour être publiée.");

        var signed = $"## Décision de {approverName} (checkpoint sprint launcher)\n\n{body}\n\n[decision: {approverName} | us: {issueKey}]";

        if (_dryRun)
        {
            Console.WriteLine($"[DRY-RUN] Would post decision to {issueKey}:");
            Console.WriteLine(signed);
            return PublishResult.DryRun(signed);
        }

        if (await CommentAlreadyExistsAsync(issueKey, signed, ct))
            return PublishResult.Skipped("Décision identique déjà publiée.");

        return await PostCommentAsync(issueKey, signed, ct);
    }

    private static string AppendSignature(string body, ActorRole role, string issueKey)
    {
        var tag = role.ToSignatureTag();
        var separator = body.EndsWith('\n') ? "" : "\n";
        return $"{body}{separator}\n[agent: {tag} | us: {issueKey}]";
    }

    private async Task<PublishResult> PostCommentAsync(string issueKey, string text, CancellationToken ct)
    {
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment";
        // Markdown → ADF : titres, listes, tableaux et code rendus correctement dans Jira (SERZENIA-143)
        var payload = new { body = MarkdownToAdf.Convert(text) };
        var json = JsonSerializer.Serialize(payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = _auth;
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return PublishResult.Failed($"HTTP {(int)resp.StatusCode}: {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        var commentId = doc.RootElement.GetProperty("id").GetString()!;
        return PublishResult.Posted(commentId);
    }

}

public sealed record PublishResult(PublishStatus Status, string? CommentId, string? Message)
{
    public static PublishResult Posted(string commentId) => new(PublishStatus.Posted, commentId, null);
    public static PublishResult DryRun(string body) => new(PublishStatus.DryRun, null, body);
    public static PublishResult Skipped(string reason) => new(PublishStatus.Skipped, null, reason);
    public static PublishResult Failed(string error) => new(PublishStatus.Failed, null, error);
}

public enum PublishStatus { Posted, DryRun, Skipped, Failed }

public sealed class VagueCommentException(ActorRole role, string reason)
    : InvalidOperationException($"Garde commentaire vague [{role}]: {reason} Refusing to post.");
