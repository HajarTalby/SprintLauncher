namespace SprintLauncher.Config;

internal static class EnvFileLoader
{
    internal static void Load(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var idx = trimmed.IndexOf('=');
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
