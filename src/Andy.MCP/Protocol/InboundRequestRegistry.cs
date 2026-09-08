namespace Andy.MCP.Protocol;

/// <summary>Owns inbound handler cancellation separately from outbound response correlation.</summary>
internal sealed class InboundRequestRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<RequestId, Entry> _entries = new();
    private bool _stopped;

    internal int Count { get { lock (_gate) return _entries.Count; } }

    public bool Run(RequestId id, CancellationToken parent, Func<CancellationToken, Task> handler)
    {
        lock (_gate)
        {
            if (_stopped || _entries.ContainsKey(id)) return false;
            var entry = new Entry(CancellationTokenSource.CreateLinkedTokenSource(parent));
            _entries.Add(id, entry);
            entry.Task = Task.Run(async () =>
            {
                try { await handler(entry.Cancellation.Token); }
                finally
                {
                    lock (_gate) _entries.Remove(id);
                    entry.Cancellation.Dispose();
                }
            });
            return true;
        }
    }

    public void Cancel(RequestId id)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(id, out entry);
        if (entry is not null) Cancel(entry);
    }

    private static void Cancel(Entry entry)
    {
        try { entry.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Continue cancelling and draining the remaining handlers. */ }
    }

    public async Task StopAsync()
    {
        Entry[] entries;
        lock (_gate) { _stopped = true; entries = _entries.Values.ToArray(); }
        foreach (var entry in entries) Cancel(entry);
        await Task.WhenAll(entries.Select(e => e.Task));
    }

    private sealed class Entry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
