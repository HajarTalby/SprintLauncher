using System.Text.Json;

namespace SprintLauncher.Runners;

/// <summary>
/// Cadrage des messages JSON-RPC 2.0 du protocole `codex app-server`, dérivé des
/// schémas générés hors ligne par `codex app-server generate-json-schema`
/// (donc SANS consommer de quota). Fonctions pures, testables.
///
/// Séquence d'un tour codex piloté :
///   1. initialize            { clientInfo:{name,version} }
///   2. thread/start          { cwd, sandbox?, approvalPolicy? }        → result.thread.id
///   3. turn/start            { threadId, input:[{type:text,text}] }    → result.turn.id
///   4. flux de notifications : item/agentMessage/delta (texte au fil de l'eau),
///      turn/completed (fin du tour).
///   5. INJECTION LIVE : turn/steer { threadId, expectedTurnId, input:[…] }
///      — c'est le canal qui permet à Hajar de parler à codex PENDANT qu'il travaille
///      (équivalent du chat de l'extension VS Code).
///
/// ⚠ Le protocole est marqué expérimental par codex et n'a PAS été validé bout en bout
/// contre le binaire (quota épuisé à l'écriture, 2026-07-16). Les FORMES de message
/// viennent des schémas officiels ; le comportement runtime reste à confirmer au
/// smoke live (cf. LiveChatSmoke).
/// </summary>
public static class CodexAppServerProtocol
{
    private static readonly JsonSerializerOptions _compact = new() { WriteIndented = false };

    public static string Initialize(int id, string clientName, string clientVersion) =>
        Request(id, "initialize", new { clientInfo = new { name = clientName, version = clientVersion } });

    public static string ThreadStart(int id, string cwd, bool readOnly, bool bypassApprovals) =>
        Request(id, "thread/start", new
        {
            cwd,
            sandbox = readOnly ? "read-only" : (bypassApprovals ? "danger-full-access" : "workspace-write"),
            approvalPolicy = bypassApprovals ? "never" : "on-request",
        });

    public static string TurnStart(int id, string threadId, string text) =>
        Request(id, "turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text = text ?? "" } },
        });

    /// <summary>Injection live : oriente le tour ACTIF avec un nouveau message utilisateur.</summary>
    public static string TurnSteer(int id, string threadId, string expectedTurnId, string text) =>
        Request(id, "turn/steer", new
        {
            threadId,
            expectedTurnId,
            input = new[] { new { type = "text", text = text ?? "" } },
        });

    public static string TurnInterrupt(int id, string threadId, string turnId) =>
        Request(id, "turn/interrupt", new { threadId, turnId });

    private static string Request(int id, string method, object @params) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }, _compact);

    // ── Lecture des messages entrants ────────────────────────────────────────────

    public sealed record Incoming(
        string? Method,     // notification/requête : nom de méthode ; null pour une réponse
        int? Id,            // réponse/requête : id JSON-RPC
        JsonElement Root);

    /// <summary>Parse une ligne JSON-RPC ; null si la ligne n'est pas du JSON exploitable.</summary>
    public static Incoming? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) return null;
        try
        {
            var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            int? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number
                ? i.GetInt32() : null;
            return new Incoming(method, id, root.Clone());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Extrait result.thread.id d'une réponse à thread/start.</summary>
    public static string? ThreadId(JsonElement root) =>
        root.TryGetProperty("result", out var r) && r.TryGetProperty("thread", out var t)
        && t.TryGetProperty("id", out var id) ? id.GetString() : null;

    /// <summary>Extrait result.turn.id d'une réponse à turn/start.</summary>
    public static string? TurnId(JsonElement root) =>
        root.TryGetProperty("result", out var r) && r.TryGetProperty("turn", out var t)
        && t.TryGetProperty("id", out var id) ? id.GetString() : null;

    /// <summary>Delta de texte d'une notification item/agentMessage/delta.</summary>
    public static string? AgentDelta(string? method, JsonElement root)
    {
        if (method != "item/agentMessage/delta") return null;
        return root.TryGetProperty("params", out var p) && p.TryGetProperty("delta", out var d)
            ? d.GetString() : null;
    }

    /// <summary>
    /// itemId porté par une notification (delta, item/started…). Sert à insérer un
    /// séparateur de paragraphe entre deux items : sans lui, les messages successifs
    /// de l'agent se collent en un seul bloc illisible (retour de Hajar, 2026-07-17).
    /// </summary>
    public static string? ItemId(JsonElement root) =>
        root.TryGetProperty("params", out var p) && p.TryGetProperty("itemId", out var id)
            ? id.GetString() : null;

    public static bool IsTurnCompleted(string? method) => method == "turn/completed";
}
