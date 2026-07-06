using System.Text.RegularExpressions;

namespace SprintLauncher.Runners;

/// <summary>
/// Détection d'épuisement de quota des CLIs claude/codex (SERZENIA-143 lot 5).
/// Patterns empiriques à maintenir — un échec quota est distingué d'un échec
/// technique pour déclencher la relève par l'autre moteur au lieu d'un abandon.
/// </summary>
public static class QuotaDetector
{
    private static readonly Regex Patterns = new(
        @"rate.?limit|usage.?limit|quota|too many requests|\b429\b|limit (?:has been )?reached|" +
        @"out of credits|insufficient credits|usage cap|upgrade to continue|plan limit",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsQuotaExhausted(string? output, string? errorOutput) =>
        (errorOutput is not null && Patterns.IsMatch(errorOutput)) ||
        (output is not null && Patterns.IsMatch(output));
}

/// <summary>
/// Tour de rôle ccode/codex par US avec relève sur quota (SERZENIA-143 lot 5).
/// Logique pure, testable sans subprocess.
/// </summary>
public static class ImplementationRotation
{
    public static readonly string ClaudeEngine = "ClaudeImplementation";
    public static readonly string CodexEngine = "GptImplementation";

    /// <summary>
    /// Choisit le moteur pour la prochaine US : alternance stricte avec le dernier
    /// utilisé, en écartant les moteurs à quota épuisé. Null si plus aucun moteur.
    /// </summary>
    public static string? PickEngine(string? lastImplementer, IReadOnlySet<string> exhausted)
    {
        var order = lastImplementer == ClaudeEngine
            ? new[] { CodexEngine, ClaudeEngine }
            : new[] { ClaudeEngine, CodexEngine };

        foreach (var engine in order)
            if (!exhausted.Contains(engine))
                return engine;
        return null;
    }

    /// <summary>Le moteur de relève quand <paramref name="failedEngine"/> tombe sur quota.</summary>
    public static string? PickRelief(string failedEngine, IReadOnlySet<string> exhausted)
    {
        var other = failedEngine == ClaudeEngine ? CodexEngine : ClaudeEngine;
        return exhausted.Contains(other) ? null : other;
    }
}
