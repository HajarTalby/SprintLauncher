using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed record ActorRunResult(
    ActorRole Role,
    bool Success,
    string Output,
    string? ErrorOutput,
    int ExitCode,
    bool IsSemiManual);
