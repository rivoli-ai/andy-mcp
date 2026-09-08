using Andy.MCP.Auth;

namespace Andy.MCP.Examples;

/// <summary>Explicit DCR after discovery and application-selected issuer verification.</summary>
public static class OAuthDiscoveryExample
{
    public static async Task<ClientRegistrationResponse> DiscoverAndRegisterAsync(
        Uri resource, string trustedIssuer, ClientRegistrationRequest registration,
        OAuthClient? oauth = null, DynamicClientRegistrationClient? registrar = null,
        CancellationToken ct = default)
    {
        oauth ??= new OAuthClient();
        registrar ??= new DynamicClientRegistrationClient();
        var protectedResource = await oauth.DiscoverResourceMetadataAsync(resource, ct);
        if (!protectedResource.AuthorizationServers.Contains(trustedIssuer, StringComparer.Ordinal))
            throw new InvalidOperationException("The resource did not advertise the application's selected issuer.");
        var authorizationServer = await oauth.DiscoverAuthServerMetadataAsync(trustedIssuer, ct);
        var endpoint = authorizationServer.RegistrationEndpoint
            ?? throw new InvalidOperationException("The selected issuer does not advertise DCR; configure a pre-registered client or CIMD.");
        // Calling this function explicitly chooses registration. Persist returned credentials securely.
        return await registrar.RegisterAsync(endpoint, registration, ct: ct);
    }
}
