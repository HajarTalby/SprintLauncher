using SprintLauncher.Events;
using SprintLauncher.Prompts;

namespace SprintLauncher.Runners;

public sealed class ActorTurnCoordinator
{
    private readonly RepositoryReconciler _reconciler;
    private readonly Dictionary<string, SemaphoreSlim> _engineLocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new(1, 1),
        ["codex"] = new(1, 1),
    };
    private readonly object _activeLock = new();
    private readonly List<GitStatusSnapshot> _deferredBaselines = [];
    private int _activeTurns;

    public ActorTurnCoordinator(string repoRoot)
    {
        _reconciler = new RepositoryReconciler(repoRoot);
    }

    public async Task<ActorTurnScope> BeginAsync(ActorRole role, CancellationToken ct = default)
    {
        var engine = EngineFor(role);
        var engineLock = _engineLocks[engine];
        await engineLock.WaitAsync(ct);

        GitStatusSnapshot baseline;
        try
        {
            baseline = await _reconciler.CaptureAsync(ct);
            lock (_activeLock) _activeTurns++;
        }
        catch
        {
            engineLock.Release();
            throw;
        }

        return new ActorTurnScope(this, role, engine, engineLock, baseline);
    }

    private async Task CompleteAsync(ActorTurnScope scope, CancellationToken ct)
    {
        IReadOnlyList<GitStatusSnapshot>? toReconcile = null;
        var deferred = false;

        lock (_activeLock)
        {
            _activeTurns--;
            if (_activeTurns > 0)
            {
                _deferredBaselines.Add(scope.Baseline);
                deferred = true;
            }
            else
            {
                toReconcile = [.. _deferredBaselines, scope.Baseline];
                _deferredBaselines.Clear();
            }
        }

        try
        {
            if (deferred)
            {
                Console.WriteLine($"  ~ Réconciliation git différée pour {scope.Role} : un autre moteur tourne encore.");
                EventEmitter.Emit("repository-reconcile", new { role = scope.Role.ToString(), engine = scope.Engine, deferred = true });
                return;
            }

            var result = await _reconciler.ReconcileAsync(toReconcile!, ct);
            foreach (var warning in result.Warnings)
                Console.WriteLine($"  ! Réconciliation git : {warning}");

            if (result.StagedPaths.Count > 0)
                Console.WriteLine($"  + Réconciliation git : {result.StagedPaths.Count} fichier(s) ajouté(s) à l'index (git add ciblé).");
            else if (result.Warnings.Count == 0)
                Console.WriteLine("  = Réconciliation git : aucun nouveau fichier à ajouter.");

            EventEmitter.Emit("repository-reconcile", new
            {
                role = scope.Role.ToString(),
                engine = scope.Engine,
                staged = result.StagedPaths,
                warnings = result.Warnings,
                gitUnavailable = result.GitUnavailable,
            });
        }
        finally
        {
            scope.ReleaseEngine();
        }
    }

    private static string EngineFor(ActorRole role) =>
        role.IsClaudeFamily() ? "claude" : "codex";

    public sealed class ActorTurnScope : IAsyncDisposable
    {
        private readonly ActorTurnCoordinator _owner;
        private readonly SemaphoreSlim _engineLock;
        private bool _completed;
        private bool _released;

        internal ActorTurnScope(
            ActorTurnCoordinator owner,
            ActorRole role,
            string engine,
            SemaphoreSlim engineLock,
            GitStatusSnapshot baseline)
        {
            _owner = owner;
            Role = role;
            Engine = engine;
            _engineLock = engineLock;
            Baseline = baseline;
        }

        public ActorRole Role { get; }
        public string Engine { get; }
        internal GitStatusSnapshot Baseline { get; }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            _completed = true;
            await _owner.CompleteAsync(this, ct);
        }

        internal void ReleaseEngine()
        {
            if (_released) return;
            _released = true;
            _engineLock.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                lock (_owner._activeLock)
                {
                    if (_owner._activeTurns > 0) _owner._activeTurns--;
                }
                ReleaseEngine();
            }
            await ValueTask.CompletedTask;
        }
    }
}
