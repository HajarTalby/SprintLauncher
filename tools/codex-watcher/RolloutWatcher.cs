using System.Threading.Channels;

namespace SprintLauncher.CodexWatcher;

public sealed class RolloutWatcher
{
    private readonly string _sessionsRoot;
    private readonly OffsetStore _offsetStore;
    private readonly RolloutParser _parser;
    private readonly NotifyInvoker _notify;
    private readonly TextWriter _log;
    private readonly HashSet<string> _seenTurnIds = new(StringComparer.Ordinal);

    public RolloutWatcher(string sessionsRoot, OffsetStore offsetStore, RolloutParser parser, NotifyInvoker notify, TextWriter log)
    {
        _sessionsRoot = sessionsRoot;
        _offsetStore = offsetStore;
        _parser = parser;
        _notify = notify;
        _log = log;
    }

    public async Task ScanTodayAsync(CancellationToken cancellationToken)
    {
        var todayDirectory = Path.Combine(_sessionsRoot, DateTime.Today.ToString("yyyy"), DateTime.Today.ToString("MM"), DateTime.Today.ToString("dd"));
        if (!Directory.Exists(todayDirectory)) return;

        foreach (var file in Directory.EnumerateFiles(todayDirectory, "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
            await ProcessFileAsync(file, cancellationToken);
    }

    public async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var watcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        var changedPaths = Channel.CreateUnbounded<string>();
        void Queue(string path) => changedPaths.Writer.TryWrite(path);
        watcher.Created += (_, args) => Queue(args.FullPath);
        watcher.Changed += (_, args) => Queue(args.FullPath);
        watcher.Renamed += (_, args) => Queue(args.FullPath);
        watcher.Error += (_, args) => _log.WriteLine($"[watcher] FileSystemWatcher: {args.GetException().Message}");

        // Subscribe before scanning so an append that races the startup scan is queued too.
        await ScanTodayAsync(cancellationToken);

        try
        {
            while (await changedPaths.Reader.WaitToReadAsync(cancellationToken))
                while (changedPaths.Reader.TryRead(out var path))
                    await ProcessFileAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task ProcessFileAsync(string rolloutPath, CancellationToken cancellationToken)
    {
        var entry = _offsetStore.Get(rolloutPath);
        var lines = _offsetStore.ReadNewLines(rolloutPath);
        if (lines.Count == 0) return;

        var parsed = _parser.ParseLines(lines, entry.SessionMeta);
        entry.SessionMeta = parsed.SessionMeta;
        foreach (var turn in parsed.CompletedTurns)
        {
            if (!entry.SeenTurnIds.Add(turn.TurnId) || !_seenTurnIds.Add(turn.TurnId)) continue;
            if (!SessionFilter.ShouldNotify(entry.SessionMeta)) continue;
            await _notify.InvokeAsync(rolloutPath, turn, cancellationToken);
        }
        _offsetStore.Save();
    }
}
