using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SprintLauncher.Runners;

/// <summary>
/// QA outillée (SERZENIA-143 lot 8, protocole SERZENIA-140) : le verdict QA ne
/// repose plus sur le seul contexte Jira — l'outil exécute réellement QA_COMMAND
/// (build + tests) et injecte les logs bruts aux acteurs QA.
/// </summary>
public static class QaExecutor
{
    public static async Task<string> RunAsync(string qaCommand, string? repoRoot, TimeSpan timeout, CancellationToken ct)
    {
        if (repoRoot is null)
            return "AUCUNE EXÉCUTION : dépôt source introuvable (SERZENIA_REPO absent).";

        var sb = new StringBuilder();
        sb.AppendLine($"$ {qaCommand}   (cwd: {repoRoot})");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {qaCommand}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine("[err] " + e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            lock (sb) sb.AppendLine($"[exit code: {process.ExitCode}]");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            lock (sb) sb.AppendLine("[QA_COMMAND interrompu par timeout]");
        }

        var log = sb.ToString();
        // Les logs de tests peuvent être énormes : garder la fin (verdicts/échecs y figurent)
        const int maxChars = 30000;
        return log.Length <= maxChars ? log : "…(début tronqué)…\n" + log[^maxChars..];
    }
}

/// <summary>
/// Audit automatique des preuves DoD (SERZENIA-91) : présence par US des
/// captures/vidéos/résultats de tests dans artifacts/sprintN/&lt;US&gt;/.
/// Le résultat est injecté aux acteurs QA — une preuve absente devient un écart.
/// </summary>
public static class ProofAuditor
{
    public static string Audit(string? repoRoot, string sprintTag, IReadOnlyList<string> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## AUDIT AUTOMATIQUE DES PREUVES (SERZENIA-91)");
        if (repoRoot is null) { sb.AppendLine("Dépôt introuvable — audit impossible."); return sb.ToString(); }

        foreach (var key in keys)
        {
            var baseDir = Path.Combine(repoRoot, "artifacts", sprintTag, key);
            string Count(string sub)
            {
                var d = Path.Combine(baseDir, sub);
                if (!Directory.Exists(d)) return "ABSENT";
                var n = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length;
                return n == 0 ? "VIDE" : $"{n} fichier(s)";
            }
            sb.AppendLine($"- {key} : screenshots={Count("screenshots")} · videos={Count("videos")} · test-results={Count("test-results")} · logs={Count("logs")}");
        }
        sb.AppendLine("Règle : toute US à critère visuel sans screenshots/vidéo = écart DoD ; test-results ABSENT = écart.");
        return sb.ToString();
    }
}

/// <summary>
/// Écarts structurés extraits du verdict QA : lignes '- [CLE] description'.
/// Le sprint n'est pas terminé tant que cette liste n'est pas vide — l'outil
/// enchaîne des cycles de remédiation (traitement intégral, pas de simple doc).
/// </summary>
public static class EcartParser
{
    private static readonly Regex EcartLine = new(
        @"^\s*[-*]\s*\[(?<key>[A-Z][A-Z0-9]+-\d+|GLOBAL|TRANSVERSE)\]\s*(?<desc>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex EcartsSection = new(
        @"##\s*ECARTS\s*\n(?<body>.*?)(?=\n##\s|\Z)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<(string Key, string Description)> Parse(string qaVerdict)
    {
        var section = EcartsSection.Match(qaVerdict);
        if (!section.Success) return [];
        var body = section.Groups["body"].Value;
        if (body.Contains("AUCUN", StringComparison.OrdinalIgnoreCase) && !EcartLine.IsMatch(body))
            return [];

        return EcartLine.Matches(body)
            .Select(m => (m.Groups["key"].Value, m.Groups["desc"].Value.Trim()))
            .Where(e => e.Item2.Length > 5)
            .ToList();
    }
}
