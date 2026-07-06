using System.Text.RegularExpressions;

namespace SprintLauncher.Jira;

/// <summary>
/// Convertit le markdown produit par les acteurs en ADF (Atlassian Document Format)
/// pour que titres, listes, tableaux et blocs de code soient rendus correctement
/// dans Jira (SERZENIA-143 lot 6 — remplace l'aplatissement texte brut).
/// Inverse fonctionnel de <see cref="AdfConverter"/> (ADF → texte).
/// Couverture : titres #..######, listes -/* et 1., blocs ```, tableaux |,
/// règles ---, gras **x**, code inline `x`, liens [t](url). Le reste passe en paragraphe.
/// </summary>
public static class MarkdownToAdf
{
    public static object Convert(string markdown)
    {
        var content = new List<object>();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // ── Bloc de code ```lang ... ``` ─────────────────────────────────
            if (line.TrimStart().StartsWith("```"))
            {
                var lang = line.TrimStart()[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    code.Add(lines[i++]);
                i++; // saute la fence fermante
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "codeBlock",
                    ["attrs"] = string.IsNullOrEmpty(lang) ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["language"] = lang },
                    ["content"] = code.Count == 0
                        ? new List<object>()
                        : new List<object> { new Dictionary<string, object?> { ["type"] = "text", ["text"] = string.Join("\n", code) } },
                });
                continue;
            }

            // ── Tableau | a | b | ────────────────────────────────────────────
            if (IsTableLine(line))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && IsTableLine(lines[i]))
                    tableLines.Add(lines[i++]);
                content.Add(BuildTable(tableLines));
                continue;
            }

            // ── Liste à puces (lignes consécutives) ──────────────────────────
            if (Regex.IsMatch(line, @"^\s*[-*]\s+"))
            {
                var items = new List<object>();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*[-*]\s+"))
                    items.Add(ListItem(Regex.Replace(lines[i++], @"^\s*[-*]\s+", "")));
                content.Add(new Dictionary<string, object?> { ["type"] = "bulletList", ["content"] = items });
                continue;
            }

            // ── Liste numérotée ──────────────────────────────────────────────
            if (Regex.IsMatch(line, @"^\s*\d+[\.\)]\s+"))
            {
                var items = new List<object>();
                while (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*\d+[\.\)]\s+"))
                    items.Add(ListItem(Regex.Replace(lines[i++], @"^\s*\d+[\.\)]\s+", "")));
                content.Add(new Dictionary<string, object?> { ["type"] = "orderedList", ["content"] = items });
                continue;
            }

            // ── Titre #..###### ──────────────────────────────────────────────
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (heading.Success)
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "heading",
                    ["attrs"] = new Dictionary<string, object?> { ["level"] = heading.Groups[1].Value.Length },
                    ["content"] = Inline(heading.Groups[2].Value),
                });
                i++;
                continue;
            }

            // ── Règle horizontale ────────────────────────────────────────────
            if (Regex.IsMatch(line.Trim(), @"^(-{3,}|_{3,}|\*{3,})$"))
            {
                content.Add(new Dictionary<string, object?> { ["type"] = "rule" });
                i++;
                continue;
            }

            // ── Ligne vide ───────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // ── Paragraphe ───────────────────────────────────────────────────
            content.Add(new Dictionary<string, object?> { ["type"] = "paragraph", ["content"] = Inline(line) });
            i++;
        }

        if (content.Count == 0)
            content.Add(new Dictionary<string, object?> { ["type"] = "paragraph", ["content"] = new List<object>() });

        return new Dictionary<string, object?> { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    private static bool IsTableLine(string line) =>
        line.TrimStart().StartsWith('|') && line.TrimEnd().EndsWith('|') && line.Count(c => c == '|') >= 2;

    private static object BuildTable(List<string> tableLines)
    {
        var rows = new List<object>();
        bool headerDone = false;

        foreach (var raw in tableLines)
        {
            var trimmed = raw.Trim().Trim('|');
            // Ligne séparatrice |---|---| → ignorée, marque la fin de l'en-tête
            if (Regex.IsMatch(trimmed, @"^[\s:\-|]+$")) { headerDone = true; continue; }

            var cellType = rows.Count == 0 && !headerDone ? "tableHeader" : "tableCell";
            var cells = trimmed.Split('|')
                .Select(c => (object)new Dictionary<string, object?>
                {
                    ["type"] = cellType,
                    ["content"] = new List<object>
                    {
                        new Dictionary<string, object?> { ["type"] = "paragraph", ["content"] = Inline(c.Trim()) },
                    },
                })
                .ToList();
            rows.Add(new Dictionary<string, object?> { ["type"] = "tableRow", ["content"] = cells });
        }

        return new Dictionary<string, object?> { ["type"] = "table", ["content"] = rows };
    }

    private static object ListItem(string text) => new Dictionary<string, object?>
    {
        ["type"] = "listItem",
        ["content"] = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "paragraph", ["content"] = Inline(text) },
        },
    };

    // Inline : gras **x**, code `x`, lien [t](url). Tokenisation par regex combinée.
    private static readonly Regex InlineToken = new(
        @"(\*\*(?<bold>.+?)\*\*)|(`(?<code>[^`]+)`)|(\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\))",
        RegexOptions.Compiled);

    private static List<object> Inline(string text)
    {
        var nodes = new List<object>();
        if (string.IsNullOrEmpty(text)) return nodes;

        int pos = 0;
        foreach (Match m in InlineToken.Matches(text))
        {
            if (m.Index > pos)
                nodes.Add(TextNode(text[pos..m.Index]));

            if (m.Groups["bold"].Success)
                nodes.Add(TextNode(m.Groups["bold"].Value, mark: "strong"));
            else if (m.Groups["code"].Success)
                nodes.Add(TextNode(m.Groups["code"].Value, mark: "code"));
            else
                nodes.Add(LinkNode(m.Groups["label"].Value, m.Groups["url"].Value));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            nodes.Add(TextNode(text[pos..]));

        return nodes;
    }

    private static Dictionary<string, object?> TextNode(string text, string? mark = null)
    {
        var node = new Dictionary<string, object?> { ["type"] = "text", ["text"] = text };
        if (mark is not null)
            node["marks"] = new List<object> { new Dictionary<string, object?> { ["type"] = mark } };
        return node;
    }

    private static Dictionary<string, object?> LinkNode(string label, string url) => new()
    {
        ["type"] = "text",
        ["text"] = label,
        ["marks"] = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "link",
                ["attrs"] = new Dictionary<string, object?> { ["href"] = url },
            },
        },
    };
}
