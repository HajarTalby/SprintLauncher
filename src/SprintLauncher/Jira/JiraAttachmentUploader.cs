using System.Net.Http.Headers;
using System.Text;

namespace SprintLauncher.Jira;

public sealed class JiraAttachmentUploader
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly AuthenticationHeaderValue _auth;

    public JiraAttachmentUploader(HttpClient http, string baseUrl, string email, string apiToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{email}:{apiToken}")));
    }

    public async Task<AttachmentUploadResult> UploadAsync(
        string issueKey, string fileName, string content, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/attachments";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = _auth;
        req.Headers.Add("X-Atlassian-Token", "no-check");
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        return new AttachmentUploadResult(
            Success: resp.IsSuccessStatusCode,
            StatusCode: (int)resp.StatusCode,
            ResponseBody: body);
    }
}

public sealed record AttachmentUploadResult(
    bool Success,
    int StatusCode,
    string ResponseBody);
