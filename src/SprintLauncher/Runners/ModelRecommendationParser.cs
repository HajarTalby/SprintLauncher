using System.Text.RegularExpressions;

namespace SprintLauncher.Runners;

public enum ModelEngine
{
    Claude,
    Codex,
}

public sealed record ModelRecommendation(ModelEngine Engine, string Model, string Source);

public static class ModelRecommendationParser
{
    private static readonly Regex CommandRegex = new(
        @"^\s*!model\s+(?<engine>claude|ccode|claudeimplementation|codex|gpt|gptimplementation)\s+(?<model>\S.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitRecommendationRegex = new(
        @"(?im)^\s*(?:[-*]\s*)?(?:modele|modèle|model)\b[^:=\r\n]{0,80}(?:\((?<engine>claude|ccode|codex|gpt)\))?\s*[:=]\s*(?:(?<engine2>claude|ccode|codex|gpt)\s+)?(?<model>[A-Za-z0-9][A-Za-z0-9_.:/+\-]{1,80})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BracketRecommendationRegex = new(
        @"(?im)^\s*\[(?:model|modele|modèle)\s*(?:dev)?\s*(?::|\|)?\s*(?:(?<engine>claude|ccode|codex|gpt)\s+)?(?<model>[A-Za-z0-9][A-Za-z0-9_.:/+\-]{1,80})\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParseCommand(string line, out ModelRecommendation recommendation)
    {
        var match = CommandRegex.Match(line ?? "");
        if (match.Success && TryParseEngine(match.Groups["engine"].Value, out var engine))
        {
            recommendation = new ModelRecommendation(engine, CleanModel(match.Groups["model"].Value), line ?? "");
            return recommendation.Model.Length > 0;
        }

        recommendation = new ModelRecommendation(ModelEngine.Claude, "", "");
        return false;
    }

    public static IReadOnlyList<ModelRecommendation> ExtractRecommendations(string output, ModelEngine defaultEngine)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        var results = new List<ModelRecommendation>();
        foreach (Match match in ExplicitRecommendationRegex.Matches(output))
            AddMatch(results, match, defaultEngine);
        foreach (Match match in BracketRecommendationRegex.Matches(output))
            AddMatch(results, match, defaultEngine);
        foreach (var line in output.Split('\n'))
            if (TryParseCommand(line, out var command))
                results.Add(command);

        return results
            .Where(r => r.Model.Length > 0)
            .GroupBy(r => r.Engine)
            .Select(g => g.Last())
            .ToList();
    }

    private static void AddMatch(List<ModelRecommendation> results, Match match, ModelEngine defaultEngine)
    {
        var engineToken = match.Groups["engine"].Success
            ? match.Groups["engine"].Value
            : match.Groups["engine2"].Success ? match.Groups["engine2"].Value : "";
        var engine = TryParseEngine(engineToken, out var parsed) ? parsed : defaultEngine;
        var model = CleanModel(match.Groups["model"].Value);
        if (model.Length > 0)
            results.Add(new ModelRecommendation(engine, model, match.Value.Trim()));
    }

    public static bool TryParseEngine(string token, out ModelEngine engine)
    {
        switch ((token ?? "").Trim().ToLowerInvariant())
        {
            case "claude":
            case "ccode":
            case "claudeimplementation":
                engine = ModelEngine.Claude;
                return true;
            case "codex":
            case "gpt":
            case "gptimplementation":
                engine = ModelEngine.Codex;
                return true;
            default:
                engine = ModelEngine.Claude;
                return false;
        }
    }

    private static string CleanModel(string value)
    {
        var model = (value ?? "").Trim();
        model = model.Trim('`', '"', '\'', '.', ',', ';');
        return model.Length > 120 ? model[..120] : model;
    }
}
