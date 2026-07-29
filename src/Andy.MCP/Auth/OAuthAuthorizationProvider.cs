namespace Andy.MCP.Auth;

/// <summary>
/// Host boundary for external authorization interaction. The library owns PKCE, state, callback
/// validation, code exchange, and token persistence.
/// </summary>
public interface IOAuthAuthorizationProvider
{
    Task<OAuthAuthorizationCallback?> AuthorizeAsync(
        OAuthAuthorizationInteraction interaction,
        CancellationToken cancellationToken = default);
}

/// <summary>The only authorization details exposed to a host interaction provider.</summary>
public sealed record OAuthAuthorizationInteraction
{
    public required Uri AuthorizationUri { get; init; }
    public required Uri RedirectUri { get; init; }
}

/// <summary>Host-returned callback URI after external authorization interaction.</summary>
public sealed record OAuthAuthorizationCallback
{
    public required Uri CallbackUri { get; init; }
}
