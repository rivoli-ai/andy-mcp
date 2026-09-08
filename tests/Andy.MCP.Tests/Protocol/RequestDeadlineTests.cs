using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class RequestDeadlineTests
{
    [Fact]
    public async Task ProgressResetsIdleTimeout_ButCannotExtendMaximum()
    {
        var clock = new Clock();
        using var tracker = new PendingRequestTracker();
        var request = tracker.Track(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), clock);
        request.ProgressToken = "progress";
        clock.Advance(4);
        Assert.True(tracker.TryReportProgress("progress", 1, null, null));
        clock.Advance(4);
        Assert.False(request.Task.IsCompleted);
        Assert.True(tracker.TryReportProgress("progress", 2, null, null));
        clock.Advance(2);
        await Assert.ThrowsAsync<TimeoutException>(() => request.Task);
        Assert.Equal(0, tracker.Count);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.False(tracker.TryComplete(1, JsonRpcResponse.Success(1)));
    }

    [Fact]
    public async Task InvalidOrRepeatedProgress_DoesNotKeepRequestAlive()
    {
        var clock = new Clock();
        using var tracker = new PendingRequestTracker();
        var request = tracker.Track(1, TimeSpan.FromSeconds(5), null, clock);
        request.ProgressToken = "p";
        Assert.True(tracker.TryReportProgress("p", 1, null, null));
        clock.Advance(4);
        Assert.False(tracker.TryReportProgress("p", 1, null, null));
        Assert.False(tracker.TryReportProgress("p", double.NaN, null, null));
        clock.Advance(1);
        await Assert.ThrowsAsync<TimeoutException>(() => request.Task);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public async Task CompletionAndDisposal_ReleaseTimersAndRequests()
    {
        var clock = new Clock();
        using var tracker = new PendingRequestTracker();
        var first = tracker.Track(1, TimeSpan.FromSeconds(5), null, clock);
        Assert.True(tracker.TryComplete(1, JsonRpcResponse.Success(1)));
        await first.Task;
        Assert.Equal(0, clock.ActiveTimers);
        var second = tracker.Track(2, TimeSpan.FromSeconds(5), null, clock);
        second.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Task);
        Assert.Equal(0, tracker.Count);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task ThrowingCancellationCallback_DoesNotPreventOtherRequestCleanup()
    {
        using var tracker = new PendingRequestTracker();
        var a = tracker.Track(1);
        var b = tracker.Track(2);
        using var registration = a.CancellationToken.Register(() => throw new InvalidOperationException("callback"));
        tracker.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.Task);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Task);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public async Task InfiniteTimeout_RemainsPendingUntilResponse()
    {
        var clock = new Clock();
        using var tracker = new PendingRequestTracker();
        var request = tracker.Track(1, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan, clock);
        clock.Advance(10000);
        Assert.False(request.Task.IsCompleted);
        Assert.Equal(0, clock.ActiveTimers);
        tracker.TryComplete(1, JsonRpcResponse.Success(1));
        await request.Task;
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly List<Timer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public int ActiveTimers => _timers.Count(t => !t.Disposed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(int seconds)
        {
            _ticks += TimeSpan.FromSeconds(seconds).Ticks;
            foreach (var timer in _timers.ToArray())
                if (!timer.Disposed && timer.Due <= _ticks) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public bool Disposed { get; private set; }
            public long Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return !Disposed;
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
