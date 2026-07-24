namespace SprintLauncher.CodexWatcher;

public static class SessionFilter
{
    public static bool ShouldNotify(SessionMeta? sessionMeta)
    {
        if (sessionMeta is null ||
            !string.Equals(sessionMeta.Originator, "codex_vscode", StringComparison.Ordinal) ||
            !string.Equals(sessionMeta.ThreadSource, "user", StringComparison.Ordinal) ||
            sessionMeta.HasSubagent)
            return false;

        return !sessionMeta.Cwd
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith("SL-", StringComparison.OrdinalIgnoreCase));
    }
}
