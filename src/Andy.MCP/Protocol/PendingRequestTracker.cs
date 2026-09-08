using System.Collections.Concurrent;

namespace Andy.MCP.Protocol;

/// <summary>Correlates outbound requests and owns their deadline resources.</summary>
public sealed class PendingRequestTracker : IDisposable
{
    private readonly ConcurrentDictionary<RequestId, PendingRequest> _pending = new();
    private readonly object _gate = new();
    private bool _disposed;

    public PendingRequest Track(RequestId id, TimeSpan? timeout = null) => Track(id, timeout, null);

    /// <summary>Track an idle timeout that progress can reset, bounded by a fixed maximum duration.</summary>
    public PendingRequest Track(RequestId id, TimeSpan? timeout, TimeSpan? maximumTimeout, TimeProvider? timeProvider = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pending = new PendingRequest(id, timeout, maximumTimeout, timeProvider ?? TimeProvider.System,
                value => ((ICollection<KeyValuePair<RequestId, PendingRequest>>)_pending).Remove(new(id, value)));
            if (!_pending.TryAdd(id, pending))
            {
                pending.Dispose();
                throw new InvalidOperationException($"Request with ID '{id}' is already pending.");
            }
            pending.Start();
            return pending;
        }
    }

    public bool TryComplete(RequestId id, JsonRpcResponse response) =>
        _pending.TryGetValue(id, out var pending) && pending.SetResponse(response);

    public bool TryCancel(RequestId id, string? reason = null) =>
        _pending.TryGetValue(id, out var pending) && pending.Cancel(reason);

    public bool TryReportProgress(RequestId progressToken, double progress, double? total, string? message)
    {
        foreach (var pending in _pending.Values)
            if (pending.ProgressToken is { } token && token.Equals(progressToken))
                return pending.ReportProgress(progress, total, message);
        return false;
    }

    public bool IsPending(RequestId id) => _pending.ContainsKey(id);
    public int Count => _pending.Count;
    public void CancelAll(string? reason = null)
    {
        foreach (var pending in _pending.Values) pending.Cancel(reason);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        CancelAll("Tracker disposed");
    }
}

/// <summary>A pending response with monotonic progress, idle timeout and absolute deadline.</summary>
public sealed class PendingRequest : IDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource<JsonRpcResponse> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private readonly TimeProvider _clock;
    private readonly TimeSpan? _idle;
    private readonly TimeSpan? _maximum;
    private readonly Action<PendingRequest> _remove;
    private ITimer? _timer;
    private long _started, _activity;
    private bool _finished;
    private double _lastProgress = double.NegativeInfinity;
    private IProgress<McpProgress>? _progress;
    public RequestId Id { get; }
    public RequestId? ProgressToken { get; set; }
    public CancellationToken CancellationToken => _token;
    public Task<JsonRpcResponse> Task => _tcs.Task;

    internal PendingRequest(RequestId id, TimeSpan? idle, TimeSpan? maximum, TimeProvider clock, Action<PendingRequest> remove)
    {
        if (idle == Timeout.InfiniteTimeSpan) idle = null;
        if (maximum == Timeout.InfiniteTimeSpan) maximum = null;
        if (idle is { } i && i < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idle));
        if (maximum is { } m && m <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximum));
        Id = id; _idle = idle; _maximum = maximum; _clock = clock; _remove = remove; _token = _cts.Token;
    }

    internal void Start()
    {
        lock (_gate)
        {
            _started = _activity = _clock.GetTimestamp();
            if (_idle is not null || _maximum is not null)
            {
                _timer = _clock.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                Schedule();
            }
        }
    }

    private TimeSpan Remaining()
    {
        var idle = _idle is { } i ? i - _clock.GetElapsedTime(_activity) : TimeSpan.MaxValue;
        var maximum = _maximum is { } m ? m - _clock.GetElapsedTime(_started) : TimeSpan.MaxValue;
        return idle < maximum ? idle : maximum;
    }

    private void Schedule()
    {
        var due = Remaining();
        _timer?.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void Expire()
    {
        lock (_gate)
        {
            if (_finished) return;
            if (Remaining() > TimeSpan.Zero) { Schedule(); return; }
            Finish();
            _tcs.TrySetException(new TimeoutException($"Request '{Id}' timed out (idle timeout or maximum duration)."));
            CancelToken();
        }
    }

    public void OnProgress(IProgress<McpProgress> handler) { lock (_gate) _progress = handler; }

    internal bool ReportProgress(double progress, double? total, string? message)
    {
        lock (_gate)
        {
            if (_finished || !double.IsFinite(progress) || progress < 0 || progress <= _lastProgress ||
                (total is { } t && (!double.IsFinite(t) || t < 0))) return false;
            if (_timer is not null && Remaining() <= TimeSpan.Zero) { Expire(); return false; }
            _lastProgress = progress;
            _activity = _clock.GetTimestamp();
            Schedule();
            _progress?.Report(new McpProgress(progress, total, message));
            return true;
        }
    }

    internal bool SetResponse(JsonRpcResponse response)
    {
        lock (_gate)
        {
            if (_finished) return false;
            Finish(); _tcs.TrySetResult(response); _cts.Dispose(); return true;
        }
    }

    internal bool Cancel(string? reason)
    {
        lock (_gate)
        {
            if (_finished) return false;
            Finish(); _tcs.TrySetCanceled(_token); CancelToken(); return true;
        }
    }

    private void CancelToken()
    {
        try { _cts.Cancel(); } catch (AggregateException) { /* Callback failures cannot prevent request cleanup. */ } finally { _cts.Dispose(); }
    }

    private void Finish()
    {
        _finished = true; _timer?.Dispose(); _timer = null; _remove(this);
    }
    public void Dispose() => Cancel("Request disposed");
}

/// <summary>Progress data for a long-running operation.</summary>
public readonly record struct McpProgress(double Progress, double? Total, string? Message);
