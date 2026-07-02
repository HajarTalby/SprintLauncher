using System.Text;
using System.Text.Json;

namespace SprintLauncher.Jira;

public static class AdfConverter
{
    public static string ToText(JsonElement? doc)
    {
        if (doc is null || doc.Value.ValueKind == JsonValueKind.Null) return string.Empty;
        var sb = new StringBuilder();
        AppendNode(sb, doc.Value);
        return sb.ToString().Trim();
    }

    private static void AppendNode(StringBuilder sb, JsonElement node)
    {
        if (!node.TryGetProperty("type", out var typeProp)) return;
        var type = typeProp.GetString();

        // Leaf nodes (no content array)
        switch (type)
        {
            case "text":
                if (node.TryGetProperty("text", out var txt)) sb.Append(txt.GetString());
                return;
            case "hardBreak":
                sb.AppendLine();
                return;
            case "rule":
                sb.AppendLine("---");
                return;
            case "mention":
                if (node.TryGetProperty("attrs", out var mAttrs) && mAttrs.TryGetProperty("text", out var mTxt))
                    sb.Append('@').Append(mTxt.GetString());
                return;
            case "emoji":
                if (node.TryGetProperty("attrs", out var eAttrs) && eAttrs.TryGetProperty("text", out var eTxt))
                    sb.Append(eTxt.GetString());
                return;
            case "inlineCard":
                if (node.TryGetProperty("attrs", out var cAttrs) && cAttrs.TryGetProperty("url", out var cUrl))
                    sb.Append('[').Append(cUrl.GetString()).Append(']');
                return;
            case "media":
                if (node.TryGetProperty("attrs", out var mediaAttrs) && mediaAttrs.TryGetProperty("alt", out var alt))
                    sb.Append('[').Append(alt.GetString()).Append(']');
                else
                    sb.Append("[attachment]");
                return;
        }

        if (!node.TryGetProperty("content", out var contentProp)) return;
        var children = contentProp.EnumerateArray().ToArray();

        switch (type)
        {
            case "doc":
            case "blockquote":
            case "mediaGroup":
                foreach (var child in children) AppendNode(sb, child);
                break;

            case "heading":
                var level = node.TryGetProperty("attrs", out var hAttrs) && hAttrs.TryGetProperty("level", out var lvl)
                    ? lvl.GetInt32() : 1;
                sb.Append(new string('#', level)).Append(' ');
                foreach (var child in children) AppendNode(sb, child);
                sb.AppendLine();
                break;

            case "paragraph":
                foreach (var child in children) AppendNode(sb, child);
                sb.AppendLine();
                break;

            case "bulletList":
                foreach (var item in children)
                {
                    sb.Append("- ");
                    AppendListItemContent(sb, item);
                }
                break;

            case "orderedList":
                int n = 1;
                foreach (var item in children)
                {
                    sb.Append($"{n++}. ");
                    AppendListItemContent(sb, item);
                }
                break;

            case "listItem":
                AppendListItemContent(sb, node);
                break;

            case "codeBlock":
                sb.AppendLine("```");
                foreach (var child in children) AppendNode(sb, child);
                sb.AppendLine("```");
                break;

            case "table":
                foreach (var row in children) AppendTableRow(sb, row);
                sb.AppendLine();
                break;

            case "tableRow":
                AppendTableRow(sb, node);
                break;

            default:
                foreach (var child in children) AppendNode(sb, child);
                break;
        }
    }

    private static void AppendListItemContent(StringBuilder sb, JsonElement listItem)
    {
        if (!listItem.TryGetProperty("content", out var content)) { sb.AppendLine(); return; }
        foreach (var child in content.EnumerateArray())
        {
            if (child.TryGetProperty("type", out var t) && t.GetString() == "paragraph")
            {
                if (child.TryGetProperty("content", out var pContent))
                    foreach (var cc in pContent.EnumerateArray()) AppendNode(sb, cc);
            }
            else
            {
                AppendNode(sb, child);
            }
        }
        sb.AppendLine();
    }

    private static void AppendTableRow(StringBuilder sb, JsonElement row)
    {
        if (!row.TryGetProperty("content", out var cells)) return;
        var parts = new List<string>();
        foreach (var cell in cells.EnumerateArray())
        {
            var cellSb = new StringBuilder();
            if (cell.TryGetProperty("content", out var cellContent))
                foreach (var cp in cellContent.EnumerateArray()) AppendNode(cellSb, cp);
            parts.Add(cellSb.ToString().Trim().Replace('\n', ' '));
        }
        sb.AppendLine("| " + string.Join(" | ", parts) + " |");
    }
}
