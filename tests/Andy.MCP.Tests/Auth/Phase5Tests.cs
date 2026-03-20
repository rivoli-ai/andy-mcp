using System.Net;
using System.Text.Json;
using Andy.MCP.Auth;

namespace Andy.MCP.Tests.Auth;

#region PKCE Tests (#16)

public class PkceTests
{
    [Fact]
    public void GenerateCodeVerifier_Length()
    {
        var verifier = PkceHelper.GenerateCodeVerifier(64);
        Assert.Equal(64, verifier.Length);
    }

    [Fact]
    public void GenerateCodeVerifier_MinLength()
    {
        var verifier = PkceHelper.GenerateCodeVerifier(43);
        Assert.Equal(43, verifier.Length);
    }

    [Fact]
    public void GenerateCodeVerifier_MaxLength()
    {
        var verifier = PkceHelper.GenerateCodeVerifier(128);
        Assert.Equal(128, verifier.Length);
    }

    [Fact]
    public void GenerateCodeVerifier_TooShort_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PkceHelper.GenerateCodeVerifier(42));
    }

    [Fact]
    public void GenerateCodeVerifier_TooLong_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PkceHelper.GenerateCodeVerifier(129));
    }

    [Fact]
    public void GenerateCodeVerifier_IsUrlSafe()
    {
        var verifier = PkceHelper.GenerateCodeVerifier();
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void GenerateCodeVerifier_Unique()
    {
        var a = PkceHelper.GenerateCodeVerifier();
        var b = PkceHelper.GenerateCodeVerifier();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCodeChallenge_DeterministicForSameVerifier()
    {
        var verifier = "test-verifier-12345678901234567890123456789012";
        var a = PkceHelper.ComputeCodeChallenge(verifier);
        var b = PkceHelper.ComputeCodeChallenge(verifier);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeCodeChallenge_DifferentForDifferentVerifiers()
    {
        var a = PkceHelper.ComputeCodeChallenge(PkceHelper.GenerateCodeVerifier());
        var b = PkceHelper.ComputeCodeChallenge(PkceHelper.GenerateCodeVerifier());
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Generate_ReturnsPair()
    {
        var (verifier, challenge) = PkceHelper.Generate();
        Assert.NotEmpty(verifier);
        Assert.NotEmpty(challenge);
        Assert.NotEqual(verifier, challenge);
        Assert.Equal(challenge, PkceHelper.ComputeCodeChallenge(verifier));
    }

    [Fact]
    public void GenerateState_Unique()
    {
        var a = PkceHelper.GenerateState();
        var b = PkceHelper.GenerateState();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 20);
    }
}

#endregion

#region OAuth Types Tests (#16)

public class OAuthTypesTests
{
    [Fact]
    public void ProtectedResourceMetadata_Deserializes()
    {
        var json = """
        {
            "resource": "https://mcp.example.com",
            "authorization_servers": ["https://auth.example.com"],
            "scopes_supported": ["openid", "profile"]
        }
        """;

        var metadata = JsonSerializer.Deserialize<ProtectedResourceMetadata>(json)!;
        Assert.Equal("https://mcp.example.com", metadata.Resource);
        Assert.Single(metadata.AuthorizationServers);
        Assert.Equal(2, metadata.ScopesSupported!.Count);
    }

    [Fact]
    public void AuthorizationServerMetadata_Deserializes()
    {
        var json = """
        {
            "issuer": "https://auth.example.com",
            "authorization_endpoint": "https://auth.example.com/authorize",
            "token_endpoint": "https://auth.example.com/token",
            "registration_endpoint": "https://auth.example.com/register",
            "code_challenge_methods_supported": ["S256"]
        }
        """;

        var metadata = JsonSerializer.Deserialize<AuthorizationServerMetadata>(json)!;
        Assert.Equal("https://auth.example.com", metadata.Issuer);
        Assert.Equal("https://auth.example.com/authorize", metadata.AuthorizationEndpoint);
        Assert.Equal("https://auth.example.com/token", metadata.TokenEndpoint);
        Assert.Equal("https://auth.example.com/register", metadata.RegistrationEndpoint);
        Assert.Contains("S256", metadata.CodeChallengeMethodsSupported!);
    }

    [Fact]
    public void OAuthTokens_IsExpired()
    {
        var expired = new OAuthTokens
        {
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        Assert.True(expired.IsExpired);

        var valid = new OAuthTokens
        {
            AccessToken = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        Assert.False(valid.IsExpired);

        var noExpiry = new OAuthTokens { AccessToken = "token" };
        Assert.False(noExpiry.IsExpired);
    }

    [Fact]
    public void ResourceParameter_Valid()
    {
        OAuthClient.ValidateResourceParameter("https://mcp.example.com/mcp");
        OAuthClient.ValidateResourceParameter("https://mcp.example.com");
    }

    [Fact]
    public void ResourceParameter_NoScheme_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            OAuthClient.ValidateResourceParameter("mcp.example.com"));
    }

    [Fact]
    public void ResourceParameter_WithFragment_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            OAuthClient.ValidateResourceParameter("https://mcp.example.com#section"));
    }

    [Fact]
    public void BuildAuthorizationUrl_IncludesAllParams()
    {
        var metadata = new AuthorizationServerMetadata
        {
            Issuer = "https://auth.example.com",
            AuthorizationEndpoint = "https://auth.example.com/authorize",
            TokenEndpoint = "https://auth.example.com/token"
        };

        var url = OAuthClient.BuildAuthorizationUrl(
            metadata, "client-1", "http://localhost:3000/callback",
            "challenge123", "state456", "https://mcp.example.com", "openid profile");

        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=client-1", url);
        Assert.Contains("code_challenge=challenge123", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state456", url);
        Assert.Contains("scope=openid", url);
    }
}

#endregion

#region Token Store Tests (#16)

public class TokenStoreTests
{
    [Fact]
    public async Task InMemory_SaveAndLoad()
    {
        var store = new InMemoryTokenStore();
        var tokens = new OAuthTokens
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await store.SaveTokensAsync("https://server1", tokens);
        var loaded = await store.LoadTokensAsync("https://server1");

        Assert.NotNull(loaded);
        Assert.Equal("access-123", loaded!.AccessToken);
        Assert.Equal("refresh-456", loaded.RefreshToken);
    }

    [Fact]
    public async Task InMemory_LoadNonexistent_ReturnsNull()
    {
        var store = new InMemoryTokenStore();
        Assert.Null(await store.LoadTokensAsync("https://unknown"));
    }

    [Fact]
    public async Task InMemory_Clear()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://server1",
            new OAuthTokens { AccessToken = "token" });

        await store.ClearTokensAsync("https://server1");
        Assert.Null(await store.LoadTokensAsync("https://server1"));
    }

    [Fact]
    public async Task InMemory_IsolatedByServer()
    {
        var store = new InMemoryTokenStore();
        await store.SaveTokensAsync("https://server1",
            new OAuthTokens { AccessToken = "token1" });
        await store.SaveTokensAsync("https://server2",
            new OAuthTokens { AccessToken = "token2" });

        Assert.Equal("token1", (await store.LoadTokensAsync("https://server1"))!.AccessToken);
        Assert.Equal("token2", (await store.LoadTokensAsync("https://server2"))!.AccessToken);
    }
}

