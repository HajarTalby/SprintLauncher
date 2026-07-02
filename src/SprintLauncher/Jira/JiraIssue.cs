namespace SprintLauncher.Jira;

public sealed record JiraIssue(
    string Key,
    string Summary,
    string Description,
    IReadOnlyList<JiraComment> Comments);
