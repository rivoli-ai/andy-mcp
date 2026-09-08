# HTTP authorization and local configuration

Protected HTTP endpoints fail closed unless configured with a resource/issuer policy.
The ASP.NET Core authentication middleware must validate the token signature, lifetime and
issuer before assigning HttpContext.User. Use the host's JWT bearer authentication handler;
never construct a principal directly from an unvalidated JWT payload.

Configure the host's JWT bearer scheme with Authority matching the authorization-server issuer,
Audience matching the canonical MCP resource URI, and signature/lifetime validation enabled.
Call UseAuthentication and UseAuthorization before serving the endpoint, then register:

    app.MapMcp("/mcp", server => { /* register tools */ },
        new StreamableHttpServerOptions
        {
            Authorization = new McpHttpAuthorizationOptions
            {
                Resource = "https://api.example/mcp",
                Issuer = "https://auth.example",
                RequiredScopes = ["tools:read"]
            },
            ValidateOrigin = origin => origin == "https://ui.example"
        });

The handler checks authentication, issuer, audience and ordinal case-sensitive scope membership
on every POST, GET and DELETE. Scope claims may be named scope or scp. It accepts sub or
NameIdentifier as the subject and binds sessions to issuer plus subject. Missing/invalid identity
returns 401 with a protected-resource metadata challenge; insufficient scope returns 403.
Metadata is automatically mapped at /.well-known/oauth-protected-resource/mcp and is public.

The default rejects every present Origin unless the allow-list callback accepts it. Requests
without Origin are allowed by the Origin check but still require authorization. Multiple Origin
values are rejected. Tokens must never be forwarded to upstream tools; obtain separate credentials
for each upstream resource.

For a trusted local deployment, explicitly select:

    new StreamableHttpServerOptions { AllowAnonymous = true }

Bind such a host to loopback or a trusted private interface. This opt-out does not disable Origin,
body-size, session-count or session-isolation checks. An unconfigured protected endpoint returns
503 rather than silently becoming anonymous.

Sessions expire after SessionTimeout inactivity, release waiting POSTs on close, and are disposed
when the host stops. POST activity and authorized GET/DELETE access renew activity. Configure a
timeout appropriate for long-running work. MapMcp binds any supplied shared task store to the HTTP
session; the serverOptions argument allows supplying that store without sharing task ownership.

2026-09-08: added fail-closed authorization, public metadata mapping, session expiry and tests.
