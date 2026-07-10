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
/// Smoke E2E sur RELEASE RÉELLE (SERZENIA-143 lot 8) : l'outil génère la release
/// de l'application (RELEASE_COMMAND), la LANCE réellement, l'enregistre en vidéo
/// (ffmpeg) avec capture d'écran, et injecte le tout au verdict QA — la QA juge
/// l'application vivante, pas seulement les tests unitaires.
/// </summary>
public static class ReleaseSmoke
{
    public static async Task<string> RunAsync(
        string? releaseCommand, string? appExeRelative, string? repoRoot,
        string sprintTag, string? ffmpegPath, TimeSpan buildTimeout, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## SMOKE RELEASE RÉELLE");

        if (repoRoot is null || string.IsNullOrWhiteSpace(releaseCommand) || string.IsNullOrWhiteSpace(appExeRelative))
        {
            sb.AppendLine("NON CONFIGURÉ : définir RELEASE_COMMAND (génération de la release) et APP_EXE " +
                          "(chemin relatif de l'exe produit) dans .env — à traiter comme un écart DoD.");
            return sb.ToString();
        }

        // 1. Génération de la release
        sb.AppendLine($"$ {releaseCommand}");
        var buildLog = await QaExecutor.RunAsync(releaseCommand, repoRoot, buildTimeout, ct);
        sb.AppendLine(buildLog.Length > 4000 ? "…(tronqué)…\n" + buildLog[^4000..] : buildLog);

        var appExe = Path.Combine(repoRoot, appExeRelative);
        if (!File.Exists(appExe))
        {
            sb.AppendLine($"ÉCHEC : exe introuvable après génération ({appExeRelative}).");
            return sb.ToString();
        }

        var proofDir = Path.Combine(repoRoot, "artifacts", sprintTag, "RELEASE-SMOKE");
        Directory.CreateDirectory(Path.Combine(proofDir, "screenshots"));
        Directory.CreateDirectory(Path.Combine(proofDir, "videos"));

        // 2. Lancement réel + vidéo + capture
        Process? app = null;
        Process? ffmpeg = null;
        try
        {
            app = Process.Start(new ProcessStartInfo { FileName = appExe, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(appExe)! });
            sb.AppendLine($"Application lancée (PID {app?.Id}).");

            var videoPath = Path.Combine(proofDir, "videos", "release-smoke.mp4");
            if (ffmpegPath is not null && File.Exists(ffmpegPath))
            {
                ffmpeg = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -f gdigrab -framerate 10 -t 20 -i desktop \"{videoPath}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                });
                sb.AppendLine("Enregistrement vidéo 20 s (ffmpeg gdigrab)…");
            }
            else sb.AppendLine("Vidéo : ffmpeg non configuré (variable FFMPEG) — captures uniquement.");

            await Task.Delay(TimeSpan.FromSeconds(8), ct);

            if (app is { HasExited: true })
                sb.AppendLine($"ÉCHEC : l'application s'est fermée en {8}s (exit {app.ExitCode}) — crash au démarrage probable.");
            else
            {
                var shot = Path.Combine(proofDir, "screenshots", "release-smoke-startup.png");
                var psScript = "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; " +
                    "$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
                    "$bmp=New-Object System.Drawing.Bitmap($b.Width,$b.Height); " +
                    "$g=[System.Drawing.Graphics]::FromImage($bmp); " +
                    "$g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size); " +
                    $"$bmp.Save('{shot.Replace("'", "''")}'); $g.Dispose(); $bmp.Dispose()";
                var ps = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{psScript.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false, CreateNoWindow = true,
                });
                if (ps is not null) await ps.WaitForExitAsync(ct);
                sb.AppendLine(File.Exists(shot)
                    ? $"Capture au démarrage : {Path.GetFileName(shot)} ({new FileInfo(shot).Length / 1024} Ko)."
                    : "ÉCHEC capture d'écran.");
            }

            if (ffmpeg is not null)
            {
                await ffmpeg.WaitForExitAsync(ct);
                sb.AppendLine(File.Exists(videoPath)
                    ? $"Vidéo : {Path.GetFileName(videoPath)} ({new FileInfo(videoPath).Length / 1024} Ko)."
                    : "ÉCHEC vidéo ffmpeg.");
            }

            sb.AppendLine(app is { HasExited: false }
                ? "Application STABLE pendant le smoke (pas de crash)."
                : "Application terminée pendant le smoke — vérifier.");
        }
        catch (OperationCanceledException) { sb.AppendLine("Smoke interrompu."); }
        finally
        {
            try { if (app is { HasExited: false }) app.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        sb.AppendLine($"Preuves du smoke : artifacts/{sprintTag}/RELEASE-SMOKE/ (à référencer dans le verdict).");
        return sb.ToString();
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
