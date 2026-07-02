using System.Text;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

/// <summary>
/// Captures the result of one actor execution for the HTML report.
/// </summary>
public sealed record ActorReportEntry(
    ActorRole Role,
    bool Success,
    bool IsSemiManual,
    bool IsSkipped,
    int ExitCode,
    int ElapsedSeconds,
    int OutputChars,
    string? ErrorSnippet,
    string? OutputFilePath,
    string? SemiManualPromptPath);

/// <summary>
/// Generates a self-contained HTML run report after each sprint-launcher execution.
/// </summary>
public static class HtmlReportGenerator
{
    public static async Task<string> GenerateAsync(
        string artifactsDir,
        bool dryRun,
        string[] issueKeys,
        DateTimeOffset runStartedAt,
        IReadOnlyList<ActorReportEntry> entries,
        string? handoffFilePath,
        string? cacheFilePath)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var reportFile = Path.Combine(artifactsDir, $"run-report-{timestamp:yyyyMMdd-HHmmss}.html");

        var handoff = handoffFilePath is not null && File.Exists(handoffFilePath)
            ? await File.ReadAllTextAsync(handoffFilePath)
            : null;

        var sb = new StringBuilder();
        sb.Append(HtmlHead(dryRun, issueKeys, timestamp));
        sb.Append(SummarySection(dryRun, issueKeys, runStartedAt, timestamp, entries));
        sb.Append(ActorTableSection(entries));
        sb.Append(ActorOutputsSection(entries));
        sb.Append(CacheSection(cacheFilePath));
        sb.Append(HandoffSection(handoff));
        sb.Append(SemiManualSection(entries));
        sb.Append("</body></html>");