#endregion

#region DRC Tests (#17)

public class DynamicClientRegistrationTests
{
    [Fact]
    public void ClientRegistrationRequest_Serializes()
    {
        var req = new ClientRegistrationRequest
        {
            ClientName = "Andy MCP Client",
            RedirectUris = ["http://localhost:3000/callback"],
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
            Scope = "openid profile email"
        };

        var json = JsonSerializer.Serialize(req);
        Assert.Contains("\"client_name\":\"Andy MCP Client\"", json);
        Assert.Contains("authorization_code", json);
    }

    [Fact]
    public void ClientRegistrationResponse_Deserializes()
    {
        var json = """
        {
            "client_id": "dcr_abc123",
            "client_secret": "secret456",
            "client_id_issued_at": 1700000000,
            "client_secret_expires_at": 0,
            "registration_access_token": "rat_xyz",
            "registration_client_uri": "https://auth.example.com/register/dcr_abc123"
        }
        """;

        var resp = JsonSerializer.Deserialize<ClientRegistrationResponse>(json)!;
        Assert.Equal("dcr_abc123", resp.ClientId);
        Assert.Equal("secret456", resp.ClientSecret);
        Assert.Equal("rat_xyz", resp.RegistrationAccessToken);
        Assert.Equal(1700000000, resp.ClientIdIssuedAt);
        Assert.Equal(0, resp.ClientSecretExpiresAt);
    }

    [Fact]
    public void ClientRegistrationResponse_PublicClient_NoSecret()
    {
        var json = """{"client_id": "dcr_public"}""";
        var resp = JsonSerializer.Deserialize<ClientRegistrationResponse>(json)!;
        Assert.Equal("dcr_public", resp.ClientId);
        Assert.Null(resp.ClientSecret);
    }
}

