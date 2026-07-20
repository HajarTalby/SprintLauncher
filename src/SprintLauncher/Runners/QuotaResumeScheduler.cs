namespace SprintLauncher.Runners;

public interface IQuotaDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemQuotaDelay : IQuotaDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

public sealed record QuotaResumePlan(DateTimeOffset ResumeAt, TimeSpan Wait);

public sealed class QuotaResumeScheduler
{
    private readonly Func<DateTimeOffset> _now;
    private readonly IQuotaDelay _delay;

    public QuotaResumeScheduler(Func<DateTimeOffset>? now = null, IQuotaDelay? delay = null)
    {
        _now = now ?? (() => DateTimeOffset.Now);
        _delay = delay ?? new SystemQuotaDelay();
    }

    public QuotaResumePlan? Plan(
        IReadOnlyDictionary<string, DateTimeOffset> resetTimesByEngine,
        IReadOnlySet<string> exhaustedEngines,
        TimeSpan maxWait)
    {
        if (maxWait <= TimeSpan.Zero) return null;

        var now = _now();
        DateTimeOffset? resumeAt = null;
        foreach (var engine in exhaustedEngines)
        {
            if (!resetTimesByEngine.TryGetValue(engine, out var resetAt))
                return null;
            if (resumeAt is null || resetAt > resumeAt.Value)
                resumeAt = resetAt;
        }

        if (resumeAt is null) return null;
        var wait = resumeAt.Value - now;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > maxWait) return null;
        return new QuotaResumePlan(resumeAt.Value, wait);
    }

    public async Task<bool> WaitAsync(QuotaResumePlan plan, CancellationToken ct)
    {
        try
        {
            await _delay.DelayAsync(plan.Wait, ct);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