        await File.WriteAllTextAsync(reportFile, sb.ToString(), Encoding.UTF8);
        return reportFile;
    }

    private static string HtmlHead(bool dryRun, string[] keys, DateTimeOffset ts)
    {
        var mode = dryRun ? "DRY-RUN" : "WRITE";
        var title = $"Sprint Launcher — {mode} — {string.Join(", ", keys)} — {ts:yyyy-MM-dd HH:mm} UTC";
        // CSS kept in a non-interpolated raw string to avoid CS9006 (curly-brace conflicts with $"""...""")
        const string css = """
            *{box-sizing:border-box;margin:0;padding:0}
            body{font-family:system-ui,sans-serif;font-size:14px;background:#f8f9fa;color:#212529;padding:24px}
            h1{font-size:20px;margin-bottom:4px}
            h2{font-size:16px;margin:24px 0 8px;border-bottom:2px solid #dee2e6;padding-bottom:4px}
            h3{font-size:14px;margin:12px 0 4px}
            .badge{display:inline-block;padding:2px 8px;border-radius:12px;font-size:12px;font-weight:600}
            .mode-dry{background:#fff3cd;color:#856404}.mode-write{background:#d1e7dd;color:#0f5132}
            .meta{color:#6c757d;font-size:13px;margin:4px 0 16px}
            table{width:100%;border-collapse:collapse;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,.1)}
            th{background:#343a40;color:#fff;padding:10px 14px;text-align:left;font-size:13px}
            td{padding:8px 14px;border-bottom:1px solid #dee2e6;font-size:13px}
            tr:last-child td{border-bottom:none}
            .ok{color:#198754;font-weight:600}.fail{color:#dc3545;font-weight:600}
            .semi{color:#fd7e14;font-weight:600}.skip{color:#6c757d}
            details{background:#fff;border:1px solid #dee2e6;border-radius:6px;margin:8px 0}
            summary{padding:10px 14px;cursor:pointer;font-weight:600;font-size:13px;list-style:none}
            summary::-webkit-details-marker{display:none}
            summary::before{content:'▶ ';font-size:10px;color:#6c757d}
            details[open] summary::before{content:'▼ '}
            .output-pre{background:#1e1e2e;color:#cdd6f4;padding:16px;overflow:auto;font-family:monospace;font-size:12px;line-height:1.5;white-space:pre-wrap;word-break:break-word;max-height:600px;border-radius:0 0 6px 6px}
            .info-box{background:#fff;border:1px solid #dee2e6;border-radius:6px;padding:16px;margin:8px 0}
            .handoff-pre{background:#f8f9fa;border:1px solid #dee2e6;padding:12px;border-radius:4px;font-size:12px;white-space:pre-wrap;font-family:monospace}
            .semi-box{background:#fff8f0;border:1px solid #fd7e14;border-radius:6px;padding:16px;margin:8px 0}
            .copy-box{background:#1e1e2e;color:#cdd6f4;padding:8px 12px;border-radius:4px;font-family:monospace;font-size:12px;word-break:break-all}
            """;
        return $"""
            <!DOCTYPE html>
            <html lang="fr">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{Esc(title)}</title>
            <style>
            {css}
            </style>
            </head>
            <body>
            """;
    }

    private static string SummarySection(bool dryRun, string[] keys, DateTimeOffset start, DateTimeOffset end, IReadOnlyList<ActorReportEntry> entries)
    {
        var mode = dryRun ? "DRY-RUN" : "WRITE";
        var modeClass = dryRun ? "mode-dry" : "mode-write";
        var ok = entries.Count(e => e.Success && !e.IsSkipped);
        var fail = entries.Count(e => !e.Success && !e.IsSkipped && !e.IsSemiManual);
        var semi = entries.Count(e => e.IsSemiManual);
        var skip = entries.Count(e => e.IsSkipped);
        var elapsed = (int)(end - start).TotalSeconds;

        return $"""
            <h1>Sprint Launcher — Run Report <span class="badge {modeClass}">{mode}</span></h1>
            <p class="meta">
              Issues : <strong>{Esc(string.Join(", ", keys))}</strong> &nbsp;|&nbsp;
              Démarré : {start:yyyy-MM-dd HH:mm} UTC &nbsp;|&nbsp;
              Généré : {end:yyyy-MM-dd HH:mm} UTC &nbsp;|&nbsp;
              Durée totale : {elapsed}s
            </p>
            <p class="meta">
              <span class="ok">✓ {ok} réussi(s)</span> &nbsp;
              <span class="fail">✗ {fail} échoué(s)</span> &nbsp;
              <span class="semi">~ {semi} semi-manuel(s)</span> &nbsp;
              <span class="skip">○ {skip} ignoré(s)</span>
            </p>
            """;
    }

    private static string ActorTableSection(IReadOnlyList<ActorReportEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<h2>Acteurs</h2>");
        sb.AppendLine("<table><thead><tr><th>Acteur</th><th>Rôle</th><th>Statut</th><th>Exit</th><th>Durée</th><th>Chars</th></tr></thead><tbody>");
        foreach (var e in entries)
        {
            string icon, cls;
            if (e.IsSkipped) { icon = "○ ignoré"; cls = "skip"; }
            else if (e.IsSemiManual) { icon = "~ semi-manuel"; cls = "semi"; }
            else if (e.Success) { icon = "✓ OK"; cls = "ok"; }
            else { icon = "✗ ÉCHEC"; cls = "fail"; }

            var exit = e.IsSkipped || e.IsSemiManual ? "—" : e.ExitCode.ToString();
            var dur = e.IsSkipped ? "—" : $"{e.ElapsedSeconds}s";
            var chars = e.IsSkipped || e.IsSemiManual ? "—" : e.OutputChars.ToString();

            sb.AppendLine($"<tr><td><strong>{Esc(e.Role.ToString())}</strong></td><td>{Esc(e.Role.ToSignatureTag())}</td><td class=\"{cls}\">{icon}</td><td>{exit}</td><td>{dur}</td><td>{chars}</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
        return sb.ToString();
    }

    private static string ActorOutputsSection(IReadOnlyList<ActorReportEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<h2>Sorties acteurs</h2>");

        foreach (var e in entries)
        {
            if (e.IsSkipped) continue;

            string headerIcon = e.IsSemiManual ? "~" : (e.Success ? "✓" : "✗");
            string headerClass = e.IsSemiManual ? "semi" : (e.Success ? "ok" : "fail");

            sb.AppendLine("<details>");
            sb.AppendLine($"<summary><span class=\"{headerClass}\">{headerIcon}</span> {Esc(e.Role.ToString())} — {Esc(e.Role.ToSignatureTag())}</summary>");

            if (e.IsSemiManual && e.SemiManualPromptPath is not null)
            {
                sb.AppendLine($"<div class=\"output-pre\">Prompt semi-manuel écrit dans :\n{Esc(e.SemiManualPromptPath)}\n\nCopiez le contenu dans ChatGPT web, puis utilisez --publish-manual --from-file pour publier la réponse.</div>");
            }
            else if (e.OutputFilePath is not null && File.Exists(e.OutputFilePath))
            {
                var content = File.ReadAllText(e.OutputFilePath);
                sb.AppendLine($"<pre class=\"output-pre\">{Esc(content)}</pre>");
            }
            else if (!string.IsNullOrWhiteSpace(e.ErrorSnippet))
            {
                sb.AppendLine($"<pre class=\"output-pre\" style=\"color:#f38ba8\">ERREUR:\n{Esc(e.ErrorSnippet)}</pre>");
            }
            else
            {
                sb.AppendLine("<div class=\"output-pre\"><em>Pas de sortie disponible.</em></div>");
            }

            sb.AppendLine("</details>");
        }
        return sb.ToString();
    }

    private static string CacheSection(string? cacheFilePath)
    {
        if (cacheFilePath is null || !File.Exists(cacheFilePath))
            return "<h2>Cache Jira</h2><p class=\"meta\">Aucun cache — lecture complète (--no-cache ou premier run).</p>";

        var info = new FileInfo(cacheFilePath);
        return $"""
            <h2>Cache Jira</h2>
            <div class="info-box">
              <p>Fichier : <code>{Esc(Path.GetFileName(cacheFilePath))}</code></p>
              <p>Taille : {info.Length:N0} octets &nbsp;|&nbsp; Modifié : {info.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC</p>
            </div>
            """;
    }

    private static string HandoffSection(string? handoff)
    {
        if (string.IsNullOrWhiteSpace(handoff))
            return "<h2>Session handoff</h2><p class=\"meta\">Aucun fichier de handoff disponible.</p>";

        return $"""
            <h2>Session handoff</h2>
            <div class="info-box"><pre class="handoff-pre">{Esc(handoff)}</pre></div>
            """;
    }

    private static string SemiManualSection(IReadOnlyList<ActorReportEntry> entries)
    {
        var semiEntries = entries.Where(e => e.IsSemiManual && e.SemiManualPromptPath is not null).ToList();
        if (semiEntries.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("<h2>Acteurs semi-manuels (GptPilotage)</h2>");
        foreach (var e in semiEntries)
        {
            sb.AppendLine($"""
                <div class="semi-box">
                  <h3>{Esc(e.Role.ToString())} — instructions de lancement</h3>
                  <p><strong>1.</strong> Ouvrir le fichier de prompt :</p>
                  <div class="copy-box">{Esc(e.SemiManualPromptPath!)}</div>
                  <p style="margin-top:8px"><strong>2.</strong> Copier le contenu dans ChatGPT web (abonnement ChatGPT Plus).</p>
                  <p><strong>3.</strong> Copier la réponse dans un fichier, ex. <code>response-{e.Role}.txt</code>, puis publier :</p>
                  <div class="copy-box">dotnet run --project tools/sprint-launcher -- --publish-manual {e.Role} --from-file response-{e.Role}.txt {"{ISSUE-KEY}"} --write</div>
                </div>
                """);
        }
        return sb.ToString();
    }

    private static string Esc(string? s) =>
        (s ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
