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
        File.WriteAllText(expected, "SLACK_WEBHOOK_CCODE=https://example.invalid/install");
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
        var explicitHome = CreateRepo(directories, "explicit", "https://example.invalid/explicit");
        var installationRoot = CreateRepo(directories, "installation", "https://example.invalid/install");
        var cwdRoot = CreateRepo(directories, "cwd", "https://example.invalid/cwd");
        var applicationBase = Directory.CreateDirectory(Path.Combine(installationRoot, "published")).FullName;

        var resolution = EnvFile.Resolve(explicitHome, applicationBase, cwdRoot);

        Assert.NotNull(resolution);
        Assert.True(resolution.Exists);
        Assert.Equal(Path.Combine(explicitHome, ".env"), resolution.Path);
    }

    [Fact]
    public async Task Check_reports_env_and_masked_webhook_presence_without_sending()
    {
        using var directories = new TestDirectories();
        var installationRoot = directories.Create("SprintLauncher");
        File.WriteAllText(Path.Combine(installationRoot, "SprintLauncher.sln"), string.Empty);
        var envPath = Path.Combine(installationRoot, ".env");
        const string rawWebhook = "https://example.invalid/private-check-value";
        File.WriteAllText(envPath, $"SLACK_WEBHOOK_CCODE={rawWebhook}");
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
        Assert.Contains("ccode: yes - https://example.invalid/***", output.ToString());
        Assert.Contains("ag: no", output.ToString());
        Assert.Contains("codex: no", output.ToString());
        Assert.Contains("sl: no", output.ToString());
        Assert.DoesNotContain(rawWebhook, output.ToString());
    }

    [Fact]
    public async Task No_webhook_is_silent_for_notifications_and_makes_check_fail()
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
    public void Resolves_actor_specific_webhook()
    {
        var values = new Dictionary<string, string>
        {
            ["SLACK_WEBHOOK_CCODE"] = "https://example.invalid/actor",
            ["SLACK_WEBHOOK_DEFAULT"] = "https://example.invalid/default"
        };

        var resolved = WebhookResolver.Resolve(values, "ccode");

        Assert.Equal("https://example.invalid/actor", resolved?.ToString());
    }

    [Fact]
    public void Falls_back_to_default_webhook()
    {
        var values = new Dictionary<string, string>
        {
            ["SLACK_WEBHOOK_DEFAULT"] = "https://example.invalid/default"
        };

        var resolved = WebhookResolver.Resolve(values, "codex");

        Assert.Equal("https://example.invalid/default", resolved?.ToString());
    }

    [Fact]
    public void Missing_config_returns_null()
    {
        var resolved = WebhookResolver.Resolve(new Dictionary<string, string>(), "sl");

        Assert.Null(resolved);
    }

    [Fact]
    public void Masks_slack_webhook_secret()
    {
        var masked = WebhookResolver.Mask(new Uri("https://hooks.slack.com/services/"));

        Assert.Equal("https://hooks.slack.com/services/***", masked);
    }

    [Fact]
    public async Task Posts_expected_json_payload()
    {
        var handler = new RecordingHandler();
        var notifier = new SlackNotifier(handler, (_, _) => Task.CompletedTask);
        var options = new NotifyOptions("codex", "warn", "outil echoue", "SERZENIA-146");

        await notifier.SendAsync(new Uri("https://example.invalid/webhook"), options, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("application/json", handler.Request?.Content?.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", handler.Request?.Content?.Headers.ContentType?.CharSet?.ToLowerInvariant());

        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("[CODEX] warn \u2014 outil echoue\ncontext: SERZENIA-146", json.RootElement.GetProperty("text").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static string CreateRepo(TestDirectories directories, string name, string webhook)
    {
        var root = directories.Create(name);
        File.WriteAllText(Path.Combine(root, "SprintLauncher.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root, ".env"), $"SLACK_WEBHOOK_CCODE={webhook}");
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
