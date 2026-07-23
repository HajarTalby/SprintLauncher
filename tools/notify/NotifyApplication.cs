namespace SprintLauncher.Notify;

public sealed record NotifyRuntimeContext(
    string ApplicationBaseDirectory,
    string CurrentDirectory,
    string? SprintLauncherHome)
{
    public static NotifyRuntimeContext Create() => new(
        AppContext.BaseDirectory,
        Directory.GetCurrentDirectory(),
        Environment.GetEnvironmentVariable("SPRINTLAUNCHER_HOME"));
}

public static class NotifyApplication
{
    private static readonly string[] Actors = ["ccode", "ag", "codex", "sl"];

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        NotifyRuntimeContext runtimeContext)
    {
        if (args.Length == 1 && args[0].Equals("--check", StringComparison.OrdinalIgnoreCase))
        {
            return await RunCheckAsync(output, error, runtimeContext);
        }

        if (!NotifyOptions.TryParse(args, out var options, out var parseError))
        {
            await error.WriteLineAsync(parseError);
            return 2;
        }

        var resolution = ResolveEnvFile(runtimeContext);
        if (resolution is null || !resolution.Exists)
        {
            await WriteMissingEnvErrorAsync(error, resolution?.Path);
            return 0;
        }

        var values = EnvFile.Load(resolution.Path);
        var target = SlackTargetResolver.Resolve(values, options!.Actor);
        if (target is null)
        {
            return 0;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var notifier = new SlackNotifier(httpClient);
            await notifier.SendAsync(target, options, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SlackNotifyException)
        {
            await error.WriteLineAsync(
                $"Slack notification failed (#{target.Channel}): {ex.Message}");
        }

        return 0;
    }

    private static async Task<int> RunCheckAsync(
        TextWriter output,
        TextWriter error,
        NotifyRuntimeContext runtimeContext)
    {
        var resolution = ResolveEnvFile(runtimeContext);
        await output.WriteLineAsync($".env: {resolution?.Path ?? "not found"}");

        if (resolution is null || !resolution.Exists)
        {
            await WriteMissingEnvErrorAsync(error, resolution?.Path);
            foreach (var actor in Actors)
            {
                await output.WriteLineAsync($"{actor}: no");
            }

            return 1;
        }

        var values = EnvFile.Load(resolution.Path);
        var hasToken = false;
        foreach (var actor in Actors)
        {
            var target = SlackTargetResolver.Resolve(values, actor);
            if (target is null)
            {
                await output.WriteLineAsync($"{actor}: no");
                continue;
            }

            hasToken = true;
            await output.WriteLineAsync(
                $"{actor}: yes - {SlackTargetResolver.MaskToken(target.Token)} -> #{target.Channel}");
        }

        return hasToken ? 0 : 1;
    }

    private static EnvFileResolution? ResolveEnvFile(NotifyRuntimeContext runtimeContext) =>
        EnvFile.Resolve(
            runtimeContext.SprintLauncherHome,
            runtimeContext.ApplicationBaseDirectory,
            runtimeContext.CurrentDirectory);

    private static Task WriteMissingEnvErrorAsync(TextWriter error, string? path) =>
        error.WriteLineAsync(path is null
            ? "Slack notifier: .env file not found."
            : $"Slack notifier: .env file not found: {path}");
}
