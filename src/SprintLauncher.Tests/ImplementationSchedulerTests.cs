using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class QuotaDetectorTests
{
    [Theory]
    [InlineData("Error: rate limit exceeded, retry later")]
    [InlineData("You have hit your usage limit for this period")]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("Your plan limit has been reached — upgrade to continue")]
    [InlineData("quota exceeded for this billing period")]
    public void Quota_patterns_are_detected(string stderr)
    {
        Assert.True(QuotaDetector.IsQuotaExhausted(null, stderr));
    }

    [Theory]
    [InlineData("NullReferenceException at Program.cs line 42")]
    [InlineData("fatal: not a git repository")]
    [InlineData("Build FAILED with 3 errors")]
    public void Technical_failures_are_not_quota(string stderr)
    {
        Assert.False(QuotaDetector.IsQuotaExhausted(null, stderr));
    }

    [Fact]
    public void Detects_in_stdout_too()
    {
        Assert.True(QuotaDetector.IsQuotaExhausted("usage cap reached for claude", null));
    }
}

public class ImplementationRotationTests
{
    private static HashSet<string> None => [];

    [Fact]
    public void First_us_goes_to_claude_by_default()
    {
        Assert.Equal("ClaudeImplementation", ImplementationRotation.PickEngine(null, None));
    }

    [Fact]
    public void Engines_alternate_between_us()
    {
        Assert.Equal("GptImplementation", ImplementationRotation.PickEngine("ClaudeImplementation", None));
        Assert.Equal("ClaudeImplementation", ImplementationRotation.PickEngine("GptImplementation", None));
    }

    [Fact]
    public void Exhausted_engine_is_skipped()
    {
        var exhausted = new HashSet<string> { "GptImplementation" };
        // Le tour de rôle voudrait codex mais il est épuisé → claude continue seul
        Assert.Equal("ClaudeImplementation", ImplementationRotation.PickEngine("ClaudeImplementation", exhausted));
    }

    [Fact]
    public void Both_exhausted_returns_null()
    {
        var exhausted = new HashSet<string> { "ClaudeImplementation", "GptImplementation" };
        Assert.Null(ImplementationRotation.PickEngine(null, exhausted));
    }

    [Fact]
    public void Relief_is_the_other_engine()
    {
        Assert.Equal("GptImplementation", ImplementationRotation.PickRelief("ClaudeImplementation", new HashSet<string> { "ClaudeImplementation" }));
        Assert.Equal("ClaudeImplementation", ImplementationRotation.PickRelief("GptImplementation", new HashSet<string> { "GptImplementation" }));
    }

    [Fact]
    public void No_relief_when_other_engine_also_exhausted()
    {
        var exhausted = new HashSet<string> { "ClaudeImplementation", "GptImplementation" };
        Assert.Null(ImplementationRotation.PickRelief("ClaudeImplementation", exhausted));
    }
}

public class UsTypeSpecializationTests
{
    private const string Front = "GptImplementation";
    private const string Back = "ClaudeImplementation";
    private static HashSet<string> None => [];

    [Fact]
    public void Ui_us_is_classified_front()
    {
        var type = UsTypeClassifier.Classify(
            "Écran de connexion", "## Objectif\nCréer la page de login avec formulaire et navigation.");
        Assert.Equal(UsType.Front, type);
    }

    [Fact]
    public void Api_us_is_classified_backend()
    {
        var type = UsTypeClassifier.Classify(
            "Service d'authentification", "## Objectif\nExposer un endpoint API avec persistance Firebase.");
        Assert.Equal(UsType.Backend, type);
    }

    [Fact]
    public void Neutral_us_is_unknown()
    {
        Assert.Equal(UsType.Unknown, UsTypeClassifier.Classify(
            "Documentation du projet", "## Objectif\nMettre à jour le runbook."));
    }

    [Fact]
    public void Front_us_goes_to_front_engine_and_backend_to_back_engine()
    {
        Assert.Equal(Front, ImplementationRotation.PickEngineForUs(UsType.Front, null, None, Front, Back));
        Assert.Equal(Back, ImplementationRotation.PickEngineForUs(UsType.Backend, null, None, Front, Back));
    }

    [Fact]
    public void Unknown_us_falls_back_to_alternation()
    {
        Assert.Equal(Back, ImplementationRotation.PickEngineForUs(UsType.Unknown, Front, None, Front, Back));
        Assert.Equal(Front, ImplementationRotation.PickEngineForUs(UsType.Unknown, Back, None, Front, Back));
    }

    [Fact]
    public void Exhausted_specialist_is_relieved_by_other_engine()
    {
        var exhausted = new HashSet<string> { Front };
        // US front mais moteur front à quota épuisé → le back prend la relève (avancer prime)
        Assert.Equal(Back, ImplementationRotation.PickEngineForUs(UsType.Front, null, exhausted, Front, Back));
    }
}
