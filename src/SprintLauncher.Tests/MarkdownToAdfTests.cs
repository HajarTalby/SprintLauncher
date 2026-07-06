using System.Text.Json;
using SprintLauncher.Jira;
using Xunit;

namespace SprintLauncher.Tests;

public class MarkdownToAdfTests
{
    private static JsonElement Convert(string markdown)
    {
        var json = JsonSerializer.Serialize(MarkdownToAdf.Convert(markdown));
        return JsonDocument.Parse(json).RootElement;
    }

    private static IEnumerable<JsonElement> Content(JsonElement doc) =>
        doc.GetProperty("content").EnumerateArray();

    [Fact]
    public void Heading_becomes_heading_node_with_level()
    {
        var doc = Convert("## Titre de section");
        var node = Content(doc).First();
        Assert.Equal("heading", node.GetProperty("type").GetString());
        Assert.Equal(2, node.GetProperty("attrs").GetProperty("level").GetInt32());
    }

    [Fact]
    public void Bullets_become_bulletList()
    {
        var doc = Convert("- premier\n- second");
        var node = Content(doc).First();
        Assert.Equal("bulletList", node.GetProperty("type").GetString());
        Assert.Equal(2, node.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void Bold_text_gets_strong_mark()
    {
        var doc = Convert("Un point **important** ici.");
        var para = Content(doc).First();
        var inline = para.GetProperty("content").EnumerateArray().ToList();
        var bold = inline.Single(n => n.TryGetProperty("marks", out _));
        Assert.Equal("important", bold.GetProperty("text").GetString());
        Assert.Equal("strong", bold.GetProperty("marks")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Code_block_preserves_content_and_language()
    {
        var doc = Convert("```powershell\ndotnet build\n```");
        var node = Content(doc).First();
        Assert.Equal("codeBlock", node.GetProperty("type").GetString());
        Assert.Equal("powershell", node.GetProperty("attrs").GetProperty("language").GetString());
        Assert.Equal("dotnet build", node.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Table_becomes_table_with_header_row()
    {
        var doc = Convert("| Col A | Col B |\n|---|---|\n| a | b |");
        var table = Content(doc).First();
        Assert.Equal("table", table.GetProperty("type").GetString());
        var rows = table.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count); // séparateur ignoré
        Assert.Equal("tableHeader", rows[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("tableCell",   rows[1].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Empty_input_yields_valid_document()
    {
        var doc = Convert("");
        Assert.Equal("doc", doc.GetProperty("type").GetString());
        Assert.Equal(1, doc.GetProperty("version").GetInt32());
        Assert.True(doc.GetProperty("content").GetArrayLength() >= 1);
    }

    [Fact]
    public void Signature_line_survives_conversion()
    {
        var doc = Convert("Contenu.\n\n[agent: claude-chat | role: pilotage-cadrage | us: SERZ-1]");
        var json = JsonSerializer.Serialize(MarkdownToAdf.Convert("Contenu.\n\n[agent: claude-chat | role: pilotage-cadrage | us: SERZ-1]"));
        Assert.Contains("pilotage-cadrage", json);
    }
}
