using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Protocol;

public class PendingRequestTrackerTests
{
    [Fact]
    public async Task Track_AndComplete_ResolvesTask()
    {
        using var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1);

        var response = JsonRpcResponse.Success((RequestId)1);
        Assert.True(tracker.TryComplete((RequestId)1, response));

        var result = await pending.Task;
        Assert.True(result.IsSuccess);
        Assert.Equal(1L, result.Id.AsNumber());
    }

    [Fact]
    public void Track_DuplicateId_Throws()
    {
        using var tracker = new PendingRequestTracker();
        tracker.Track((RequestId)1);

        Assert.Throws<InvalidOperationException>(() => tracker.Track((RequestId)1));
    }

    [Fact]
    public void TryComplete_UnknownId_ReturnsFalse()
    {
        using var tracker = new PendingRequestTracker();
        var response = JsonRpcResponse.Success((RequestId)99);

        Assert.False(tracker.TryComplete((RequestId)99, response));
    }

    [Fact]
    public async Task TryCancel_FiresCancellationToken()
    {
        using var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1);

        Assert.False(pending.CancellationToken.IsCancellationRequested);
        Assert.True(tracker.TryCancel((RequestId)1, "test cancel"));
        Assert.True(pending.CancellationToken.IsCancellationRequested);

        await Assert.ThrowsAsync<TaskCanceledException>(() => pending.Task);
    }

    [Fact]
    public void TryCancel_UnknownId_ReturnsFalse()
    {
        using var tracker = new PendingRequestTracker();
        Assert.False(tracker.TryCancel((RequestId)99));
    }

    [Fact]
    public void TryCancel_AlreadyCompleted_ReturnsFalse()
    {
        using var tracker = new PendingRequestTracker();
        tracker.Track((RequestId)1);

        var response = JsonRpcResponse.Success((RequestId)1);
        tracker.TryComplete((RequestId)1, response);

        // Already removed from tracker
        Assert.False(tracker.TryCancel((RequestId)1));
    }

    [Fact]
    public void IsPending_ReturnsCorrectly()
    {
        using var tracker = new PendingRequestTracker();
        Assert.False(tracker.IsPending((RequestId)1));

        tracker.Track((RequestId)1);
        Assert.True(tracker.IsPending((RequestId)1));

        tracker.TryComplete((RequestId)1, JsonRpcResponse.Success((RequestId)1));
        Assert.False(tracker.IsPending((RequestId)1));
    }

    [Fact]
    public void Count_TracksCorrectly()
    {
        using var tracker = new PendingRequestTracker();
        Assert.Equal(0, tracker.Count);

        tracker.Track((RequestId)1);
        tracker.Track((RequestId)2);
        Assert.Equal(2, tracker.Count);

        tracker.TryComplete((RequestId)1, JsonRpcResponse.Success((RequestId)1));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public async Task CancelAll_CancelsAllPending()
    {
        using var tracker = new PendingRequestTracker();
        var p1 = tracker.Track((RequestId)1);
        var p2 = tracker.Track((RequestId)2);
        var p3 = tracker.Track((RequestId)3);

        tracker.CancelAll("shutdown");

        Assert.Equal(0, tracker.Count);
        await Assert.ThrowsAsync<TaskCanceledException>(() => p1.Task);
        await Assert.ThrowsAsync<TaskCanceledException>(() => p2.Task);
        await Assert.ThrowsAsync<TaskCanceledException>(() => p3.Task);
    }

    [Fact]
    public async Task Timeout_FiresAfterDuration()
    {
        using var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1, timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => pending.Task);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Timeout_DoesNotFire_WhenCompletedBeforeTimeout()
    {
        using var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1, timeout: TimeSpan.FromSeconds(10));

        tracker.TryComplete((RequestId)1, JsonRpcResponse.Success((RequestId)1));

        var result = await pending.Task;
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Progress_ReportedToHandler()
    {
        using var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1);
        pending.ProgressToken = (RequestId)"prog-1";

        var reports = new List<McpProgress>();
        pending.OnProgress(new SynchronousProgress<McpProgress>(p => reports.Add(p)));

        tracker.TryReportProgress((RequestId)"prog-1", 1, 10, "Step 1");
        tracker.TryReportProgress((RequestId)"prog-1", 5, 10, "Step 5");

        Assert.Equal(2, reports.Count);
        Assert.Equal(1.0, reports[0].Progress);
        Assert.Equal(10.0, reports[0].Total);
        Assert.Equal("Step 1", reports[0].Message);
        Assert.Equal(5.0, reports[1].Progress);
    }

    /// <summary>
    /// IProgress implementation that invokes the callback synchronously (no SynchronizationContext posting).
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public void Progress_UnknownToken_ReturnsFalse()
    {
        using var tracker = new PendingRequestTracker();
        Assert.False(tracker.TryReportProgress((RequestId)"unknown", 1, 10, null));
    }

    [Fact]
    public async Task ConcurrentOperations_ThreadSafe()
    {
        using var tracker = new PendingRequestTracker();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            var id = (RequestId)i;
            tasks.Add(Task.Run(() =>
            {
                var pending = tracker.Track(id);
                tracker.TryComplete(id, JsonRpcResponse.Success(id));
            }));
        }

        await Task.WhenAll(tasks);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Dispose_CancelsAllAndPreventsNewTracking()
    {
        var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1);

        tracker.Dispose();

        Assert.True(pending.CancellationToken.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => tracker.Track((RequestId)2));
    }

    [Fact]
    public async Task LateResponse_AfterCancel_IsDiscarded()
    {
        using var tracker = new PendingRequestTracker();
        var pending = tracker.Track((RequestId)1);

        // Cancel it
        tracker.TryCancel((RequestId)1);
        await Assert.ThrowsAsync<TaskCanceledException>(() => pending.Task);

        // Late response arrives — TryComplete returns false (already removed)
        Assert.False(tracker.TryComplete((RequestId)1, JsonRpcResponse.Success((RequestId)1)));
    }
}
