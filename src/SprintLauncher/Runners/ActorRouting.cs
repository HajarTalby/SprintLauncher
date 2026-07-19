using SprintLauncher.Jira;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed record ActorRoutingIteration(
    ActorRole Role,
    IReadOnlyList<JiraIssue> PromptIssues,
    string PromptIssueKey,
    string PublishIssueKey,
    string ArtifactSuffix);

public static class ActorRouting
{
    public static IReadOnlyList<ActorRoutingIteration> BuildIterations(
        ActorRole role,
        IReadOnlyList<JiraIssue> issues,
        string referenceIssueKey)
    {
        if (role.UsesPerUsImplementationRouting())
        {
            return issues
                .Select(issue => new ActorRoutingIteration(
                    role,
                    [issue],
                    issue.Key,
                    issue.Key,
                    issue.Key))
                .ToArray();
        }

        return
        [
            new ActorRoutingIteration(
                role,
                issues,
                referenceIssueKey,
                referenceIssueKey,
                "sprint")
        ];
    }
}
