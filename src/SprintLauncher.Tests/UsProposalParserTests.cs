using SprintLauncher.Cadrage;
using Xunit;

namespace SprintLauncher.Tests;

public class UsProposalParserTests
{
    [Fact]
    public void Parses_proposals_block_from_synthesis()
    {
        var synthesis = """
        ## Synthèse finale

        Nous avons convergé sur deux US.

        ===US-PROPOSALS===
        ```json
        [
          {"summary": "US 1 — Login", "description": "## Objectif\nPermettre le login.", "readyConditions": ["maquette validée"]},
          {"summary": "US 2 — Logout", "description": "## Objectif\nPermettre le logout."}
        ]
        ```
        ===FIN-US-PROPOSALS===

        [DECISION FINALE]
        """;

        var proposals = UsProposalParser.TryParse(synthesis);

        Assert.Equal(2, proposals.Count);
        Assert.Equal("US 1 — Login", proposals[0].Summary);
        Assert.Contains("## Objectif", proposals[0].Description);
        Assert.Single(proposals[0].ReadyConditions!);
    }

    [Fact]
    public void Missing_block_returns_empty_list()
    {
        Assert.Empty(UsProposalParser.TryParse("Synthèse sans bloc structuré. [CONSENSUS]"));
    }

    [Fact]
    public void Invalid_json_returns_empty_list_without_throwing()
    {
        var broken = $"{UsProposalParser.StartMarker}\n[{{pas du json}}]\n{UsProposalParser.EndMarker}";
        Assert.Empty(UsProposalParser.TryParse(broken));
    }

    [Fact]
    public void Proposals_without_summary_or_description_are_filtered()
    {
        var block = $$"""
        {{UsProposalParser.StartMarker}}
        [{"summary": "", "description": "x"}, {"summary": "Valide", "description": "## Objectif\nok"}]
        {{UsProposalParser.EndMarker}}
        """;
        var proposals = UsProposalParser.TryParse(block);
        Assert.Single(proposals);
        Assert.Equal("Valide", proposals[0].Summary);
    }

    [Fact]
    public void Template_conformity_guard_lists_missing_sections()
    {
        var missing = UsProposalParser.MissingTemplateSections("## Objectif\ntexte\n## Contexte\ntexte");
        Assert.Contains("## Périmètre", missing);
        Assert.Contains("## Definition of Done", missing);
        Assert.DoesNotContain("## Objectif", missing);
    }
}
