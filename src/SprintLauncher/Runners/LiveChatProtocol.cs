using System.Text.Json;

namespace SprintLauncher.Runners;

/// <summary>
/// Cadrage des messages injectés EN COURS DE TOUR (chat live — retour de Hajar,
/// 2026-07-16 : « tu codes et je t'envoie une intervention que tu lis au fur et à
/// mesure »). Fonctions pures, testables sans binaire.
///
/// Claude : <c>claude -p --input-format stream-json</c> lit sur stdin une suite de
/// messages utilisateur JSON, un par ligne, et répond au fil de l'eau. Le prompt
/// initial ET les interventions suivantes empruntent ce même canal.
///
/// ⚠ NON ENCORE VALIDÉ CONTRE LES BINAIRES (quota épuisé au moment de l'écriture).
/// Le smoke live est requis avant release — cf. LiveChatSmoke.
/// </summary>
public static class LiveChatProtocol
{
    private static readonly JsonSerializerOptions _compact = new()
    {
        // stdin ligne par ligne : jamais de retour à la ligne dans le JSON sérialisé.
        WriteIndented = false,
    };

    /// <summary>
    /// Un message utilisateur pour le mode stream-json de claude, sérialisé sur UNE ligne.
    /// Forme : {"type":"user","message":{"role":"user","content":[{"type":"text","text":"…"}]}}
    /// </summary>
    public static string ClaudeUserMessage(string text)
    {
        var payload = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new[] { new { type = "text", text = text ?? "" } },
            },
        };
        return JsonSerializer.Serialize(payload, _compact);
    }

    /// <summary>
    /// Message pour codex app-server (JSON-RPC 2.0, une notification par ligne).
    /// ⚠ EXPÉRIMENTAL : le nom de méthode exact reste à confirmer contre le binaire.
    /// La structure JSON-RPC, elle, est stable ; seule la méthode est une hypothèse.
    /// </summary>
    public static string CodexUserMessage(string text, int id)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method = "session/user_input",
            @params = new { text = text ?? "" },
        };
        return JsonSerializer.Serialize(payload, _compact);
    }

    /// <summary>Le texte est-il exploitable comme intervention (non vide) ?</summary>
    public static bool IsSendable(string? text) => !string.IsNullOrWhiteSpace(text);
}
