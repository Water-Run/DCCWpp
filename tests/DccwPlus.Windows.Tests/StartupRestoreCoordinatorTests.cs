using DccwPlus.Windows;
using Xunit;

namespace DccwPlus.Windows.Tests;

public sealed class StartupRestoreCoordinatorTests
{
    [Fact]
    public async Task RestoreRetriesAfterAMinuteLongPreviewWithoutAnotherDisplayEvent()
    {
        var clock = new FakeTime();
        List<TimeSpan> attempts = [];
        await RunAsync(clock, TimeSpan.FromSeconds(75), (enabled, trigger) =>
        {
            Assert.True(enabled);
            Assert.Equal("sign_in", trigger);
            attempts.Add(clock.Now);
            return Report(clock.Now < TimeSpan.FromMinutes(1) ? "preview_busy" : "restore_finished");
        });

        Assert.Equal(new[] { 0, 15, 30, 45, 60 }.Select(seconds => TimeSpan.FromSeconds(seconds)), attempts);
    }

    [Fact]
    public async Task DisablingStartupCancelsAPendingRetry()
    {
        var clock = new FakeTime();
        List<bool> attempts = [];
        await RunAsync(clock, TimeSpan.FromSeconds(5), (enabled, _) =>
        {
            attempts.Add(enabled);
            return Report(enabled ? "preview_busy" : "startup_disabled");
        });

        Assert.Equal(new[] { true, false }, attempts);
    }

    [Fact]
    public async Task DisableDetectedInsideTheRestoreStopsTheMonitorWithoutWaitingForTheNextPoll()
    {
        var clock = new FakeTime();
        int attempts = 0;
        await RunAsync(clock, TimeSpan.FromSeconds(5), (enabled, _) =>
        {
            Assert.True(enabled);
            attempts++;
            return Report("startup_disabled");
        }, _ => throw new InvalidOperationException("The monitor should exit after a restore detects the policy change."));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ResumeTakesPriorityOverDisplayChangesWhileAPreviewIsActive()
    {
        var clock = new FakeTime();
        List<(TimeSpan Time, string Trigger)> attempts = [];
        var events = new Queue<string>(["resume", "display_changed"]);
        await RunAsync(clock, TimeSpan.FromSeconds(30), (enabled, trigger) =>
        {
            Assert.True(enabled);
            attempts.Add((clock.Now, trigger));
            return Report(clock.Now == TimeSpan.Zero ? "preview_busy" : "restore_finished");
        }, wait =>
        {
            if (!events.TryDequeue(out string? signal)) return clock.WaitAsync(wait);
            Assert.True(wait > TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult<string?>(signal);
        });

        Assert.Equal(new[] { (TimeSpan.Zero, "sign_in"), (TimeSpan.FromSeconds(15), "resume") }, attempts);
    }

    [Fact]
    public async Task APreviewThatNeverReleasesStopsRetryingAtTheDeadline()
    {
        var clock = new FakeTime();
        List<TimeSpan> attempts = [];
        await RunAsync(clock, TimeSpan.FromMinutes(32), (enabled, _) =>
        {
            Assert.True(enabled);
            attempts.Add(clock.Now);
            return Report("preview_busy");
        });

        Assert.Equal(125, attempts.Count);
        Assert.Equal(TimeSpan.FromMinutes(31), attempts[^1]);
    }

    [Theory]
    [InlineData("recovery_required", null, 3)]
    [InlineData("startup_unavailable", null, 3)]
    [InlineData("restore_finished", "display_disconnected", 3)]
    [InlineData("restore_finished", "display_state_changed", 3)]
    [InlineData("restore_finished", "profile_state_unavailable", 3)]
    [InlineData("restore_finished", "profile_file_unavailable", 3)]
    [InlineData("restore_finished", "curve_read_failed", 3)]
    [InlineData("restore_finished", "external_curve_change", 1)]
    [InlineData("restore_finished", "profile_selection_changed", 1)]
    [InlineData("restore_finished", "profile_file_changed", 1)]
    public async Task OnlyTransientResultsReceiveTheShortRetries(string reason, string? bindingReason, int expectedAttempts)
    {
        var clock = new FakeTime();
        List<TimeSpan> attempts = [];
        await RunAsync(clock, TimeSpan.FromSeconds(30), (enabled, _) =>
        {
            Assert.True(enabled);
            attempts.Add(clock.Now);
            return Report(reason, bindingReason);
        });

        Assert.Equal(Enumerable.Range(0, expectedAttempts).Select(attempt => TimeSpan.FromSeconds(attempt * 3)), attempts);
    }

    [Fact]
    public async Task APreviouslyUnavailablePathTriggersRestoreEvenWhenTheLayoutReturnsUnchanged()
    {
        var clock = new FakeTime();
        List<(TimeSpan Time, string Trigger)> attempts = [];
        await RunAsync(clock, TimeSpan.FromSeconds(60), (enabled, trigger) =>
        {
            Assert.True(enabled);
            attempts.Add((clock.Now, trigger));
            return Report("restore_finished");
        }, readConfiguration: () => clock.Now == TimeSpan.FromSeconds(15) ? null : "same-layout");

        Assert.Equal(new[] { (TimeSpan.Zero, "sign_in"), (TimeSpan.FromSeconds(30), "display_changed") }, attempts);
    }

    [Fact]
    public async Task PendingBindingCompletionRetriesWhenTheProfilesAreStillLoadingAtSignIn()
    {
        var clock = new FakeTime();
        var session = Guid.NewGuid();
        List<TimeSpan> attempts = [];
        await RunAsync(clock, TimeSpan.FromSeconds(30), (enabled, trigger) =>
        {
            Assert.True(enabled);
            Assert.Equal("sign_in", trigger);
            attempts.Add(clock.Now);
            var recovery = clock.Now == TimeSpan.Zero ? new InterruptedPreviewResult(session, "binding", "pending", "profile_state_unavailable", true)
                : new InterruptedPreviewResult(session, "binding", "saved", "binding_saved", false);
            return Report("no_bindings_enabled") with { Recoveries = [recovery] };
        });

        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3) }, attempts);
    }

    private static Task RunAsync(FakeTime clock, TimeSpan stopAt, Func<bool, string, CalibrationRestoreReport> restore,
        Func<TimeSpan, Task<string?>>? wait = null, Func<string?>? readConfiguration = null)
    {
        var coordinator = new StartupRestoreCoordinator(() => Task.FromResult(clock.Now < stopAt),
            (enabled, trigger) => Task.FromResult(restore(enabled, trigger)), readConfiguration ?? (() => "same-layout"), clock);
        return coordinator.RunAsync(wait ?? clock.WaitAsync);
    }

    private static CalibrationRestoreReport Report(string reason, string? bindingReason = null) =>
        new(1, false, reason, default, bindingReason is null ? [] : [new("fixture", null, "skipped", bindingReason)], []);

    private sealed class FakeTime : TimeProvider
    {
        public TimeSpan Now { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
        public void Advance(TimeSpan delay) => Now += delay;
        public Task<string?> WaitAsync(TimeSpan delay)
        {
            Assert.InRange(delay, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(15));
            Advance(delay);
            return Task.FromResult<string?>(null);
        }
    }
}
