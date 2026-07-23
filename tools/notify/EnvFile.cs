namespace SprintLauncher.Notify;

public sealed record EnvFileResolution(string Path, bool Exists);

public static class EnvFile
{
    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;

            var idx = trimmed.IndexOf('=');
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            if (key.Length == 0) continue;

            values[key] = Unquote(value);
        }

        return values;
    }

    public static EnvFileResolution? Resolve(
        string? sprintLauncherHome,
        string applicationBaseDirectory,
        string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(sprintLauncherHome))
        {
            return FromRoot(Environment.ExpandEnvironmentVariables(sprintLauncherHome.Trim()));
        }

        // Layout release : le .env est déposé à côté de notify.exe (pas de .sln à remonter).
        var beside = Path.GetFullPath(Path.Combine(applicationBaseDirectory, ".env"));
        if (File.Exists(beside))
        {
            return new EnvFileResolution(beside, true);
        }

        var installationRoot = FindSprintLauncherRoot(applicationBaseDirectory);
        if (installationRoot is not null)
        {
            return FromRoot(installationRoot);
        }

        var currentRoot = FindSprintLauncherRoot(currentDirectory);
        if (currentRoot is not null)
        {
            return FromRoot(currentRoot);
        }

        return null;
    }

    private static EnvFileResolution FromRoot(string root)
    {
        var path = Path.GetFullPath(Path.Combine(root, ".env"));
        return new EnvFileResolution(path, File.Exists(path));
    }

    private static string? FindSprintLauncherRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory)) return null;

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SprintLauncher.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
