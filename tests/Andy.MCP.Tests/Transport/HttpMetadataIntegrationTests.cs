using System.Net;
using System.Net.Http.Json;
using Andy.MCP.AspNetCore;
using Andy.MCP.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Andy.MCP.Tests.Transport;

public class HttpMetadataIntegrationTests
{
    [Fact]
    public async Task MappedEndpoint_ServesPublicMetadata_AndChallengesUnauthenticatedRequests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var policy = new McpHttpAuthorizationOptions
        {
            Resource = "https://api.example/mcp", Issuer = "https://auth.example", RequiredScopes = ["tools:read"]
        };
        app.MapMcp("/mcp", _ => { }, new StreamableHttpServerOptions { Authorization = policy });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var metadata = await client.GetFromJsonAsync<ProtectedResourceMetadata>(policy.MetadataPath);
        Assert.Equal(policy.Resource, metadata!.Resource);
        Assert.Equal(new[] { policy.Issuer }, metadata.AuthorizationServers);
        using var response = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(policy.MetadataUrl, response.Headers.WwwAuthenticate.ToString());
        await app.StopAsync();
    }
}
