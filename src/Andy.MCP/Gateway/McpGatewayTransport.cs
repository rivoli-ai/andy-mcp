using System.Net;
using System.Net.Http.Headers;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Gateway;

/// <summary>Authenticated gateway proxy transport. Gateway tokens never reach an adapter's upstream origin.</summary>
public sealed class McpGatewayTransport : IClientTransport
{
    private readonly IClientTransport _inner;
    private readonly HttpClient _authenticated;
    public bool IsConnected => _inner.IsConnected;
    public IAsyncEnumerable<JsonRpcMessage> Messages => _inner.Messages;
    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }
    internal event Action<InitializeResult>? SessionReinitialized
    {
        add { if (_inner is StreamableHttpClientTransport http) http.SessionReinitialized += value; }
        remove { if (_inner is StreamableHttpClientTransport http) http.SessionReinitialized -= value; }
    }

    public McpGatewayTransport(McpGatewayOptions options, string adapterName,
        McpAdapterType type = McpAdapterType.StreamableHttp, HttpClient? httpClient = null)
    {
        var endpoint = GetProxyEndpoint(options.RegistryUri, adapterName, type);
        var http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _authenticated = new HttpClient(new GatewayBearerHandler(http, options, httpClient is null)) { Timeout = Timeout.InfiniteTimeSpan };
        _inner = type == McpAdapterType.Sse
            ? new LegacySseClientTransport(endpoint, _authenticated, options.RequestTimeout)
            : new StreamableHttpClientTransport(new() { Endpoint = endpoint, HttpClient = _authenticated });
    }

    public static Uri GetProxyEndpoint(Uri gateway, string adapterName, McpAdapterType type = McpAdapterType.StreamableHttp)
    {
        McpGatewayClient.ValidateEndpoint(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        if (adapterName is "." or ".." || adapterName.Contains('/') || adapterName.Contains('\\')) throw new ArgumentException("Invalid adapter name.", nameof(adapterName));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        return new Uri(gateway.AbsoluteUri.TrimEnd('/') + "/adapters/" + Uri.EscapeDataString(adapterName) + (type == McpAdapterType.Sse ? "/sse" : "/mcp"));
    }
    public Task ConnectAsync(CancellationToken cancellationToken = default) => _inner.ConnectAsync(cancellationToken);
    public Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => _inner.SendAsync(message, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        try { await _inner.DisposeAsync().ConfigureAwait(false); }
        finally { _authenticated.Dispose(); }
    }
}

internal sealed class GatewayBearerHandler(HttpClient http, McpGatewayOptions options, bool ownsHttp) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (bytes is not null)
            {
                copy.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content!.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (options.TokenProvider is { } provider)
            {
                var token = await provider(attempt > 0, timeout.Token).ConfigureAwait(false);
                copy.Headers.Authorization = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
            }
            if (options.RequireAuthentication && copy.Headers.Authorization is null && http.DefaultRequestHeaders.Authorization is null)
                throw new McpGatewayException(HttpStatusCode.Unauthorized, "No gateway bearer token configured.");
            var response = await http.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && options.TokenProvider is not null)
            {
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MethodNotAllowed && response.StatusCode != HttpStatusCode.NotFound)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new McpGatewayException(status, $"Gateway proxy returned HTTP {(int)status} for adapter path {request.RequestUri!.AbsolutePath}.");
            }
            return response;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsHttp) http.Dispose();
        base.Dispose(disposing);
    }
}
