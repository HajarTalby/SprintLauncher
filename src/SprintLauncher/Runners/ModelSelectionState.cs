using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed class ModelSelectionState
{
    private readonly object _lock = new();
    private readonly Dictionary<ActorRole, string> _roleModels = [];
    private string _claudeModel;
    private string _codexModel;
    private string _codexExecutionModel;
    private string _agyModel;

    public ModelSelectionState(string claudeModel, string codexModel, string agyModel, string? codexExecutionModel = null)
    {
        _claudeModel = string.IsNullOrWhiteSpace(claudeModel) ? "sonnet-5" : claudeModel;
        _codexModel = string.IsNullOrWhiteSpace(codexModel) ? "gpt-5.6-sol" : codexModel;
        _codexExecutionModel = string.IsNullOrWhiteSpace(codexExecutionModel) ? "gpt-5.6-terra" : codexExecutionModel!;
        _agyModel = string.IsNullOrWhiteSpace(agyModel) ? "agy" : agyModel;
    }

    public string ModelFor(ActorRole role)
    {
        lock (_lock)
        {
            if (_roleModels.TryGetValue(role, out var model)) return model;
            if (role.IsClaudeFamily()) return _claudeModel;
            if (role.IsAgFamily()) return _agyModel;
            // codex (Hajar 2026-07-22) : le dev et la QA (rôles d'exécution) tournent sur
            // le modèle d'exécution économique (terra) ; l'analyse, le pilotage, l'arbitrage
            // et le comité restent sur le modèle de raisonnement (sol). La revue croisée est
            // un cas particulier : bien que portée par un rôle d'implémentation, elle est
            // surchargée vers sol au site d'appel.
            return role.IsExecutionRole() ? _codexExecutionModel : _codexModel;
        }
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
            else if (recommendation.Engine == ModelEngine.Agy)
                _agyModel = recommendation.Model;
            else
                _codexModel = recommendation.Model;

            return null;
        }
    }

    private static ActorRole DevelopmentRoleFor(ModelEngine engine) => engine switch
    {
        ModelEngine.Claude => ActorRole.ClaudeImplementation,
        ModelEngine.Agy => ActorRole.AgImplementation,
        _ => ActorRole.GptImplementation,
    };
}
