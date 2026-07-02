namespace SprintLauncher.Jira;

public sealed record JiraComment(
    string Id,
    string Author,
    string Created,
    string Body);
