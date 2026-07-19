using SprintLauncher.Jira;
using SprintLauncher.Memory;
using SprintLauncher.Prompts;
using Xunit;

namespace SprintLauncher.Tests;

public sealed class MemorySyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SprintLauncher.MemorySyncTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_treats_knowledge_memories_as_shared_capital()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "practice.md"), "---\ntype: feedback\n---\nToujours creuser la cause racine");
        File.WriteAllText(Path.Combine(memoryDir, "pointer.md"), "---\ntype: reference\n---\nTableau de bord");
        File.WriteAllText(Path.Combine(memoryDir, "agent-flag.md"), "---\ntype: project\ninject_to_agents: true\n---\nA partager partout");
        File.WriteAllText(Path.Combine(memoryDir, "MEMORY.md"), "---\ntype: feedback\n---\nIndex content");

        var result = MemorySync.Load(_root, context: "serzenia");

        // Le savoir suit tous les contextes : c'est ce qu'on capitalise.
        Assert.Equal(["agent-flag", "pointer", "practice"], result.Entries.Select(e => e.Name).Order());
        Assert.All(result.Entries, e => Assert.True(e.IsShared));
        Assert.DoesNotContain(result.Entries, e => e.Name == "MEMORY");
    }

    [Fact]
    public void Load_keeps_dev_state_inside_its_own_context()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "sl-state.md"), "---\ntype: project\nscope: sprint-launcher\n---\nEtat du dev du launcher");
        File.WriteAllText(Path.Combine(memoryDir, "product-state.md"), "---\ntype: project\nscope: serzenia\n---\nEtat du produit");

        var serzenia = MemorySync.Load(_root, context: "serzenia");
        var launcher = MemorySync.Load(_root, context: "sprint-launcher");

        // Regression du run sprint 6 (2026-07-19) : l'etat du dev du launcher se
        // retrouvait dans les prompts des acteurs qui analysaient des tickets produit.
        Assert.Equal(["product-state"], serzenia.Entries.Select(e => e.Name));
        Assert.Equal(["sl-state"], launcher.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Load_excludes_unscoped_dev_state_rather_than_leaking_it()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "orphan.md"), "---\ntype: project\n---\nEtat non rattache a un chantier");

        var result = MemorySync.Load(_root, context: "serzenia");

        // Defaut sur : mieux vaut manquer de contexte que le voir fuiter ailleurs.
        Assert.False(result.HasEntries);
    }

    [Fact]
    public void Load_skips_gracefully_when_memory_directory_is_absent()
    {
        var result = MemorySync.Load(_root, context: "serzenia");

        Assert.False(result.HasEntries);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Load_skips_malformed_frontmatter_without_failing()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "bad.md"), "---\ntype: feedback\nMissing closing marker");
        File.WriteAllText(Path.Combine(memoryDir, "good.md"), "---\ntype: feedback\n---\nUsable guidance");

        var result = MemorySync.Load(_root, context: "serzenia");

        Assert.Single(result.Entries);
        Assert.Equal("good", result.Entries[0].Name);
    }

    [Fact]
    public void Load_includes_entries_with_inject_to_agents_true_even_without_type()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "agent-only.md"), "---\ninject_to_agents: true\n---\nAgent-only guidance");

        var result = MemorySync.Load(_root, context: "serzenia");

        Assert.Single(result.Entries);
        Assert.Equal("agent-only", result.Entries[0].Name);
    }

    [Fact]
    public void BuildPromptSection_separates_shared_knowledge_from_current_context()
    {
        var ctx = new AgentMemoryContext(
            [
                new AgentMemoryEntry("practice", "feedback", MemorySync.SharedScope, "Regle durable"),
                new AgentMemoryEntry("state", "project", "serzenia", "Etat du chantier"),
            ],
            HasEntries: true);

        var section = RemoveDiacritics(MemorySync.BuildPromptSection(ctx));

        var sharedAt = section.IndexOf("Regles et pratiques acquises", StringComparison.Ordinal);
        var scopedAt = section.IndexOf("Contexte du chantier en cours", StringComparison.Ordinal);
        Assert.True(sharedAt >= 0 && scopedAt > sharedAt);
        Assert.Contains("Regle durable", section);
        Assert.Contains("Etat du chantier", section);
    }

    [Fact]
    public void PromptBuilder_injects_memory_into_final_user_prompt()
    {
        var issue = new JiraIssue("SERZENIA-142", "Memory", "Ticket body", []);
        var memory = new AgentMemoryContext(
            [new AgentMemoryEntry("project-entry", "project", "serzenia", "Project memory line")],
            HasEntries: true);
        var builder = new PromptBuilder("SERZENIA", "Hajar");

        var prompt = builder.Build(
            ActorRole.GptImplementation,
            [issue],
            issue.Key,
            memory: memory);

        Assert.Contains("## Contexte du chantier en cours", RemoveDiacritics(prompt.UserPrompt));
        Assert.Contains("### project-entry", prompt.UserPrompt);
        Assert.Contains("Project memory line", prompt.UserPrompt);
    }

    private string CreateMemoryDir()
    {
        var memoryDir = Path.Combine(_root, "memory");
        Directory.CreateDirectory(memoryDir);
        return memoryDir;
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC);
    }
}
