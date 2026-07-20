using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class ModelRecommendationParserTests
{
    [Theory]
    [InlineData("!model claude sonnet-5", ModelEngine.Claude, "sonnet-5")]
    [InlineData("!model ccode claude-opus-4-8", ModelEngine.Claude, "claude-opus-4-8")]
    [InlineData("!model codex gpt-5.5", ModelEngine.Codex, "gpt-5.5")]
    [InlineData("!model agy gemini-3-pro", ModelEngine.Agy, "gemini-3-pro")]
    public void Parses_explicit_hajar_model_command(string line, ModelEngine engine, string model)
    {
        Assert.True(ModelRecommendationParser.TryParseCommand(line, out var recommendation));
        Assert.Equal(engine, recommendation.Engine);
        Assert.Equal(model, recommendation.Model);
    }

    [Fact]
    public void Extracts_structured_recommendation_from_analysis_output()
    {
        var text = """
        ## SYNTHESE
        Complexite : critique.
        Modele dev recommande : codex gpt-5.5
        """;

        var recommendation = Assert.Single(ModelRecommendationParser.ExtractRecommendations(text, ModelEngine.Claude));
        Assert.Equal(ModelEngine.Codex, recommendation.Engine);
        Assert.Equal("gpt-5.5", recommendation.Model);
    }

    [Fact]
    public void Uses_default_engine_when_recommendation_has_no_engine()
    {
        var text = "Modele dev recommande : sonnet-5";

        var recommendation = Assert.Single(ModelRecommendationParser.ExtractRecommendations(text, ModelEngine.Claude));
        Assert.Equal(ModelEngine.Claude, recommendation.Engine);
        Assert.Equal("sonnet-5", recommendation.Model);
    }

    [Fact]
    public void Last_recommendation_wins_per_engine()
    {
        var text = """
        Modele dev recommande : claude sonnet-5
        Modele dev recommande : claude claude-opus-4-8
        Modele dev recommande : codex gpt-5.5
        Modele dev recommande : agy gemini-3-pro
        """;

        var recommendations = ModelRecommendationParser.ExtractRecommendations(text, ModelEngine.Claude);

        Assert.Contains(recommendations, r => r.Engine == ModelEngine.Claude && r.Model == "claude-opus-4-8");
        Assert.Contains(recommendations, r => r.Engine == ModelEngine.Codex && r.Model == "gpt-5.5");
        Assert.Contains(recommendations, r => r.Engine == ModelEngine.Agy && r.Model == "gemini-3-pro");
        Assert.Equal(3, recommendations.Count);
    }
}
