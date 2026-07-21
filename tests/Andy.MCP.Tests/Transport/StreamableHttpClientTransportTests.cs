using System.Net;
using System.Text;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Transport;

public class StreamableHttpClientTransportTests
{
    [Fact]
    public async Task SendAsync_PostsJsonAndReceivesJsonResponse()
    {
        var responseMsg = JsonRpcResponse.Success((RequestId)1);
        var responseJson = McpJsonDefaults.Serialize(responseMsg);

        var handler = new MockHttpHandler((req, ct) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("application/json", req.Headers.Accept.ToString());
            Assert.Contains("text/event-stream", req.Headers.Accept.ToString());
            Assert.Contains("MCP-Protocol-Version", req.Headers.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler);
        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = httpClient,
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();

        var request = new JsonRpcRequest { Id = 1, Method = "ping" };
        await transport.SendAsync(request);

        // Read response from Messages
        await foreach (var msg in transport.Messages)
        {
            var resp = Assert.IsType<JsonRpcResponse>(msg);
            Assert.Equal(1L, resp.Id.AsNumber());
            Assert.True(resp.IsSuccess);
            break;
        }
    }

    [Fact]
    public async Task SendAsync_HandlesSSEResponse()
    {
        var responseMsg = JsonRpcResponse.Success((RequestId)1,
            McpJsonDefaults.ToElement(new { tools = new[] { "tool1" } }));
        var sseContent = $"data: {McpJsonDefaults.Serialize(responseMsg)}\n\n";

        var handler = new MockHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseContent, Encoding.UTF8, "text/event-stream")
            });
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();

        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "tools/list" });

        await foreach (var msg in transport.Messages)
        {
            var resp = Assert.IsType<JsonRpcResponse>(msg);
            Assert.Equal(1L, resp.Id.AsNumber());
            break;
        }
    }

    [Fact]
    public async Task SendAsync_Notification_Gets202()
    {
        var handler = new MockHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();

        var notification = new JsonRpcNotification { Method = "notifications/initialized" };
        await transport.SendAsync(notification); // Should not throw
    }

    [Fact]
    public async Task SessionId_CapturedFromResponse()
    {
        int requestCount = 0;
        string? capturedSessionId = null;

        var handler = new MockHttpHandler((req, ct) =>
        {
            requestCount++;

            // Second request should have the session ID
            if (requestCount > 1)
            {
                capturedSessionId = req.Headers.TryGetValues("Mcp-Session-Id", out var vals)
                    ? vals.First() : null;
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}",
                    Encoding.UTF8, "application/json")
            };
            resp.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-abc");
            return Task.FromResult(resp);
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();

        // First request — captures session ID
        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "initialize" });
        await foreach (var _ in transport.Messages) { break; }

        // Second request — should include session ID
        await transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "ping" });
        await foreach (var _ in transport.Messages) { break; }

        Assert.Equal("session-abc", capturedSessionId);
    }

    [Fact]
    public async Task ProtocolVersionHeader_Included()
    {
        string? versionHeader = null;

        var handler = new MockHttpHandler((req, ct) =>
        {
            versionHeader = req.Headers.TryGetValues("MCP-Protocol-Version", out var vals)
                ? vals.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();
        await transport.SendAsync(new JsonRpcNotification { Method = "test" });

        Assert.Equal(McpSession.LatestProtocolVersion, versionHeader);
    }

    [Fact]
    public async Task SessionExpired_404_Throws()
    {
        var handler = new MockHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();

        await Assert.ThrowsAsync<McpSessionExpiredException>(() =>
            transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "test" }));
    }

    [Fact]
    public async Task AdditionalHeaders_Included()
    {
        string? authHeader = null;

        var handler = new MockHttpHandler((req, ct) =>
        {
            authHeader = req.Headers.TryGetValues("Authorization", out var vals)
                ? vals.First() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer my-token"
            }
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();
        await transport.SendAsync(new JsonRpcNotification { Method = "test" });

        Assert.Equal("Bearer my-token", authHeader);
    }

    [Fact]
    public async Task Dispose_SendsDeleteWithSessionId()
    {
        HttpMethod? lastMethod = null;
        string? deleteSessionId = null;

        var handler = new MockHttpHandler((req, ct) =>
        {
            lastMethod = req.Method;
            if (req.Method == HttpMethod.Delete)
            {
                deleteSessionId = req.Headers.TryGetValues("Mcp-Session-Id", out var vals)
                    ? vals.First() : null;
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}",
                    Encoding.UTF8, "application/json")
            };
            resp.Headers.TryAddWithoutValidation("Mcp-Session-Id", "sess-123");
            return Task.FromResult(resp);
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();
        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "init" });
        await foreach (var _ in transport.Messages) { break; }

        await transport.DisposeAsync();

        Assert.Equal(HttpMethod.Delete, lastMethod);
        Assert.Equal("sess-123", deleteSessionId);
    }

    [Fact]
    public async Task SubsequentRequests_SendNegotiatedProtocolVersion()
    {
        var versions = new List<string?>();
        var call = 0;
        var handler = new MockHttpHandler((req, ct) =>
        {
            versions.Add(req.Headers.TryGetValues("MCP-Protocol-Version", out var v) ? v.FirstOrDefault() : null);
            call++;

            // The first response is the initialize result carrying the negotiated version.
            var payload = call == 1
                ? JsonRpcResponse.Success((RequestId)1, McpJsonDefaults.ToElement(new { protocolVersion = "2025-06-18" }))
                : JsonRpcResponse.Success((RequestId)2);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(McpJsonDefaults.Serialize(payload), Encoding.UTF8, "application/json")
            });
        });

        var options = new StreamableHttpClientTransportOptions
        {
            Endpoint = new Uri("https://example.com/mcp"),
            HttpClient = new HttpClient(handler),
            EnableServerSseStream = false
        };

        await using var transport = new StreamableHttpClientTransport(options);
        await transport.ConnectAsync();

        await transport.SendAsync(new JsonRpcRequest { Id = 1, Method = "initialize" });
        await transport.SendAsync(new JsonRpcRequest { Id = 2, Method = "ping" });

        Assert.Equal(McpSession.LatestProtocolVersion, versions[0]); // before negotiation
        Assert.Equal("2025-06-18", versions[1]);                     // negotiated version
    }

    /// <summary>
    /// Mock HTTP message handler for testing.
    /// </summary>
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
