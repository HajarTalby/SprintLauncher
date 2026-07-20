using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed class ModelSelectionState
{
    private readonly object _lock = new();
    private readonly Dictionary<ActorRole, string> _roleModels = [];
    private string _claudeModel;
    private string _codexModel;

    public ModelSelectionState(string claudeModel, string codexModel)
    {
        _claudeModel = string.IsNullOrWhiteSpace(claudeModel) ? "sonnet-5" : claudeModel;
        _codexModel = string.IsNullOrWhiteSpace(codexModel) ? "gpt-5.5" : codexModel;
    }

    public string ModelFor(ActorRole role)
    {
        lock (_lock)
            return _roleModels.TryGetValue(role, out var model)
                ? model
                : role.IsClaudeFamily() ? _claudeModel : _codexModel;
    }

    public ActorRole? Apply(ModelRecommendation recommendation, bool nextDevelopmentOnly)
    {
        lock (_lock)
        {
            var targetRole = recommendation.Role
                ?? (nextDevelopmentOnly ? DevelopmentRoleFor(recommendation.Engine) : null);

            if (targetRole is not null)
            {
                _roleModels[targetRole.Value] = recommendation.Model;
                return targetRole;
            }

            if (recommendation.Engine == ModelEngine.Claude)
                _claudeModel = recommendation.Model;
            else
                _codexModel = recommendation.Model;

            return null;
        }
    }

    private static ActorRole DevelopmentRoleFor(ModelEngine engine) =>
        engine == ModelEngine.Claude
            ? ActorRole.ClaudeImplementation
            : ActorRole.GptImplementation;
}
