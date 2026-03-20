using System.Collections.Concurrent;

namespace Andy.MCP.Auth;

/// <summary>
/// Pluggable storage for OAuth tokens. Consumers can implement file/keychain persistence.
/// </summary>
public interface ITokenStore
{
    Task SaveTokensAsync(string serverUri, OAuthTokens tokens);
    Task<OAuthTokens?> LoadTokensAsync(string serverUri);
    Task ClearTokensAsync(string serverUri);
}

/// <summary>
/// In-memory token store. Tokens are lost when the process exits.
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<string, OAuthTokens> _tokens = new();

    public Task SaveTokensAsync(string serverUri, OAuthTokens tokens)
    {
        _tokens[serverUri] = tokens;
        return Task.CompletedTask;
    }

    public Task<OAuthTokens?> LoadTokensAsync(string serverUri)
    {
        _tokens.TryGetValue(serverUri, out var tokens);
        return Task.FromResult(tokens);
    }

    public Task ClearTokensAsync(string serverUri)
    {
        _tokens.TryRemove(serverUri, out _);
        return Task.CompletedTask;
    }
}
