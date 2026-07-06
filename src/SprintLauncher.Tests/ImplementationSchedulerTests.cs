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
