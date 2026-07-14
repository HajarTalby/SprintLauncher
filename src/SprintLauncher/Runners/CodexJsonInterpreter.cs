using System.Text;
using System.Text.Json;

namespace SprintLauncher.Runners;

/// <summary>Interprète un flux live d'acteur en lignes lisibles pour l'UI.</summary>
public interface ILiveInterpreter
{
    /// <summary>Traduit une ligne du flux en ligne(s) lisible(s) — null si rien à montrer.</summary>
    string? Interpret(string line);
    /// <summary>Sortie retenue comme livrable de l'acteur.</summary>
    string Output { get; }
}

/// <summary>
/// Interprète le flux `codex exec --json` (événements JSONL) en lignes live pour
/// l'UI — comme StreamJsonInterpreter le fait pour claude. Codex n'émet rien sur
/// stdout en mode normal (tout à la fin) : le mode --json donne la réflexion et
/// les actions au fil de l'eau. Tolérant : une ligne non-JSON est restituée telle
/// quelle. Le livrable final officiel vient de --output-last-message (fichier), ce
/// texte accumulé n'est qu'un secours.
/// </summary>
public sealed class CodexJsonInterpreter : ILiveInterpreter
{
    private readonly StringBuilder _agentText = new();

    /// <summary>Dernier message de l'agent (secours si le fichier --output-last-message manque).</summary>
    public string? FinalMessage { get; private set; }

    public string Output => !string.IsNullOrWhiteSpace(FinalMessage) ? FinalMessage! : _agentText.ToString();

    public string? Interpret(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (!line.TrimStart().StartsWith('{')) return line; // passthrough non-événementiel

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "thread.started": return "── session codex démarrée ──";
                case "turn.started":   return null;
                case "turn.completed": return "── tour terminé ──";

                case "item.started":
                case "item.updated":
                case "item.completed":
                    return root.TryGetProperty("item", out var item)
                        ? InterpretItem(item, evType: type)
                        : null;

                case "error":
                    return root.TryGetProperty("message", out var em)
                        ? $"⚠ {em.GetString()}" : "⚠ erreur codex";

                default: return null;
            }
        }
        catch (JsonException)
        {
            return line; // ligne non-JSON : passthrough
        }
    }

    private string? InterpretItem(JsonElement item, string? evType)
    {
        var completed = evType == "item.completed";
        var started = evType == "item.started";
        var itype = item.TryGetProperty("type", out var it) ? it.GetString() : null;

        if (itype == "agent_message")
        {
            var text = item.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            if (completed && !string.IsNullOrEmpty(text))
            {
                FinalMessage = text;            // le dernier agent_message = message final
                _agentText.AppendLine(text);
                return text;
            }
            return null;
        }

        if (itype == "reasoning")
        {
            if (!completed) return null;
            var r = FirstString(item, "text", "summary", "content");
            return r is not null ? $"🧠 {Trunc(r)}" : "🧠 (réflexion…)";
        }

        // Types variables selon la version de codex : commandes, patches, outils.
        if (itype is not null)
        {
            // Commande : montrée dès son DÉMARRAGE (temps réel), pas une fois finie.
            if (itype.Contains("command") || itype.Contains("shell") || itype.Contains("exec"))
            {
                if (!started) return null;
                var cmd = FirstString(item, "command", "cmd", "text");
                return cmd is not null ? $"🔧 {Trunc(cmd)}" : "🔧 (commande)";
            }
            if (itype.Contains("file") || itype.Contains("patch") || itype.Contains("change") || itype.Contains("diff"))
            {
                if (!completed) return null;
                var f = FirstString(item, "path", "file", "files", "text");
                return f is not null ? $"📝 {Trunc(f)}" : "📝 (modification de fichier)";
            }
        }
        return null;
    }

    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }

    private static string Trunc(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 140 ? s[..140] + "…" : s;
    }
}
