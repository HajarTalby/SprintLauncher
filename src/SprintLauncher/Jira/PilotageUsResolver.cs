using System.Text.RegularExpressions;

namespace SprintLauncher.Jira;

/// <summary>
/// Identifie l'US de pilotage d'un sprint : celle qui porte la synthèse sprint-level
/// (délibération du comité de pilotage, verdict QA collectif).
///
/// Historiquement c'était `issueKeys[0]` — le premier ticket de la liste du sprint.
/// Convention implicite, jamais vérifiée : au sprint 6 la liste commençait par
/// SERZENIA-98, donc la délibération du comité est partie sur la 98 alors que l'US de
/// pilotage était SERZENIA-111 (constat de Hajar, 2026-07-16).
///
/// L'information est dans Jira et n'a pas à être déclarée à la main : l'US de pilotage
/// s'appelle « Pilotage Sprint N » et sa description s'ouvre sur le même titre.
/// </summary>
public static partial class PilotageUsResolver
{
    [GeneratedRegex(@"^\s*pilotage\b", RegexOptions.IgnoreCase)]
    private static partial Regex PilotageSummary();

    private static Regex PilotageForSprint(string sprint) =>
        new(@"^\s*pilotage\s+sprint\s*" + Regex.Escape(sprint) + @"\b", RegexOptions.IgnoreCase);

    public sealed record Resolution(string Key, string Reason, bool IsFallback);

    /// <summary>
    /// Résout l'US de pilotage parmi les issues du sprint.
    /// Priorité : (1) clé forcée explicitement ; (2) « Pilotage Sprint &lt;N&gt; » exact ;
    /// (3) une seule US « Pilotage … » ; (4) repli sur la première clé, signalé comme tel.
    /// </summary>
    public static Resolution Resolve(
        IReadOnlyList<JiraIssue> issues,
        IReadOnlyList<string> issueKeys,
        string? sprintId = null,
        string? forcedKey = null)
    {
        if (!string.IsNullOrWhiteSpace(forcedKey))
        {
            var forced = forcedKey.Trim().ToUpperInvariant();
            return new Resolution(forced, "forcée explicitement (--pilotage-us / PILOTAGE_US)", IsFallback: false);
        }

        // « Pilotage Sprint 6 » — le cas nominal, sans ambiguïté possible.
        if (!string.IsNullOrWhiteSpace(sprintId))
        {
            var exact = issues.Where(i => PilotageForSprint(sprintId!).IsMatch(i.Summary ?? "")).ToList();
            if (exact.Count == 1)
                return new Resolution(exact[0].Key, $"US « {exact[0].Summary} » (titre Jira)", IsFallback: false);
        }

        // À défaut, une seule US « Pilotage … » dans le périmètre.
        var loose = issues.Where(i => PilotageSummary().IsMatch(i.Summary ?? "")).ToList();
        if (loose.Count == 1)
            return new Resolution(loose[0].Key, $"US « {loose[0].Summary} » (titre Jira)", IsFallback: false);

        if (loose.Count > 1)
        {
            var keys = string.Join(", ", loose.Select(i => i.Key));
            return new Resolution(issueKeys[0],
                $"plusieurs US de pilotage candidates ({keys}) — impossible de trancher", IsFallback: true);
        }

        return new Resolution(issueKeys[0],
            "aucune US « Pilotage … » trouvée dans le périmètre du sprint", IsFallback: true);
    }
}