#endregion

#region Security Helpers Tests (#18)

public class SecurityHelpersTests
{
    [Fact]
    public void IsPrivateIp_10x() =>
        Assert.True(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("10.0.0.1")));

    [Fact]
    public void IsPrivateIp_172_16x() =>
        Assert.True(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("172.16.0.1")));

    [Fact]
    public void IsPrivateIp_192_168x() =>
        Assert.True(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("192.168.1.1")));

    [Fact]
    public void IsPrivateIp_LinkLocal() =>
        Assert.True(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("169.254.1.1")));

    [Fact]
    public void IsPrivateIp_Loopback() =>
        Assert.True(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("127.0.0.1")));

    [Fact]
    public void IsPrivateIp_IPv6Loopback() =>
        Assert.True(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.IPv6Loopback));

    [Fact]
    public void IsPrivateIp_PublicIp_False() =>
        Assert.False(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("8.8.8.8")));

    [Fact]
    public void IsPrivateIp_PublicIp2_False() =>
        Assert.False(SecurityHelpers.IsPrivateOrReservedIp(IPAddress.Parse("140.82.121.4")));

    [Fact]
    public void ValidateUrl_ValidHttps() =>
        Assert.True(SecurityHelpers.ValidateUrl("https://example.com").IsValid);

    [Fact]
    public void ValidateUrl_HttpRejectedWhenHttpsRequired() =>
        Assert.False(SecurityHelpers.ValidateUrl("http://example.com", requireHttps: true).IsValid);

    [Fact]
    public void ValidateUrl_HttpAllowedWhenNotRequired() =>
        Assert.True(SecurityHelpers.ValidateUrl("http://example.com", requireHttps: false).IsValid);

    [Fact]
    public void ValidateUrl_InvalidUri() =>
        Assert.False(SecurityHelpers.ValidateUrl("not-a-url").IsValid);

    [Fact]
    public void GenerateSessionId_Length()
    {
        var id = SecurityHelpers.GenerateSessionId(32);
        Assert.Equal(32, id.Length);
    }

    [Fact]
    public void GenerateSessionId_Unique()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => SecurityHelpers.GenerateSessionId()).ToHashSet();
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void GenerateUserBoundSessionId_ContainsUserId()
    {
        var id = SecurityHelpers.GenerateUserBoundSessionId("user-42");
        Assert.StartsWith("user-42:", id);
    }

    [Fact]
    public void ValidateOrigin_Valid()
    {
        Assert.True(SecurityHelpers.ValidateOrigin(
            "https://example.com", ["https://example.com", "https://other.com"]));
    }

    [Fact]
    public void ValidateOrigin_Invalid()
    {
        Assert.False(SecurityHelpers.ValidateOrigin(
            "https://evil.com", ["https://example.com"]));
    }

    [Fact]
    public void ValidateOrigin_Null()
    {
        Assert.False(SecurityHelpers.ValidateOrigin(null, ["https://example.com"]));
    }

    [Fact]
    public void ValidateOriginMatchesServer_Matches()
    {
        Assert.True(SecurityHelpers.ValidateOriginMatchesServer(
            "https://example.com", "https://example.com/mcp"));
    }

    [Fact]
    public void ValidateOriginMatchesServer_DifferentHost()
    {
        Assert.False(SecurityHelpers.ValidateOriginMatchesServer(
            "https://evil.com", "https://example.com/mcp"));
    }

    [Fact]
    public void ValidateOriginMatchesServer_DifferentPort()
    {
        Assert.False(SecurityHelpers.ValidateOriginMatchesServer(
            "https://example.com:8443", "https://example.com:443/mcp"));
    }

    [Fact]
    public void IsValidResourceParameter_Valid()
    {
        Assert.True(SecurityHelpers.IsValidResourceParameter("https://mcp.example.com"));
        Assert.True(SecurityHelpers.IsValidResourceParameter("https://mcp.example.com/mcp"));
    }

    [Fact]
    public void IsValidResourceParameter_NoScheme()
    {
        Assert.False(SecurityHelpers.IsValidResourceParameter("mcp.example.com"));
    }

    [Fact]
    public void IsValidResourceParameter_WithFragment()
    {
        Assert.False(SecurityHelpers.IsValidResourceParameter("https://mcp.example.com#section"));
    }
}

#endregion
