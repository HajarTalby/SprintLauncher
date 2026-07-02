namespace SprintLauncher.Runners;

public static class BinaryLocator
{
    /// <summary>
    /// Finds claude.exe — prefers the Desktop App install, falls back to PATH.
    /// Never hardcodes the versioned subfolder.
    /// </summary>
    public static string? FindClaude()
    {
        // Override via env var
        var envOverride = Environment.GetEnvironmentVariable("CLAUDE_BIN");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return envOverride;

        // Claude Desktop App on Windows: LocalAppData\Packages\Claude_*\LocalCache\Roaming\Claude\claude-code\*\claude.exe
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesDir = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesDir))
        {
            foreach (var claudePackage in Directory.GetDirectories(packagesDir, "Claude_*"))
            {
                var claudeCodeRoot = Path.Combine(claudePackage, "LocalCache", "Roaming", "Claude", "claude-code");
                if (!Directory.Exists(claudeCodeRoot)) continue;
                // Pick the latest version folder
                var versionDir = Directory.GetDirectories(claudeCodeRoot)
                    .OrderByDescending(d => d)
                    .FirstOrDefault();
                if (versionDir is null) continue;
                var candidate = Path.Combine(versionDir, "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Fallback: PATH
        return FindOnPath("claude");
    }

    /// <summary>
    /// Finds codex.exe — prefers the VS Code extension bundle, falls back to PATH.
    /// Never hardcodes the versioned extension folder.
    /// </summary>
    public static string? FindCodex()
    {
        // Override via env var
        var envOverride = Environment.GetEnvironmentVariable("CODEX_BIN");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
            return envOverride;

        // VS Code extension: .vscode\extensions\openai.chatgpt-*\bin\windows-x86_64\codex.exe
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extensionsDir = Path.Combine(userProfile, ".vscode", "extensions");
        if (Directory.Exists(extensionsDir))
        {
            foreach (var ext in Directory.GetDirectories(extensionsDir, "openai.chatgpt-*"))
            {
                var candidate = Path.Combine(ext, "bin", "windows-x86_64", "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Fallback: PATH
        return FindOnPath("codex");
    }

    private static string? FindOnPath(string name)
    {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".exe";
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
