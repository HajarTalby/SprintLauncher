using SprintLauncher.CodexWatcher;

var options = WatcherOptions.Parse(args);
var offsetStore = new OffsetStore(options.OffsetStorePath);
var watcher = new RolloutWatcher(
    options.SessionsRoot,
    offsetStore,
    new RolloutParser(),
    new NotifyInvoker(options.RepositoryRoot, Console.Error).InvokeAsync,
    Console.Error);

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellationSource.Cancel(); };

Console.Error.WriteLine($"[watcher] sessions: {options.SessionsRoot}");
Console.Error.WriteLine($"[watcher] offsets: {options.OffsetStorePath}");
if (options.Once)
    await watcher.ScanTodayAsync(cancellationSource.Token);
else
    await watcher.WatchAsync(cancellationSource.Token);

internal sealed record WatcherOptions(string SessionsRoot, string OffsetStorePath, string RepositoryRoot, bool Once)
{
    public static WatcherOptions Parse(string[] args)
    {
        var codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var sessionsRoot = Path.Combine(codexHome, "sessions");
        var offsetStorePath = Path.Combine(codexHome, ".watcher-offsets.json");
        var repositoryRoot = FindRepositoryRoot();
        var once = false;

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--once") { once = true; continue; }
            if (index + 1 == args.Length) continue;
            var value = args[++index];
            switch (args[index - 1])
            {
                case "--sessions-root": sessionsRoot = value; break;
                case "--offset-store": offsetStorePath = value; break;
                case "--repo-root": repositoryRoot = value; break;
            }
        }

        return new WatcherOptions(Path.GetFullPath(sessionsRoot), Path.GetFullPath(offsetStorePath), Path.GetFullPath(repositoryRoot), once);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        return Environment.CurrentDirectory;
    }
}
