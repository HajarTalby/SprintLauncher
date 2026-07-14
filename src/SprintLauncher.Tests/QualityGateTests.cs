using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class EcartParserTests
{
    [Fact]
    public void Parses_structured_ecarts_with_keys()
    {
        var verdict = """
        ## Verdict par critère
        - build : OK
        - preuves : KO

        ## ECARTS
        - [SERZENIA-112] Captures du parcours Google Sign-In absentes → produire les screenshots du scénario réel
        - [SERZENIA-116] Sentry non branché (DSN manquant) → intégrer dès réception du DSN
        - [GLOBAL] Branche dédiée non respectée → documenter et corriger le process

        [DECISION FINALE]
        """;
        var ecarts = EcartParser.Parse(verdict);
        Assert.Equal(3, ecarts.Count);
        Assert.Equal("SERZENIA-112", ecarts[0].Key);
        Assert.Contains("screenshots", ecarts[0].Description);
        Assert.Equal("GLOBAL", ecarts[2].Key);
    }

    [Fact]
    public void Aucun_means_sprint_clean()
    {
        Assert.Empty(EcartParser.Parse("## Verdict\nPASS.\n\n## ECARTS\nAUCUN\n[DECISION FINALE]"));
    }

    [Fact]
    public void Missing_section_yields_empty_not_crash()
    {
        Assert.Empty(EcartParser.Parse("PASS sans section structurée."));
    }

    [Fact]
    public void Clean_go_verdict_without_reservations_is_empty()
    {
        var verdict = """
        ## Verdict par critère
        - build : OK
        - preuves : OK

        GO sans réserve — tous les critères DoD satisfaits.

        [agent: gpt-chat | role: qa-verdict | us: SERZENIA-98]
        """;
        Assert.Empty(EcartParser.Parse(verdict));
    }

    // Régression sprint 6 : le verdict QA réel liste ses écarts en prose sous
    // « Conditions De Revalidation » (pas de section '## ECARTS'). Le parseur DOIT
    // quand même les détecter et les rattacher à l'US de la signature — sinon la
    // remédiation croit le sprint propre et le clôture à tort.
    [Fact]
    public void Prose_revalidation_conditions_are_detected_as_ecarts()
    {
        var verdict = """
        ## Verdict QA — SERZENIA-98

        Le rendu n'est pas prouvé.

        **Conditions De Revalidation**

        - Produire et poster les captures attendues : Home v2, Mer calme, Aube lumineuse.
        - Fournir preuve UI non-headless ou captures device/Windows exploitables.
        - Compléter artefacts 98 : checklist, mapping preuve/ticket, test-results, screenshots.

        [agent: gpt-chat | role: qa-verdict | us: SERZENIA-98]
        [CONSENSUS]
        """;

        var ecarts = EcartParser.Parse(verdict);
        Assert.Equal(3, ecarts.Count);
        Assert.All(ecarts, e => Assert.Equal("SERZENIA-98", e.Key));
        Assert.Contains(ecarts, e => e.Description.Contains("captures attendues"));
    }

    [Fact]
    public void Prose_ecarts_without_signature_fall_back_to_global()
    {
        var verdict = """
        ## Réserves

        - Bottom nav non prouvée par logs ou captures.
        - Traçabilité Git à nettoyer.
        """;
        var ecarts = EcartParser.Parse(verdict);
        Assert.Equal(2, ecarts.Count);
        Assert.All(ecarts, e => Assert.Equal("GLOBAL", e.Key));
    }
}

public class CodexJsonInterpreterTests
{
    // Schéma réel capturé de `codex exec --json` (codex-cli 0.142.5).
    [Fact]
    public void Streams_commands_live_and_extracts_final_message()
    {
        var it = new CodexJsonInterpreter();
        it.Interpret("""{"type":"thread.started","thread_id":"abc"}""");
        it.Interpret("""{"type":"turn.started"}""");
        var cmd = it.Interpret("""{"type":"item.started","item":{"id":"i1","type":"command_execution","command":"ls -la","status":"in_progress"}}""");
        it.Interpret("""{"type":"item.completed","item":{"id":"i1","type":"command_execution","command":"ls -la","aggregated_output":"...","exit_code":0,"status":"completed"}}""");
        var msg = it.Interpret("""{"type":"item.completed","item":{"id":"i2","type":"agent_message","text":"Terminé : 3 fichiers modifiés."}}""");
        it.Interpret("""{"type":"turn.completed","usage":{"input_tokens":10}}""");

        Assert.Contains("ls -la", cmd);        // commande montrée au démarrage (temps réel)
        Assert.Contains("🔧", cmd!);
        Assert.Equal("Terminé : 3 fichiers modifiés.", msg);
        Assert.Equal("Terminé : 3 fichiers modifiés.", it.Output); // livrable = dernier agent_message
    }

    [Fact]
    public void Non_json_line_is_passthrough()
    {
        var it = new CodexJsonInterpreter();
        Assert.Equal("texte brut", it.Interpret("texte brut"));
    }
}

public class StreamJsonInterpreterTests
{
    [Fact]
    public void Assistant_text_is_streamed_and_accumulated()
    {
        var i = new StreamJsonInterpreter();
        var live = i.Interpret("""{"type":"assistant","message":{"content":[{"type":"text","text":"Analyse du module A."}]}}""");
        Assert.Equal("Analyse du module A.", live);
        Assert.Contains("module A", i.AccumulatedText);
    }

    [Fact]
    public void Tool_use_becomes_readable_live_line()
    {
        var i = new StreamJsonInterpreter();
        var live = i.Interpret("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}]}}""");
        Assert.Contains("🔧 Bash dotnet test", live);
    }

    [Fact]
    public void Result_event_wins_as_final_output()
    {
        var i = new StreamJsonInterpreter();
        i.Interpret("""{"type":"assistant","message":{"content":[{"type":"text","text":"brouillon"}]}}""");
        i.Interpret("""{"type":"result","subtype":"success","result":"## Livrable final propre"}""");
        Assert.Equal("## Livrable final propre", i.Output);
    }

    [Fact]
    public void Non_json_lines_pass_through()
    {
        var i = new StreamJsonInterpreter();
        Assert.Equal("sortie brute", i.Interpret("sortie brute"));
        Assert.Contains("sortie brute", i.Output);
    }
}

public class ProofAuditorTests : IDisposable
{
    private readonly string _repo;

    public ProofAuditorTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "sl-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repo, "artifacts", "sprint6", "SERZ-1", "screenshots"));
        File.WriteAllText(Path.Combine(_repo, "artifacts", "sprint6", "SERZ-1", "screenshots", "step1.png"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch (IOException) { }
    }

    [Fact]
    public void Reports_present_and_missing_proofs_per_us()
    {
        var audit = ProofAuditor.Audit(_repo, "sprint6", ["SERZ-1", "SERZ-2"]);
        Assert.Contains("SERZ-1 : screenshots=1 fichier(s)", audit);
        Assert.Contains("SERZ-2 : screenshots=ABSENT", audit);
        Assert.Contains("écart DoD", audit);
    }
}
