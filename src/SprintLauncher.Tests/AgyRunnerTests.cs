using System.Text;
using SprintLauncher.Prompts;
using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public sealed class AgyBinaryLocatorTests
{
    [Fact]
    public void Finds_agy_from_env_override_first()
    {
        var temp = NewTempDir();
        var envBin = Path.Combine(temp, "env-agy.exe");
        var pathDir = Path.Combine(temp, "path");
        Directory.CreateDirectory(pathDir);
        File.WriteAllText(envBin, "");
        File.WriteAllText(Path.Combine(pathDir, "agy.exe"), "");

        try
        {
            var found = BinaryLocator.FindAgy(envBin, pathDir, ".exe", temp);

            Assert.Equal(envBin, found);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Finds_agy_on_path()
    {
        var temp = NewTempDir();
        var pathDir = Path.Combine(temp, "path");
        Directory.CreateDirectory(pathDir);
        var pathBin = Path.Combine(pathDir, "agy.exe");
        File.WriteAllText(pathBin, "");

        try
        {
            var found = BinaryLocator.FindAgy(null, pathDir, ".exe", temp);

            Assert.Equal(pathBin, found);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Returns_null_when_agy_is_absent()
    {
        var temp = NewTempDir();
        try
        {
            Assert.Null(BinaryLocator.FindAgy(null, Path.Combine(temp, "missing"), ".exe", temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sl-agy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class AgyRunnerTests
{
    [Fact]
    public void Builds_agy_arguments_without_cwd_and_with_add_dir()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"sl-agy-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        ActorRunner.PreparedAgyInvocation? invocation = null;

        try
        {
            invocation = Prepare("systeme", "utilisateur", repoRoot: repo, modelOverride: "g-test");
            var args = invocation.StartInfo.ArgumentList.ToArray();

            Assert.Null(invocation.Error);
            Assert.Equal("agy.exe", invocation.StartInfo.FileName);
            Assert.Equal(repo, invocation.StartInfo.WorkingDirectory);
            Assert.Contains("-p", args);
            Assert.Contains("systeme\n\n---\n\nutilisateur", args);
            Assert.Contains("--model", args);
            Assert.Contains("g-test", args);
            Assert.Contains("--add-dir", args);
            Assert.Contains(repo, args);
            Assert.Contains("--dangerously-skip-permissions", args);
            Assert.DoesNotContain("--cwd", args);
        }
        finally
        {
            if (invocation is not null) File.Delete(invocation.PromptFile);
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Writes_prompt_file_as_utf8_without_bom()
    {
        var invocation = Prepare("systeme", "prompt accentue");

        try
        {
            var bytes = File.ReadAllBytes(invocation.PromptFile);

            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("systeme\n\n---\n\nprompt accentue", File.ReadAllText(invocation.PromptFile, Encoding.UTF8));
            Assert.Empty(invocation.StartInfo.StandardInputEncoding?.GetPreamble() ?? []);
            Assert.Equal(Encoding.UTF8.WebName, invocation.StartInfo.StandardOutputEncoding?.WebName);
            Assert.Equal(Encoding.UTF8.WebName, invocation.StartInfo.StandardErrorEncoding?.WebName);
        }
        finally
        {
            File.Delete(invocation.PromptFile);
        }
    }

    [Fact]
    public void Marks_agy_process_as_sprintlauncher_actor_and_strips_api_keys()
    {
        var oldOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var oldAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "secret-openai");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "secret-anthropic");

        try
        {
            var invocation = Prepare("systeme", "utilisateur");
            var env = invocation.StartInfo.EnvironmentVariables;

            Assert.Equal("1", env[ActorRunner.ActorEnvVar]);
            Assert.False(env.ContainsKey("OPENAI_API_KEY"));
            Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
            File.Delete(invocation.PromptFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldOpenAi);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", oldAnthropic);
        }
    }

    [Fact]
    public void Long_prompt_cuts_off_until_agy_file_prompt_syntax_is_validated()
    {
        var longPrompt = new string('x', ActorRunner.AgyPromptArgumentSafetyLimit + 100);
        var invocation = Prepare("systeme", longPrompt);

        try
        {
            Assert.NotNull(invocation.Error);
            Assert.Contains("file-prompt syntax is not validated", invocation.Error);
            Assert.True(File.Exists(invocation.PromptFile));
            Assert.DoesNotContain(longPrompt, invocation.StartInfo.ArgumentList);
        }
        finally
        {
            File.Delete(invocation.PromptFile);
        }
    }

    [Fact]
    public void Validated_file_prompt_extension_can_replace_direct_prompt_argument()
    {
        var previous = Environment.GetEnvironmentVariable("AGY_PROMPT_FILE_ARGUMENT");
        Environment.SetEnvironmentVariable("AGY_PROMPT_FILE_ARGUMENT", "@{0}");

        try
        {
            var invocation = Prepare("systeme", new string('x', ActorRunner.AgyPromptArgumentSafetyLimit + 100));
            var args = invocation.StartInfo.ArgumentList.ToArray();

            Assert.Null(invocation.Error);
            Assert.Contains("-p", args);
            Assert.Contains("@" + invocation.PromptFile, args);
            File.Delete(invocation.PromptFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGY_PROMPT_FILE_ARGUMENT", previous);
        }
    }

    [Fact]
    public void Ag_quota_messages_are_detected_by_shared_detector()
    {
        Assert.True(QuotaDetector.IsQuotaExhausted(
            null,
            "Antigravity quota exceeded: out of credits for this plan"));
    }

    private static ActorRunner.PreparedAgyInvocation Prepare(
        string systemPrompt,
        string userPrompt,
        string? repoRoot = null,
        string? modelOverride = null)
    {
        return ActorRunner.PrepareAgyInvocation(
            new ActorPrompt(ActorRole.AgImplementation, systemPrompt, userPrompt),
            "agy.exe",
            "gemini-test",
            modelOverride,
            repoRoot);
    }
}
