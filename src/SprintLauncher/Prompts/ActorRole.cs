namespace SprintLauncher.Prompts;

public enum SessionMode
{
    Execution, // default: Familles → Comité Pilotage → Arbitrage → QA
    Cadrage,   // Comité Pilotage first → Familles → Arbitrage → QA
}

public static class SessionModeExtensions
{
    // Pipeline vision SERZENIA-143 : cadrage → analyse → implémentation →
    // arbitrage (seulement si litige) → QA. L'arbitrage est convoqué sur
    // marqueur [LITIGE] détecté en analyse, jamais systématiquement.
    public static IReadOnlyList<ActorGroup> GetGroupOrder(this SessionMode mode) => mode switch
    {
        SessionMode.Cadrage => [
            ActorGroup.CommitteePilotage,
            ActorGroup.Analysis,
            ActorGroup.FamilyClaude,
            ActorGroup.FamilyGpt,
            ActorGroup.CommitteeArbitrage,
            ActorGroup.Qa,
            ActorGroup.Retrospective,
        ],
        _ => [
            ActorGroup.Analysis,
            ActorGroup.FamilyClaude,
            ActorGroup.FamilyGpt,
            ActorGroup.CommitteePilotage,
            ActorGroup.CommitteeArbitrage,
            ActorGroup.Qa,
            ActorGroup.Retrospective,
        ],
    };

    public static string ToLabel(this SessionMode mode) => mode switch
    {
        SessionMode.Cadrage   => "CADRAGE",
        SessionMode.Execution => "EXECUTION",
        _                     => mode.ToString().ToUpperInvariant(),
    };
}

public enum ActorRole
{
    // ── FAMILLE CLAUDE ──
    ClaudePilotage,
    ClaudeImplementation,

    // ── FAMILLE GPT ──
    GptImplementation,
    GptPilotage, // semi-manual: prompt generated, Hajar runs in ChatGPT web

    // ── FAMILLE AG ──
    AgImplementation,

    // ── ANALYSE (discussion claude-code + codex, per-US) ──
    AnalysisCcode,
    AnalysisCodex,

    // ── COMITÉ DE PILOTAGE ──
    CommitteePilotageClaudeChat,
    CommitteePilotageGptChat,

    // ── COMITÉ D'ARBITRAGE COMPLET ──
    CommitteeClaudeChat,
    CommitteeCcode,
    CommitteeGptChat,
    CommitteeCodex,

    // ── QA ──
    ClaudeQaVerdict,
    GptQaVerdict,

    // ── RÉTROSPECTIVE (SERZENIA-144 lot 4) : fin de run, un par moteur ──
    RetrospectiveClaude,
    RetrospectiveGpt,
}

public enum ActorGroup
{
    FamilyClaude,
    FamilyGpt,
    Analysis,
    CommitteePilotage,
    CommitteeArbitrage,
    Qa,
    Retrospective,
}

public static class ActorRoleExtensions
{
    public static ActorGroup GetGroup(this ActorRole role) => role switch
    {
        ActorRole.ClaudePilotage or ActorRole.ClaudeImplementation
            => ActorGroup.FamilyClaude,
        ActorRole.GptImplementation or ActorRole.GptPilotage
            => ActorGroup.FamilyGpt,
        ActorRole.AgImplementation
            => ActorGroup.FamilyGpt,
        ActorRole.AnalysisCcode or ActorRole.AnalysisCodex
            => ActorGroup.Analysis,
        ActorRole.CommitteePilotageClaudeChat or ActorRole.CommitteePilotageGptChat
            => ActorGroup.CommitteePilotage,
        ActorRole.CommitteeClaudeChat or ActorRole.CommitteeCcode
            or ActorRole.CommitteeGptChat or ActorRole.CommitteeCodex
            => ActorGroup.CommitteeArbitrage,
        ActorRole.ClaudeQaVerdict or ActorRole.GptQaVerdict
            => ActorGroup.Qa,
        ActorRole.RetrospectiveClaude or ActorRole.RetrospectiveGpt
            => ActorGroup.Retrospective,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static string GetGroupLabel(this ActorGroup group) => group switch
    {
        ActorGroup.FamilyClaude        => "── FAMILLE CLAUDE ──",
        ActorGroup.FamilyGpt           => "── FAMILLE GPT ──",
        ActorGroup.Analysis            => "── ANALYSE ──",
        ActorGroup.CommitteePilotage   => "── COMITÉ DE PILOTAGE ──",
        ActorGroup.CommitteeArbitrage  => "── COMITÉ D'ARBITRAGE COMPLET ──",
        ActorGroup.Qa                  => "── QA ──",
        ActorGroup.Retrospective       => "── RÉTROSPECTIVE ──",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, null),
    };

