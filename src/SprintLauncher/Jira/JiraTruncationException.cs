namespace SprintLauncher.Jira;

public sealed class JiraTruncationException(string issueKey, int expected, int actual)
    : InvalidOperationException(
        $"Anti-troncature guard triggered for {issueKey}: " +
        $"expected {expected} comments, fetched {actual}. " +
        $"Aborting to avoid silent data loss.");
