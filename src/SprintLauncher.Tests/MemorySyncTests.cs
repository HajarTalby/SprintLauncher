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
    public void Load_includes_project_entries_and_explicit_agent_injection_only()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "project-entry.md"), "---\ntype: project\n---\nProject guidance");
        File.WriteAllText(Path.Combine(memoryDir, "agent-flag.md"), "---\ntype: feedback\ninject_to_agents: true\n---\nAgent guidance");
        File.WriteAllText(Path.Combine(memoryDir, "feedback-only.md"), "---\ntype: feedback\n---\nClaude Code feedback");
        File.WriteAllText(Path.Combine(memoryDir, "MEMORY.md"), "---\ntype: project\n---\nIndex content");

        var result = MemorySync.Load(_root);

        Assert.True(result.HasEntries);
        Assert.Equal(["agent-flag", "project-entry"], result.Entries.Select(e => e.Name).Order());
        Assert.DoesNotContain(result.Entries, e => e.Name == "feedback-only");
        Assert.DoesNotContain(result.Entries, e => e.Name == "MEMORY");
    }

    [Fact]
    public void Load_skips_gracefully_when_memory_directory_is_absent()
    {
        var result = MemorySync.Load(_root);

        Assert.False(result.HasEntries);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Load_skips_malformed_frontmatter_without_failing()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "bad.md"), "---\ntype: project\nMissing closing marker");
        File.WriteAllText(Path.Combine(memoryDir, "good.md"), "---\ntype: project\n---\nUsable guidance");

        var result = MemorySync.Load(_root);

        Assert.Single(result.Entries);
        Assert.Equal("good", result.Entries[0].Name);
    }

    [Fact]
    public void Load_includes_entries_with_inject_to_agents_true_even_without_type()
    {
        var memoryDir = CreateMemoryDir();
        File.WriteAllText(Path.Combine(memoryDir, "agent-only.md"), "---\ninject_to_agents: true\n---\nAgent-only guidance");

        var result = MemorySync.Load(_root);

        Assert.Single(result.Entries);
        Assert.Equal("agent-only", result.Entries[0].Name);
    }

    [Fact]
    public void PromptBuilder_injects_memory_into_final_user_prompt()
    {
        var issue = new JiraIssue("SERZENIA-142", "Memory", "Ticket body", []);
        var memory = new AgentMemoryContext(
            [new AgentMemoryEntry("project-entry", "project", "Project memory line")],
            HasEntries: true);
        var builder = new PromptBuilder("SERZENIA", "Hajar");

        var prompt = builder.Build(
            ActorRole.GptImplementation,
            [issue],
            issue.Key,
            memory: memory);

        Assert.Contains("## Memoire projet SERZENIA", RemoveDiacritics(prompt.UserPrompt));
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
