using System.Net;
using System.Text.Json;
using SprintLauncher.Notify;
using Xunit;

namespace SprintLauncher.Tests;

public class NotifyTests
{
    [Fact]
    public void Resolves_env_from_installation_when_cwd_is_a_foreign_repo()
    {
        using var directories = new TestDirectories();
        var installationRoot = directories.Create("SprintLauncher");
        File.WriteAllText(Path.Combine(installationRoot, "SprintLauncher.sln"), string.Empty);
        var expected = Path.Combine(installationRoot, ".env");
        File.WriteAllText(expected, "SLACK_BOT_TOKEN=xoxb-install");
        var applicationBase = Directory.CreateDirectory(
            Path.Combine(installationRoot, "tools", "notify", "published")).FullName;
        var foreignCwd = Directory.CreateDirectory(Path.Combine(directories.Root, "SERZENIA")).FullName;
        Directory.CreateDirectory(Path.Combine(foreignCwd, ".git"));

        var resolution = EnvFile.Resolve(null, applicationBase, foreignCwd);

        Assert.NotNull(resolution);
        Assert.True(resolution.Exists);
        Assert.Equal(expected, resolution.Path);
    }

    [Fact]
    public void Resolves_env_beside_the_executable_in_a_release_layout()
    {
        using var directories = new TestDirectories();
        // Layout release : pas de .sln, le .env est déposé à côté de notify.exe.
        var releaseDir = directories.Create("release");
        var expected = Path.Combine(releaseDir, ".env");
        File.WriteAllText(expected, "SLACK_BOT_TOKEN=xoxb-release");

        var resolution = EnvFile.Resolve(null, releaseDir, directories.Root);

        Assert.NotNull(resolution);
        Assert.True(resolution.Exists);
        Assert.Equal(expected, resolution.Path);
    }

    [Fact]
    public async Task Missing_env_is_reported_but_does_not_fail_a_notification()
    {
        using var directories = new TestDirectories();
        var installationRoot = directories.Create("SprintLauncher");
        File.WriteAllText(Path.Combine(installationRoot, "SprintLauncher.sln"), string.Empty);
        var applicationBase = Directory.CreateDirectory(
            Path.Combine(installationRoot, "tools", "notify", "published")).FullName;
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await NotifyApplication.RunAsync(
            ["--actor", "ccode", "--level", "info", "--text", "test"],
            output,
            error,
            new NotifyRuntimeContext(applicationBase, directories.Root, null));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(
            $"Slack notifier: .env file not found: {Path.Combine(installationRoot, ".env")}{Environment.NewLine}",
            error.ToString());
    }

    [Fact]
    public void Sprintlauncher_home_has_priority_over_installation_and_cwd()
    {
        using var directories = new TestDirectories();
        var explicitHome = CreateRepo(directories, "explicit");
        var installationRoot = CreateRepo(directories, "installation");
        var cwdRoot = CreateRepo(directories, "cwd");
        var applicationBase = Directory.CreateDirectory(Path.Combine(installationRoot, "published")).FullName;

        var resolution = EnvFile.Resolve(explicitHome, applicationBase, cwdRoot);

        Assert.NotNull(resolution);
        Assert.True(resolution.Exists);
        Assert.Equal(Path.Combine(explicitHome, ".env"), resolution.Path);
    }

    [Fact]
    public async Task Check_reports_env_and_channels_without_leaking_the_token()
    {
        using var directories = new TestDirectories();
        var installationRoot = directories.Create("SprintLauncher");
        File.WriteAllText(Path.Combine(installationRoot, "SprintLauncher.sln"), string.Empty);
        var envPath = Path.Combine(installationRoot, ".env");
        const string rawToken = "xoxb-1234567890-secret-value";
        File.WriteAllText(envPath, $"SLACK_BOT_TOKEN={rawToken}\nSLACK_CHANNEL_CCODE=sl-ccode");
        var applicationBase = Directory.CreateDirectory(
            Path.Combine(installationRoot, "tools", "notify", "published")).FullName;
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await NotifyApplication.RunAsync(
            ["--check"],
            output,
            error,
            new NotifyRuntimeContext(applicationBase, directories.Root, null));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains($".env: {envPath}", output.ToString());
        // Token global : tous les acteurs résolvent une cible ; canal par défaut = nom d'acteur.
        Assert.Contains("ccode: yes - xoxb-*** -> #sl-ccode", output.ToString());
        Assert.Contains("ag: yes - xoxb-*** -> #ag", output.ToString());
        Assert.Contains("codex: yes - xoxb-*** -> #codex", output.ToString());
        Assert.Contains("sl: yes - xoxb-*** -> #sl", output.ToString());
        Assert.DoesNotContain(rawToken, output.ToString());
    }