    public static bool IsClaudeFamily(this ActorRole role) => role is
        ActorRole.ClaudePilotage or
        ActorRole.ClaudeImplementation or
        ActorRole.AnalysisCcode or
        ActorRole.CommitteePilotageClaudeChat or
        ActorRole.CommitteeClaudeChat or
        ActorRole.CommitteeCcode or
        ActorRole.ClaudeQaVerdict or
        ActorRole.RetrospectiveClaude;

    public static bool IsSemiManual(this ActorRole role) => role is ActorRole.GptPilotage;

    public static bool IsAgFamily(this ActorRole role) => role is ActorRole.AgImplementation;

    public static bool IsPilotageActor(this ActorRole role) => role is
        ActorRole.ClaudePilotage or ActorRole.GptPilotage;

    public static bool UsesPerUsImplementationRouting(this ActorRole role) =>
        role is ActorRole.ClaudeImplementation or ActorRole.GptImplementation or ActorRole.AgImplementation;

    // Pilotage actors publish their sprint-level analysis to the reference ticket only.
    // Implementation actors are routed per-US by the implementation scheduler.
    public static bool PublishesToReferenceTicketOnly(this ActorRole role) =>
        role.IsPilotageActor();

    public static bool IsCollective(this ActorRole role) =>
        role.GetGroup() is ActorGroup.Analysis
            or ActorGroup.CommitteePilotage
            or ActorGroup.CommitteeArbitrage
            or ActorGroup.Qa;

    public static bool NeedsReadOnlySandbox(this ActorRole role) => role is
        ActorRole.GptPilotage or   // pilotage automatisé via codex : lecture seule
        ActorRole.AnalysisCodex or // l'analyse ne modifie rien
        ActorRole.CommitteePilotageGptChat or
        ActorRole.CommitteeGptChat or
        ActorRole.CommitteeCodex;
        // GptQaVerdict retiré : la QA EXÉCUTE elle-même les scénarios sur la release
        // réelle (lancement app, captures, vidéos) — décision de Hajar (2026-07-14).

    /// <summary>
    /// Rôles qui exécutent réellement (code, commandes, app) : implémentation ET QA.
    /// La QA ne se contente pas de lire des preuves — elle déroule les scénarios
    /// elle-même sur la release réelle (demande de Hajar).
    /// </summary>
    public static bool IsExecutionRole(this ActorRole role) => role is
        ActorRole.ClaudeImplementation or ActorRole.GptImplementation or ActorRole.AgImplementation or
        ActorRole.ClaudeQaVerdict or ActorRole.GptQaVerdict;

    public static string ToSignatureTag(this ActorRole role) => role switch
    {
        ActorRole.ClaudePilotage             => "claude-chat | role: pilotage-cadrage",
        ActorRole.ClaudeImplementation       => "claude-code | role: implementation",
        ActorRole.GptPilotage                => "gpt-chat | role: pilotage-cadrage",
        ActorRole.GptImplementation          => "codex | role: implementation",
        ActorRole.AgImplementation           => "agy | role: implementation",
        ActorRole.AnalysisCcode              => "claude-code | role: analyse",
        ActorRole.AnalysisCodex              => "codex | role: analyse",
        ActorRole.CommitteePilotageClaudeChat => "claude-chat | role: comite-pilotage",
        ActorRole.CommitteePilotageGptChat   => "gpt-chat | role: comite-pilotage",
        ActorRole.CommitteeClaudeChat        => "claude-chat | role: comite-arbitrage",
        ActorRole.CommitteeCcode             => "claude-code | role: comite-arbitrage",
        ActorRole.CommitteeGptChat           => "gpt-chat | role: comite-arbitrage",
        ActorRole.CommitteeCodex             => "codex | role: comite-arbitrage",
        ActorRole.ClaudeQaVerdict            => "claude-chat | role: qa-verdict",
        ActorRole.GptQaVerdict               => "gpt-chat | role: qa-verdict",
        ActorRole.RetrospectiveClaude        => "claude-code | role: retrospective",
        ActorRole.RetrospectiveGpt           => "codex | role: retrospective",
        _                                    => role.ToString().ToLowerInvariant(),
    };

    public static string ToGroupSignatureTag(this ActorGroup group) => group switch
    {
        ActorGroup.Analysis           => "analyse",
        ActorGroup.CommitteePilotage  => "comite-pilotage",
        ActorGroup.CommitteeArbitrage => "comite-arbitrage",
        ActorGroup.Qa                 => "qa-verdict",
        _ => group.ToString().ToLowerInvariant(),
    };
}
