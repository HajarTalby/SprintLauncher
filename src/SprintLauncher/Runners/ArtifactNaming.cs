using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public static class ArtifactNaming
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars()
        .Concat(['/', '\\', ':'])
        .Distinct()
        .ToArray();

    public static string Prompt(ActorRole role) => $"prompt-{role}.txt";

    public static string Output(ActorRole role) => $"output-{role}.txt";

    public static string PerUsOutput(ActorRole role, string key) =>
        $"output-{role}-{SanitizeSegment(key)}.txt";

    public static string ReviewOutput(string key) =>
        $"output-Review-{SanitizeSegment(key)}.txt";

    public static string ReviewCorrectionsOutput(ActorRole role, string key) =>
        $"output-{role}-{SanitizeSegment(key)}-corrections.txt";

    public static string AnalysisOutput(string key) =>
        $"output-Analysis-{SanitizeSegment(key)}.txt";

    public static string SanitizeSegment(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return "item";

        var chars = trimmed
            .Select(c => InvalidFileNameChars.Contains(c) || char.IsControl(c) ? '_' : c)
            .ToArray();
        var sanitized = new string(chars).Trim('.', ' ');
        return sanitized.Length == 0 ? "item" : sanitized;
    }

    public static IReadOnlyList<T> OrderedByStableKey<T>(IEnumerable<T> items, Func<T, string> keySelector) =>
        items.OrderBy(keySelector, StringComparer.OrdinalIgnoreCase).ToArray();
}
