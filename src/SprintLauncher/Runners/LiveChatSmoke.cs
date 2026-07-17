using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

/// <summary>
/// Smoke du chat live (`sprint-launcher --smoke-live [claude|codex|all]`) : exécute le
/// VRAI chemin de code (ActorRunner en mode LiveInputDir) contre le vrai binaire, dépose
/// une intervention dans l'inbox PENDANT le tour, et vérifie que l'acteur l'a lue.
/// Consomme un peu de quota — à lancer au retour du quota, OBLIGATOIRE avant d'activer
/// LIVE_CHAT en run réel.
/// </summary>
public static class LiveChatSmoke
{
    public static async Task<int> RunAsync(string engineArg, ActorRunner runner, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sl-smoke-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        runner.LiveOutputDir = dir;
        runner.LiveInputDir = dir;

        var targets = engineArg.ToLowerInvariant() switch
        {
            "claude" => new[] { ActorRole.ClaudePilotage },
            "codex" => new[] { ActorRole.GptPilotage },
            _ => [ActorRole.ClaudePilotage, ActorRole.GptPilotage],
        };

        var allPass = true;
        foreach (var role in targets)
        {
            Console.WriteLine($"\n── SMOKE LIVE : {role} ──");
            allPass &= await SmokeOneAsync(role, runner, dir, ct);
        }

        Console.WriteLine(allPass
            ? "\n✅ SMOKE LIVE : PASS — le chat live peut être activé (LIVE_CHAT=true)."
            : $"\n❌ SMOKE LIVE : FAIL — ne PAS activer LIVE_CHAT avant correction.\n   Traces conservées dans : {dir} (relancer avec SL_LIVE_DEBUG=1 pour le JSONL brut)");
        if (allPass)
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        return allPass ? 0 : 1;
    }

    private static async Task<bool> SmokeOneAsync(ActorRole role, ActorRunner runner, string dir, CancellationToken ct)
    {
        // Tour volontairement long pour laisser une fenêtre d'injection réelle.
        var prompt = new ActorPrompt(role,
            "Tu es un testeur de protocole. Tu exécutes les instructions à la lettre.",
            "Étape 1 : écris exactement la ligne « SMOKE-PREMIER ». " +
            "Étape 2 : liste les nombres de 1 à 10, un par ligne, avec pour chacun une courte phrase. " +
            "Étape 3 : si un message ultérieur te donne un mot de passe, termine ta réponse par ce mot de passe.");

        // L'intervention part 3 s après le lancement — pendant que l'acteur travaille.
        var inboxPath = LiveInputInbox.PathFor(dir, role);
        var injector = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await File.AppendAllTextAsync(inboxPath,
                "Message ultérieur : le mot de passe est SMOKE-SECOND. Termine par ce mot de passe.\n", ct);
            Console.WriteLine("  → intervention déposée dans l'inbox (t+3s)");
        }, ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync(prompt, ct);
        sw.Stop();
        await injector;

        var sawFirst = result.Output.Contains("SMOKE-PREMIER", StringComparison.OrdinalIgnoreCase);
        var sawSecond = result.Output.Contains("SMOKE-SECOND", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"  exit={result.ExitCode} success={result.Success} durée={sw.Elapsed.TotalSeconds:0}s sortie={result.OutputChars()} car.");
        Console.WriteLine($"  livrable initial (SMOKE-PREMIER) : {(sawFirst ? "✓" : "✗")}");
        Console.WriteLine($"  intervention lue (SMOKE-SECOND) : {(sawSecond ? "✓ LUE EN COURS DE TOUR" : "✗ NON PRISE EN COMPTE")}");
        if (!result.Success && result.ErrorOutput is { Length: > 0 } err)
            Console.WriteLine($"  stderr : {err[..Math.Min(400, err.Length)]}");

        return result.Success && sawFirst && sawSecond;
    }

    private static string OutputChars(this ActorRunResult r) => r.Output.Length.ToString();
}
