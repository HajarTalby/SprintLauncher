using SprintLauncher.Jira;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class ActorRoutingTests
{
    private static JiraIssue Issue(string key) =>
        new(key, $"Summary {key}", $"Description only for {key}", []);

    [Fact]
    public void Implementation_routing_for_one_ticket_uses_that_ticket_only()
    {
        var issue = Issue("SERZENIA-110");

        var routes = ActorRouting.BuildIterations(
            ActorRole.ClaudeImplementation,
            [issue],
            "SERZENIA-110");

        var route = Assert.Single(routes);
        Assert.Equal("SERZENIA-110", route.PromptIssueKey);
        Assert.Equal("SERZENIA-110", route.PublishIssueKey);
        Assert.Same(issue, Assert.Single(route.PromptIssues));
    }

    [Fact]
    public void Implementation_routing_for_many_tickets_targets_each_us_with_isolated_prompt_context()
    {
        var issues = new[] { Issue("SERZENIA-110"), Issue("SERZENIA-111"), Issue("SERZENIA-112") };
        var builder = new PromptBuilder();

        var routes = ActorRouting.BuildIterations(
            ActorRole.GptImplementation,
            issues,
            "SERZENIA-110");

        Assert.Equal(["SERZENIA-110", "SERZENIA-111", "SERZENIA-112"], routes.Select(r => r.PromptIssueKey));
        Assert.Equal(["SERZENIA-110", "SERZENIA-111", "SERZENIA-112"], routes.Select(r => r.PublishIssueKey));

        foreach (var route in routes)
        {
            var prompt = builder.Build(route.Role, route.PromptIssues, route.PromptIssueKey);

            Assert.Contains($"us: {route.PublishIssueKey}", prompt.SystemPrompt);
            Assert.Contains($"## [{route.PublishIssueKey}]", prompt.UserPrompt);

            foreach (var other in issues.Where(i => i.Key != route.PublishIssueKey))
            {
                Assert.DoesNotContain($"## [{other.Key}]", prompt.UserPrompt);
                Assert.DoesNotContain($"Description only for {other.Key}", prompt.UserPrompt);
            }
        }
    }

    [Fact]
    public void Pilotage_routing_stays_sprint_level_on_reference_ticket()
    {
        var issues = new[] { Issue("SERZENIA-110"), Issue("SERZENIA-111"), Issue("SERZENIA-112") };

        var routes = ActorRouting.BuildIterations(
            ActorRole.GptPilotage,
            issues,
            "SERZENIA-110");

        var route = Assert.Single(routes);
        Assert.Equal("SERZENIA-110", route.PromptIssueKey);
        Assert.Equal("SERZENIA-110", route.PublishIssueKey);
        Assert.Equal(issues, route.PromptIssues);
        Assert.True(ActorRole.GptPilotage.PublishesToReferenceTicketOnly());
        Assert.False(ActorRole.GptImplementation.PublishesToReferenceTicketOnly());
    }
}
