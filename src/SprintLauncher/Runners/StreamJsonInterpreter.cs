using System.Text;
using System.Text.Json;

namespace SprintLauncher.Runners;

/// <summary>
/// Interprète le flux événementiel de `claude -p --output-format stream-json`
/// (SERZENIA-143 lot 8) : chaque événement devient une ligne lisible pour la
/// sortie live de l'UI (réflexion, outils invoqués, texte au fil de l'eau —
/// comme dans VS Code), et le résultat final est extrait proprement.
/// Tolérant : une ligne non-JSON est restituée telle quelle.
/// </summary>
public sealed class StreamJsonInterpreter
{
    private readonly StringBuilder _accumulated = new();

    /// <summary>Résultat final officiel (événement "result") — prioritaire sur l'accumulé.</summary>
    public string? FinalResult { get; private set; }

    /// <summary>Texte assistant accumulé (secours si l'événement result manque).</summary>
    public string AccumulatedText => _accumulated.ToString();

    /// <summary>Sortie retenue comme livrable de l'acteur.</summary>
    public string Output => !string.IsNullOrWhiteSpace(FinalResult) ? FinalResult! : AccumulatedText;

    /// <summary>Traduit une ligne du flux en ligne(s) lisible(s) pour le live — null si rien à montrer.</summary>
    public string? Interpret(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (!line.TrimStart().StartsWith('{'))
        {
            _accumulated.AppendLine(line);
            return line; // sortie non-événementielle : passthrough
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "system":
                    return root.TryGetProperty("subtype", out var st) && st.GetString() == "init"
                        ? "── session acteur démarrée ──"
                        : null;

                case "assistant":
                {
                    if (!root.TryGetProperty("message", out var msg) ||
                        !msg.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array)
                        return null;

                    var sb = new StringBuilder();
                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;
                        if (bt == "text" && block.TryGetProperty("text", out var txt))
                        {
                            var text = txt.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                _accumulated.Append(text);
                                sb.Append(text);
                            }
                        }
                        else if (bt == "thinking")
                        {
                            sb.AppendLine("🧠 (réflexion en cours…)");
                        }
                        else if (bt == "tool_use" && block.TryGetProperty("name", out var name))
                        {
                            var detail = "";
                            if (block.TryGetProperty("input", out var input))
                            {
                                if (input.TryGetProperty("command", out var cmd)) detail = cmd.GetString() ?? "";
                                else if (input.TryGetProperty("file_path", out var fp)) detail = fp.GetString() ?? "";
                                if (detail.Length > 100) detail = detail[..100] + "…";
                            }
                            sb.AppendLine($"🔧 {name.GetString()} {detail}".TrimEnd());
                        }
                    }
                    var outLine = sb.ToString();
                    return string.IsNullOrWhiteSpace(outLine) ? null : outLine.TrimEnd();
                }

                case "user":
                    // Résultats d'outils renvoyés à l'acteur — trop verbeux pour le live
                    return "   ↳ résultat d'outil reçu";

                case "result":
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String)
                        FinalResult = res.GetString();
                    return "── tour terminé ──";

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            _accumulated.AppendLine(line);
            return line;
        }
    }
}
