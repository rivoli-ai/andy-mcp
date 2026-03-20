using System.Collections.Concurrent;

namespace Andy.MCP.Server;

/// <summary>
/// Tracks resource subscriptions and notifies when resources change.
/// </summary>
public sealed class ResourceSubscriptionManager
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new();
    // uri → set of session IDs

    /// <summary>
    /// Subscribe a session to a resource URI.
    /// </summary>
    public void Subscribe(string uri, string sessionId = "default")
    {
        _subscriptions.AddOrUpdate(uri,
            _ => new HashSet<string> { sessionId },
            (_, set) => { lock (set) { set.Add(sessionId); } return set; });
    }

    /// <summary>
    /// Unsubscribe a session from a resource URI.
    /// </summary>
    public void Unsubscribe(string uri, string sessionId = "default")
    {
        if (_subscriptions.TryGetValue(uri, out var set))
        {
            lock (set)
            {
                set.Remove(sessionId);
                if (set.Count == 0)
                    _subscriptions.TryRemove(uri, out _);
            }
        }
    }

    /// <summary>
    /// Check if any session is subscribed to a resource URI.
    /// </summary>
    public bool HasSubscribers(string uri) =>
        _subscriptions.TryGetValue(uri, out var set) && set.Count > 0;

    /// <summary>
    /// Get all URIs that have subscribers.
    /// </summary>
    public IReadOnlyList<string> GetSubscribedUris() =>
        _subscriptions.Keys.ToList();

    /// <summary>
    /// Remove all subscriptions for a session (e.g., on disconnect).
    /// </summary>
    public void RemoveSession(string sessionId = "default")
    {
        foreach (var kvp in _subscriptions)
        {
            lock (kvp.Value)
            {
                kvp.Value.Remove(sessionId);
            }
        }
        // Clean up empty sets
        foreach (var uri in _subscriptions.Keys.ToList())
        {
            if (_subscriptions.TryGetValue(uri, out var set) && set.Count == 0)
                _subscriptions.TryRemove(uri, out _);
        }
    }
}
