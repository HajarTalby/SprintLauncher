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
                // ffmpeg est TRÈS verbeux sur stderr : sans drainage, le tampon du
                // pipe se remplit et ffmpeg SE BLOQUE en écriture (deadlock — constaté
                // en réel : ffmpeg figé 60 min, tout le run bloqué). On draine les deux
                // flux en tâche de fond (fire-and-forget) pour que le pipe ne sature jamais.
                if (ffmpeg is not null)
                {
                    _ = ffmpeg.StandardError.ReadToEndAsync();
                    _ = ffmpeg.StandardOutput.ReadToEndAsync();
                }
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
                if (ps is not null)
                {
                    using var psCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    psCts.CancelAfter(TimeSpan.FromSeconds(20));
                    try { await ps.WaitForExitAsync(psCts.Token); }
                    catch (OperationCanceledException) { try { if (!ps.HasExited) ps.Kill(true); } catch (InvalidOperationException) { } }
                }
                sb.AppendLine(File.Exists(shot)
                    ? $"Capture au démarrage : {Path.GetFileName(shot)} ({new FileInfo(shot).Length / 1024} Ko)."
                    : "ÉCHEC capture d'écran.");
            }

            if (ffmpeg is not null)
            {
                // Borne dure : vidéo de 20 s + marge. Même si ffmpeg se coince, on ne
                // bloque JAMAIS le run — on le tue et on continue.
                using var ffCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ffCts.CancelAfter(TimeSpan.FromSeconds(45));
                try { await ffmpeg.WaitForExitAsync(ffCts.Token); }
                catch (OperationCanceledException)
                {
                    try { if (!ffmpeg.HasExited) ffmpeg.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    sb.AppendLine("Vidéo : ffmpeg interrompu (dépassement 45 s) — capture partielle.");
                }
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

    // Fallback : les acteurs QA n'émettent pas toujours la section '## ECARTS'
    // structurée. Un verdict qui liste des « Conditions de revalidation », des
    // « Réserves », des « Écarts » ou des « Points bloquants » DÉCRIT des écarts —
    // le tool doit les traiter, pas clôturer le sprint. Analyse LIGNE PAR LIGNE
    // (prévisible et portable, contrairement à un méga-regex multiligne).

    // Titre de section signalant des écarts, une fois les marqueurs markdown retirés.
    private static readonly Regex EcartHeaderKeywords = new(
        @"^(?:conditions?\s+de\s+(?:revalidation|cl[ôo]ture)|r[ée]serves?|[eé]carts?" +
        @"|points?\s+bloquants?|non[- ]?conformit[ée]s?|blocages?|anomalies?\s+bloquantes?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Dernière signature d'US du verdict (ex. '[agent: gpt-chat | ... | us: SERZENIA-98]').
    private static readonly Regex SignatureUs = new(
        @"\bus:[ \t]*(?<key>[A-Z][A-Z0-9]+-\d+)",
        RegexOptions.Compiled);

    public static List<(string Key, string Description)> Parse(string qaVerdict)
    {
        if (string.IsNullOrWhiteSpace(qaVerdict)) return [];

        // 1) Format structuré préféré : section '## ECARTS' avec lignes '- [CLE] ...'
        var section = EcartsSection.Match(qaVerdict);
        if (section.Success)
        {
            var body = section.Groups["body"].Value;
            var keyed = EcartLine.Matches(body)
                .Select(m => (m.Groups["key"].Value, m.Groups["desc"].Value.Trim()))
                .Where(e => e.Item2.Length > 5)
                .ToList();
            if (keyed.Count > 0) return keyed;
            // 'AUCUN' explicite et aucune ligne d'écart → sprint propre.
            if (body.Contains("AUCUN", StringComparison.OrdinalIgnoreCase)) return [];
        }

        // 2) Fallback ligne par ligne : sections d'écarts en prose. On entre dans une
        // section sur un titre-clé, on collecte ses puces, on sort sur un autre titre
        // markdown ou la signature de l'acteur.
        var fallbackKey = DefaultKey(qaVerdict);
        var results = new List<(string Key, string Description)>();
        var inEcartSection = false;

        foreach (var raw in qaVerdict.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue; // les lignes vides n'interrompent pas la liste

            // Signature d'acteur / marqueur de décision → fin de section.
            if (line.StartsWith('[') &&
                (line.Contains("agent:") || line.Contains("collectif:") || line.Contains("decision:")))
            { inEcartSection = false; continue; }

            var isBullet = line.StartsWith("- ") || line.StartsWith("* ");

            if (IsEcartHeader(line)) { inEcartSection = true; continue; }

            if (inEcartSection && isBullet)
            {
                var own = EcartLine.Match(raw);
                var key = own.Success ? own.Groups["key"].Value : fallbackKey;
                var desc = (own.Success ? own.Groups["desc"].Value : line[2..]).Trim().TrimEnd('.', ' ');
                if (desc.Length > 5 && !results.Any(r => r.Key == key && r.Description == desc))
                    results.Add((key, desc));
            }
            else if (inEcartSection && !isBullet && (line.StartsWith('#') || line.StartsWith("**")))
            {
                inEcartSection = false; // nouveau titre non-écart → on sort
            }
        }
        return results;
    }

    // Un titre est une section d'écarts si, une fois retirés les # / ** / : de markdown,
    // il commence par un mot-clé d'écart (conditions de revalidation, réserves, écarts…).
    private static bool IsEcartHeader(string line)
    {
        var t = line.Trim().Trim('#', '*', ' ').TrimEnd(':', ' ', '*').Trim();
        return t.Length <= 60 && EcartHeaderKeywords.IsMatch(t);
    }

    // US de rattachement des écarts en prose : dernière signature 'us: KEY', sinon GLOBAL
    // (mappé sur le ticket de pilotage par la boucle de remédiation).
    private static string DefaultKey(string verdict)
    {
        var matches = SignatureUs.Matches(verdict);
        return matches.Count > 0 ? matches[^1].Groups["key"].Value : "GLOBAL";
    }
}
