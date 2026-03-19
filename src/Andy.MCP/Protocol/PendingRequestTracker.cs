using System.Collections.Concurrent;
using System.Text.Json;

namespace Andy.MCP.Protocol;

/// <summary>
/// Tracks in-flight JSON-RPC requests, enabling response correlation,
/// cancellation, and progress reporting.
/// </summary>
public sealed class PendingRequestTracker : IDisposable
{
    private readonly ConcurrentDictionary<RequestId, PendingRequest> _pending = new();
    private bool _disposed;

    /// <summary>
    /// Register a new outgoing request and get a task that completes when the response arrives.
    /// </summary>
    public PendingRequest Track(RequestId id, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pending = new PendingRequest(id, timeout);
        if (!_pending.TryAdd(id, pending))
        {
            pending.Dispose();
            throw new InvalidOperationException($"Request with ID '{id}' is already pending.");
        }
        return pending;
    }

    /// <summary>
    /// Complete a pending request with a response.
    /// Returns true if the request was found and completed.
    /// </summary>
    public bool TryComplete(RequestId id, JsonRpcResponse response)
    {
        if (_pending.TryRemove(id, out var pending))
        {
            pending.SetResponse(response);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Cancel a pending request. Fires the CancellationToken and removes it from tracking.
    /// Returns true if the request was found and cancelled.
    /// </summary>
    public bool TryCancel(RequestId id, string? reason = null)
    {
        if (_pending.TryRemove(id, out var pending))
        {
            pending.Cancel(reason);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Report progress on a pending request identified by its progress token.
    /// Returns true if a matching request was found.
    /// </summary>
    public bool TryReportProgress(RequestId progressToken, double progress, double? total, string? message)
    {
        // Progress tokens match the request's progress token, not the request ID.
        // We need to search for it.
        foreach (var kvp in _pending)
        {
            if (kvp.Value.ProgressToken is { } token && token.Equals(progressToken))
            {
                kvp.Value.ReportProgress(progress, total, message);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check if a request is currently pending.
    /// </summary>
    public bool IsPending(RequestId id) => _pending.ContainsKey(id);

    /// <summary>
    /// Get the count of pending requests.
    /// </summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Cancel all pending requests and clear the tracker.
    /// </summary>
    public void CancelAll(string? reason = null)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var pending))
            {
                pending.Cancel(reason ?? "Session closing");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelAll("Tracker disposed");
        foreach (var kvp in _pending)
        {
            kvp.Value.Dispose();
        }
        _pending.Clear();
    }
}

/// <summary>
/// Represents a single in-flight request with response completion, cancellation, and progress.
/// </summary>
public sealed class PendingRequest : IDisposable
{
    private readonly TaskCompletionSource<JsonRpcResponse> _tcs;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource? _timeoutCts;
    private IProgress<McpProgress>? _progressHandler;
    private bool _disposed;

    public RequestId Id { get; }
    public RequestId? ProgressToken { get; set; }
    public CancellationToken CancellationToken => _cts.Token;
    public Task<JsonRpcResponse> Task => _tcs.Task;

    internal PendingRequest(RequestId id, TimeSpan? timeout)
    {
        Id = id;
        _tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();

        if (timeout.HasValue)
        {
            _timeoutCts = new CancellationTokenSource(timeout.Value);
            _timeoutCts.Token.Register(() =>
            {
                _tcs.TrySetException(new TimeoutException($"Request '{id}' timed out after {timeout.Value}."));
                _cts.Cancel();
            });
        }
    }

    /// <summary>
    /// Set a progress handler for this request.
    /// </summary>
    public void OnProgress(IProgress<McpProgress> handler)
    {
        _progressHandler = handler;
    }

    internal void SetResponse(JsonRpcResponse response)
    {
        _tcs.TrySetResult(response);
    }

    internal void Cancel(string? reason)
    {
        _tcs.TrySetCanceled();
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
    }

    internal void ReportProgress(double progress, double? total, string? message)
    {
        _progressHandler?.Report(new McpProgress(progress, total, message));

        // Reset timeout on progress (if timeout is being tracked, extend it)
        // Note: CancellationTokenSource doesn't support reset, so this is best-effort.
        // A production implementation might use a custom timer.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Dispose();
        _timeoutCts?.Dispose();
    }
}

/// <summary>
/// Progress data reported for a long-running MCP operation.
/// </summary>
public readonly record struct McpProgress(double Progress, double? Total, string? Message);
