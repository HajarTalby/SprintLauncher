using System.Text.Json;

namespace SprintLauncher.Cadrage;

/// <summary>
/// US proposée par la session de cadrage (SERZENIA-143 lot 3).
/// La description doit suivre le template SERZENIA-89 ; la création Jira
/// n'a lieu qu'après validation explicite de l'approbatrice.
/// </summary>
public sealed record UsProposal(
    string Summary,
    string Description,
    List<string>? ReadyConditions = null);

public static class UsProposalParser
{
    public const string StartMarker = "===US-PROPOSALS===";
    public const string EndMarker = "===FIN-US-PROPOSALS===";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Extrait le bloc structuré d'US depuis la synthèse finale du cadrage.
    /// Tolérant : accepte le JSON entre marqueurs, avec ou sans fence ```json.
    /// Retourne une liste vide si le bloc est absent ou invalide (jamais d'exception).
    /// </summary>
    public static List<UsProposal> TryParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];

        var start = content.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return [];

        var end = content.IndexOf(EndMarker, start, StringComparison.OrdinalIgnoreCase);
        var section = end > start
            ? content[(start + StartMarker.Length)..end]
            : content[(start + StartMarker.Length)..];

        // Le JSON est le premier tableau [...] de la section (fences markdown ignorées)
        var open = section.IndexOf('[');
        var close = section.LastIndexOf(']');
        if (open < 0 || close <= open) return [];

        try
        {
            var proposals = JsonSerializer.Deserialize<List<UsProposal>>(
                section[open..(close + 1)], _json) ?? [];
            return proposals
                .Where(p => !string.IsNullOrWhiteSpace(p.Summary) && !string.IsNullOrWhiteSpace(p.Description))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Sections SERZENIA-89 absentes de la description (garde de conformité, non bloquante).</summary>
    public static List<string> MissingTemplateSections(string description)
    {
        string[] required =
        [
            "## Objectif", "## Contexte", "## Périmètre", "## Hors périmètre",
            "## Critères d'acceptation", "## Definition of Done", "## Scénarios de test",
        ];
        return required
            .Where(s => !description.Contains(s, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