    [Fact]
    public async Task No_token_is_silent_for_notifications_and_makes_check_fail()
    {
        using var directories = new TestDirectories();
        var installationRoot = directories.Create("SprintLauncher");
        File.WriteAllText(Path.Combine(installationRoot, "SprintLauncher.sln"), string.Empty);
        File.WriteAllText(Path.Combine(installationRoot, ".env"), "# no Slack configuration");
        var applicationBase = Directory.CreateDirectory(
            Path.Combine(installationRoot, "tools", "notify", "published")).FullName;
        var runtimeContext = new NotifyRuntimeContext(applicationBase, directories.Root, null);
        var notifyOutput = new StringWriter();
        var notifyError = new StringWriter();

        var notifyExitCode = await NotifyApplication.RunAsync(
            ["--actor", "ccode", "--level", "info", "--text", "test"],
            notifyOutput,
            notifyError,
            runtimeContext);
        var checkOutput = new StringWriter();
        var checkError = new StringWriter();
        var checkExitCode = await NotifyApplication.RunAsync(
            ["--check"], checkOutput, checkError, runtimeContext);

        Assert.Equal(0, notifyExitCode);
        Assert.Equal(string.Empty, notifyOutput.ToString());
        Assert.Equal(string.Empty, notifyError.ToString());
        Assert.Equal(1, checkExitCode);
        Assert.Equal(string.Empty, checkError.ToString());
        Assert.Contains("ccode: no", checkOutput.ToString());
    }

    [Fact]
    public void Resolves_actor_channel_override()
    {
        var values = new Dictionary<string, string>
        {
            ["SLACK_BOT_TOKEN"] = "xoxb-token",
            ["SLACK_CHANNEL_CCODE"] = "C0123ABCD"
        };

        var target = SlackTargetResolver.Resolve(values, "ccode");

        Assert.Equal("xoxb-token", target?.Token);
        Assert.Equal("C0123ABCD", target?.Channel);
    }

    [Fact]
    public void Defaults_channel_to_actor_name()
    {
        var values = new Dictionary<string, string> { ["SLACK_BOT_TOKEN"] = "xoxb-token" };

        var target = SlackTargetResolver.Resolve(values, "codex");

        Assert.Equal("codex", target?.Channel);
    }

    [Fact]
    public void Missing_token_returns_null()
    {
        var target = SlackTargetResolver.Resolve(new Dictionary<string, string>(), "sl");

        Assert.Null(target);
    }

    [Fact]
    public void Masks_bot_token()
    {
        Assert.Equal("xoxb-***", SlackTargetResolver.MaskToken("xoxb-1234-abcd"));
        Assert.Equal("***", SlackTargetResolver.MaskToken("other"));
    }

    [Fact]
    public async Task Posts_chat_postmessage_with_bearer_and_channel()
    {
        var handler = new RecordingHandler("{\"ok\":true}");
        var notifier = new SlackNotifier(handler, (_, _) => Task.CompletedTask);
        var options = new NotifyOptions("codex", "warn", "outil echoue", "SERZENIA-146");

        await notifier.SendAsync(new SlackTarget("xoxb-secret", "codex"), options, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("https://slack.com/api/chat.postMessage", handler.Request?.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.Request?.Headers.Authorization?.Scheme);
        Assert.Equal("xoxb-secret", handler.Request?.Headers.Authorization?.Parameter);
        Assert.Equal("application/json", handler.Request?.Content?.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("codex", json.RootElement.GetProperty("channel").GetString());
        Assert.Equal("[CODEX] warn — outil echoue\ncontext: SERZENIA-146", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Api_error_is_not_retried_and_surfaces_the_reason()
    {
        var handler = new RecordingHandler("{\"ok\":false,\"error\":\"channel_not_found\"}");
        var notifier = new SlackNotifier(handler, (_, _) => Task.CompletedTask);
        var options = new NotifyOptions("sl", "info", "hello", null);

        var ex = await Assert.ThrowsAsync<SlackNotifyException>(() =>
            notifier.SendAsync(new SlackTarget("xoxb-secret", "sl"), options, CancellationToken.None));

        Assert.Contains("channel_not_found", ex.Message);
        Assert.Equal(1, handler.CallCount); // pas de retry sur erreur applicative
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingHandler(string responseBody) => _responseBody = responseBody;

        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }

    private static string CreateRepo(TestDirectories directories, string name)
    {
        var root = directories.Create(name);
        File.WriteAllText(Path.Combine(root, "SprintLauncher.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root, ".env"), "SLACK_BOT_TOKEN=xoxb-test");
        return root;
    }

    private sealed class TestDirectories : IDisposable
    {
        public TestDirectories()
        {
            Root = Path.Combine(Path.GetTempPath(), "SprintLauncher.Notify.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Create(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
