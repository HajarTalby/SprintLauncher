using System.Text.RegularExpressions;

namespace SprintLauncher.Dialogue;

/// <summary>
/// Découpage de la synthèse d'analyse par US et détection de litige (SERZENIA-143 lot 4).
/// La synthèse est structurée en sections '## ANALYSE &lt;CLE&gt;' (une par ticket) ;
/// chaque section est publiée sur son US, la synthèse complète sur le ticket pilotage.
/// </summary>
public static class AnalysisSections
{
    public const string LitigeMarker = "[LITIGE";

    public static bool HasLitige(string text) =>
        text.Contains(LitigeMarker, StringComparison.OrdinalIgnoreCase);

    public static string ExtractLitige(string text)
    {
        var m = Regex.Match(text, @"\[LITIGE:?\s*([^\]]*)\]", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Trim().Length > 0
            ? m.Groups[1].Value.Trim()
            : "litige non détaillé";
    }

    /// <summary>
    /// Garde de couverture (SERZENIA-143 lot 7) : la conclusion d'une session d'analyse
    /// n'est acceptable que si CHAQUE US du scope a sa section '## ANALYSE &lt;KEY&gt;'.
    /// Retourne null si complet, sinon le message de refus listant les US manquantes.
    /// </summary>
    public static string? ValidateCoverage(string text, IReadOnlyList<string> keys)
    {
        var covered = Split(text, keys);
        var missing = keys.Where(k => !covered.ContainsKey(k)).ToList();
        return missing.Count == 0
            ? null
            : $"la section '## ANALYSE <KEY>' manque pour {missing.Count} US : {string.Join(", ", missing)}.";
    }

    public static Dictionary<string, string> Split(string text, IReadOnlyList<string> keys)
    {
        var sections = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var m = Regex.Match(
                text,
                $@"##\s*ANALYSE\s+{Regex.Escape(key)}\s*\n(.*?)(?=\n##\s|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Trim().Length >= 20)
                sections[key] = m.Groups[1].Value.Trim();
        }
        return sections;
    }
}
