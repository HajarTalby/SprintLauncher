using System.Text.RegularExpressions;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public enum ModelEngine
{
    Claude,
    Codex,
    Agy,
}

public sealed record ModelRecommendation(ModelEngine Engine, string Model, string Source, ActorRole? Role = null);

public static class ModelRecommendationParser
{
    private const string EnginePattern =
        "claude|ccode|claudeimplementation|codex|gpt|gptimplementation|agy|ag|agimplementation|antigravity";

    private static readonly Regex CommandRegex = new(
        @"^\s*!model\s+(?<engine>" + EnginePattern + @")\s+(?<model>\S.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitRecommendationRegex = new(
        @"(?im)^\s*(?:[-*]\s*)?(?:mod.{0,3}le|model)\b[^:=\r\n]{0,80}(?:\((?<engine>" + EnginePattern + @")\))?\s*[:=]\s*(?:(?<engine2>" + EnginePattern + @")\s+)?(?<model>[A-Za-z0-9][A-Za-z0-9_.:/+\-]{1,80})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BracketRecommendationRegex = new(
        @"(?im)^\s*\[(?:model|mod.{0,3}le)\s*(?:dev)?\s*(?::|\|)?\s*(?:(?<engine>" + EnginePattern + @")\s+)?(?<model>[A-Za-z0-9][A-Za-z0-9_.:/+\-]{1,80})\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParseCommand(string line, out ModelRecommendation recommendation)
    {
        var match = CommandRegex.Match(line ?? "");
        if (match.Success && TryParseTarget(match.Groups["engine"].Value, out var engine, out var role))
        {
            recommendation = new ModelRecommendation(engine, CleanModel(match.Groups["model"].Value), line ?? "", role);
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
        var engine = TryParseTarget(engineToken, out var parsed, out var role) ? parsed : defaultEngine;
        var model = CleanModel(match.Groups["model"].Value);
        if (model.Length > 0)
            results.Add(new ModelRecommendation(engine, model, match.Value.Trim(), role));
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
            case "agy":
            case "ag":
            case "agimplementation":
            case "antigravity":
                engine = ModelEngine.Agy;
                return true;
            default:
                engine = ModelEngine.Claude;
                return false;
        }
    }

    public static bool TryParseTarget(string token, out ModelEngine engine, out ActorRole? role)
    {
        role = null;
        switch ((token ?? "").Trim().ToLowerInvariant())
        {
            case "ccode":
            case "claudeimplementation":
                engine = ModelEngine.Claude;
                role = ActorRole.ClaudeImplementation;
                return true;
            case "gptimplementation":
                engine = ModelEngine.Codex;
                role = ActorRole.GptImplementation;
                return true;
            default:
                return TryParseEngine(token ?? "", out engine);
        }
    }

    private static string CleanModel(string value)
    {
        var model = (value ?? "").Trim();
        model = model.Trim('`', '"', '\'', '.', ',', ';');
        return model.Length > 120 ? model[..120] : model;
    }
}
