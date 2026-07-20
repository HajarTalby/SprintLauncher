using SprintLauncher.Runners;
using Xunit;

namespace SprintLauncher.Tests;

public class QuotaResetParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 9, 0, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset Evening = new(2026, 7, 19, 21, 0, 0, TimeSpan.FromHours(-4));

    [Theory]
    [InlineData("You've hit your session limit - resets 9:50pm", 21, 50)]
    // Chaine exacte emise par claude.exe, avec son point median (constatee au run
    // sprint 6 du 2026-07-19) : c'est celle qu'on rencontrera en production.
    [InlineData("You've hit your session limit · resets 9:50pm", 21, 50)]
    [InlineData("You've hit your session limit - resets 9:50 PM", 21, 50)]
    [InlineData("session limit, resets 10am", 10, 0)]
    [InlineData("quota exhausted, resets 21:50", 21, 50)]
    public void Parses_reset_time_formats(string message, int hour, int minute)
    {
        var schedule = QuotaResetParser.TryParse(message, null, Now);

        Assert.NotNull(schedule);
        Assert.Equal(new DateTimeOffset(2026, 7, 19, hour, minute, 0, Now.Offset), schedule!.ResetAt);
    }

    [Fact]
    public void Past_time_rolls_to_next_day()
    {
        var schedule = QuotaResetParser.TryParse("You've hit your session limit - resets 8:30pm", null, Evening);

        Assert.NotNull(schedule);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 20, 30, 0, Evening.Offset), schedule!.ResetAt);
    }

    [Fact]
    public void Message_without_usable_time_returns_null()
    {
        Assert.Null(QuotaResetParser.TryParse("You've hit your session limit - try later", null, Now));
    }

    [Fact]
    public void Parses_real_claude_message_from_stderr()
    {
        var schedule = QuotaResetParser.TryParse(null, "Error: You've hit your session limit - resets 9:50pm", Now);

        Assert.NotNull(schedule);
        Assert.Equal(21, schedule!.ResetAt.Hour);
        Assert.Equal(50, schedule.ResetAt.Minute);
    }
}

public class QuotaResumeSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 21, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Plan_uses_latest_reset_and_respects_max_wait()
    {
        var scheduler = new QuotaResumeScheduler(() => Now, new RecordingDelay());
        var resetTimes = new Dictionary<string, DateTimeOffset>
        {
            ["ClaudeImplementation"] = Now.AddMinutes(20),
            ["GptImplementation"] = Now.AddMinutes(45),
        };

        var plan = scheduler.Plan(resetTimes, resetTimes.Keys.ToHashSet(), TimeSpan.FromHours(8));

        Assert.NotNull(plan);
        Assert.Equal(Now.AddMinutes(45), plan!.ResumeAt);
        Assert.Equal(TimeSpan.FromMinutes(45), plan.Wait);
    }

    [Fact]
    public void Plan_is_not_created_when_wait_exceeds_cap()
    {
        var scheduler = new QuotaResumeScheduler(() => Now, new RecordingDelay());
        var resetTimes = new Dictionary<string, DateTimeOffset>
        {
            ["ClaudeImplementation"] = Now.AddHours(9),
            ["GptImplementation"] = Now.AddMinutes(30),
        };

        Assert.Null(scheduler.Plan(resetTimes, resetTimes.Keys.ToHashSet(), TimeSpan.FromHours(8)));
    }

    [Fact]
    public void Plan_is_not_created_when_an_exhausted_engine_has_no_reset_time()
    {
        var scheduler = new QuotaResumeScheduler(() => Now, new RecordingDelay());
        var resetTimes = new Dictionary<string, DateTimeOffset>
        {
            ["ClaudeImplementation"] = Now.AddMinutes(20),
        };
        var exhausted = new HashSet<string> { "ClaudeImplementation", "GptImplementation" };

        Assert.Null(scheduler.Plan(resetTimes, exhausted, TimeSpan.FromHours(8)));
    }

    [Fact]
    public async Task Wait_is_cancelled_by_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        var delay = new CancelAwareDelay(() => cts.Cancel());
        var scheduler = new QuotaResumeScheduler(() => Now, delay);
        var plan = new QuotaResumePlan(Now.AddHours(1), TimeSpan.FromHours(1));

        var resumed = await scheduler.WaitAsync(plan, cts.Token);

        Assert.False(resumed);
        Assert.True(delay.Called);
    }

    private sealed class RecordingDelay : IQuotaDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CancelAwareDelay(Action beforeThrow) : IQuotaDelay
    {
        public bool Called { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Called = true;
            beforeThrow();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
