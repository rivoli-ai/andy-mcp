# OAuth discovery and registration

The host chooses the trusted issuer, registration strategy, redirect URI, token storage and
user interaction. Default library HTTP clients enforce HTTPS metadata/registration endpoints,
disable redirects/proxies and pin connections to vetted public DNS addresses. Injecting a
custom HttpClient transfers those connection controls to the host. Test doubles use injected
clients so CI never contacts a real identity provider.

## Explicit discovery and DCR

The compiled [OAuthDiscoveryExample](../examples/Andy.MCP.Examples/OAuthDiscoveryExample.cs)
discovers RFC9728 protected-resource metadata, verifies that it advertises the issuer selected
by the application, discovers RFC8414/OIDC metadata, and uses its registration endpoint only
when the caller explicitly requests DCR. It throws if the issuer is unadvertised or DCR is absent.

```csharp
var registration = await OAuthDiscoveryExample.DiscoverAndRegisterAsync(
    new Uri("https://api.example.com/mcp"),
    "https://auth.example.com",
    new ClientRegistrationRequest
    {
        ClientName = "documented-client",
        RedirectUris = ["https://client.example.com/callback"],
        TokenEndpointAuthMethod = "none"
    }, ct: cancellationToken);
```

This is a public-client example; choose metadata appropriate to your deployment. Persist
returned registration credentials securely. Do not log registration access tokens, client
secrets, authorization codes or user tokens. The example's exact source compiles in both the
examples project and [OAuthExampleTests](../tests/Andy.MCP.Tests/Conformance/OAuthExampleTests.cs),
which executes discovery/registration and proves an unadvertised issuer never receives a call.

For already registered clients, use their configured client ID. CIMD is selected only when
advertised by the authorization server and supplied/validated by the host; the library does
not host or fetch the document. RFC7592 `GetConfigurationAsync`, `UpdateConfigurationAsync`
and `DeleteConfigurationAsync` manage DCR state. PUT replacement metadata is explicit and
complete: omitted values request removal. Credential rotation updates the registration state.

## Authorization, refresh and scope changes

`OAuthClient.AuthorizeInteractiveAsync` owns PKCE S256, state, callback validation, code
exchange and token persistence. An `IOAuthAuthorizationProvider` receives authorization and
redirect URIs and returns the callback URI; the host implements browser/user interaction.
The library does not substitute for application consent or identity-provider policy.

`OAuthDelegatingHandler` parses resource challenges and discovers metadata on 401. It refreshes
or surfaces the challenge instead of blindly retrying. Interactive scope step-up requires a
configured provider/redirect URI and a Bearer 403 `insufficient_scope` challenge with scopes.
It requests the case-sensitive scope union, coordinates concurrent upgrades and retries once
only after a new token covers the request. Failure retains the original response/token.

Tokens are bound to the target resource. Never forward an incoming user's MCP token to an
upstream tool or a different resource; obtain separate credentials. Configure server-side
signature/lifetime/issuer validation and audience/scopes as described in
[HTTP security](http-security.md). Registration credentials and resource access tokens serve
different purposes and must not be interchanged.

Verification: [OAuthDiscoveryTests](../tests/Andy.MCP.Tests/Auth/OAuthDiscoveryTests.cs),
[OAuth401DiscoveryTests](../tests/Andy.MCP.Tests/Auth/OAuth401DiscoveryTests.cs),
[registration management](../tests/Andy.MCP.Tests/Auth/DynamicClientRegistrationManagementTests.cs),
[interactive authorization](../tests/Andy.MCP.Tests/Auth/OAuthInteractiveAuthorizationTests.cs),
[scope step-up](../tests/Andy.MCP.Tests/Auth/OAuthScopeStepUpTests.cs).
