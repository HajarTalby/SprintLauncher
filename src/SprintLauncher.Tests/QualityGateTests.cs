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
