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
        @"out of credits|insufficient credits|usage cap|upgrade to continue|plan limit|" +
        // Message réel claude.exe constaté au run sprint 6 : "You've hit your session limit · resets 9:50pm"
        @"session limit|daily limit|hit your .{0,20}limit|limite (de session|journali[eè]re|atteinte)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsQuotaExhausted(string? output, string? errorOutput) =>
        (errorOutput is not null && Patterns.IsMatch(errorOutput)) ||
        (output is not null && Patterns.IsMatch(output));

    /// <summary>
    /// claude.exe peut imprimer le message de limite et sortir en code 0 : une sortie
    /// TRÈS courte contenant un motif de quota est un épuisement, pas un livrable
    /// (constaté : sortie de 66 caractères "You've hit your session limit…" en exit 0).
    /// </summary>
    public static bool IsQuotaExhaustedOutput(string? output) =>
        output is not null && output.Trim().Length < 300 && Patterns.IsMatch(output);
}

/// <summary>
/// Détecte une sortie d'implémentation qui s'est arrêtée à une demande de GO
/// (SERZENIA-143 lot 7) : en headless personne ne peut répondre — l'US ne doit
/// PAS être marquée implémentée sur une simple déclaration d'accès.
/// </summary>
public static class ImplementationOutputGuard
{
    private static readonly Regex AwaitingGo = new(
        @"sans go explicite|attends?\s+(le|un|ton)\s+go|r[ée]pond(re|ez)\s+.{0,40}\bGO\b|" +
        @"pour autoriser l'ex[ée]cution|en attente (de|du|d'un) go|awaiting (your )?go|" +
        @"je ne (lis|lance|modifie) .{0,60}sans (go|validation)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsAwaitingGo(string output) =>
        !string.IsNullOrWhiteSpace(output) && AwaitingGo.IsMatch(output);
}

/// <summary>Type d'une US pour la répartition front/backend entre moteurs.</summary>
public enum UsType { Unknown, Front, Backend }

/// <summary>
/// Classification front/backend d'une US par mots-clés (résumé + description).
/// Sert à séparer les périmètres de code entre ccode et codex — pas de
/// chevauchement quand un sprint mélange des US UI et des US backend.
/// </summary>
public static class UsTypeClassifier
{
    private static readonly Regex FrontPattern = new(
        @"\b(ui|ux|front(end)?|écran|ecran|screen|page|vue|view|xaml|interface|affichage|design|" +
        @"navigation|formulaire|form|bouton|button|css|style|layout|responsive|maquette)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BackendPattern = new(
        @"\b(api|back(end)?|service|domain|infrastructure|endpoint|repository|persistance|" +
        @"base de données|database|db|sql|firebase|firestore|auth(entification)?|sync|" +
        @"migration|modèle de données|serveur|server|batch|scheduler)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static UsType Classify(string summary, string description)
    {
        var text = $"{summary}\n{description}";
        int front = FrontPattern.Matches(text).Count;
        int back = BackendPattern.Matches(text).Count;

        if (front == 0 && back == 0) return UsType.Unknown;
        if (front == back) return UsType.Unknown; // mixte → alternance standard
        return front > back ? UsType.Front : UsType.Backend;
    }
}

/// <summary>
/// Répartition des US entre ccode et codex (SERZENIA-143 lot 5) :
/// 1. Spécialisation par type — les US front vont au moteur front, les US backend
///    au moteur backend (configurable via ENGINE_FRONT / ENGINE_BACK) → pas de
///    chevauchement de code entre moteurs sur un même sprint.
/// 2. Alternance stricte pour les US non typées / mixtes.
/// 3. Relève sur quota : un moteur épuisé est remplacé par l'autre, quelle que
///    soit la spécialisation (avancer prime sur la séparation).
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

    /// <summary>
    /// Choix par spécialisation : US front → moteur front, US backend → moteur backend,
    /// US non typée → alternance. Si le moteur attitré est à quota épuisé, l'autre prend
    /// le relais (la progression du sprint prime sur la séparation des périmètres).
    /// </summary>
    public static string? PickEngineForUs(
        UsType type,
        string? lastImplementer,
        IReadOnlySet<string> exhausted,
        string frontEngine,
        string backEngine)
    {
        var preferred = type switch
        {
            UsType.Front   => frontEngine,
            UsType.Backend => backEngine,
            _              => null,
        };

        if (preferred is not null)
            return !exhausted.Contains(preferred)
                ? preferred
                : PickRelief(preferred, exhausted);

        return PickEngine(lastImplementer, exhausted);
    }

    /// <summary>Le moteur de relève quand <paramref name="failedEngine"/> tombe sur quota.</summary>
    public static string? PickRelief(string failedEngine, IReadOnlySet<string> exhausted)
    {
        var other = failedEngine == ClaudeEngine ? CodexEngine : ClaudeEngine;
        return exhausted.Contains(other) ? null : other;
    }
}
