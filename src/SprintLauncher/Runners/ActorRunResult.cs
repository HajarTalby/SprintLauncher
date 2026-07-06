using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed record ActorRunResult(
    ActorRole Role,
    bool Success,
    string Output,
    string? ErrorOutput,
    int ExitCode,
    bool IsSemiManual,
    // Échec par épuisement de quota (SERZENIA-143 lot 5) — déclenche la relève
    // par l'autre moteur au lieu d'un abandon.
    bool IsQuotaExhausted = false);
