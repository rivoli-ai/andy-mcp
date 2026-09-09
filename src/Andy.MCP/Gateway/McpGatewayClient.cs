using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Transport;

namespace Andy.MCP.Gateway;

/// <summary>Status values used by the Andy MCP Gateway registry API.</summary>
public enum GatewayStatus { Active, Inactive, Maintenance }

/// <summary>A registered endpoint returned by /api/GatewayRegistry.</summary>
public sealed record GatewayRegistration
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Version { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public GatewayStatus Status { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record GatewaySearch
{
    public string? SearchTerm { get; init; }
    public List<string>? Tags { get; init; }
    public GatewayStatus? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// <summary>Registry connection and bounded discovery settings.</summary>
public sealed class McpGatewayOptions
{
    public required Uri RegistryUri { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumCacheAge { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Optional registry-only bearer token provider; true requests refresh after one 401.</summary>
    public Func<bool, CancellationToken, Task<string?>>? TokenProvider { get; set; }
}

public sealed class McpGatewayException(HttpStatusCode status, string message) : HttpRequestException(message, null, status);

/// <summary>Typed access to the current Andy gateway registry contract.</summary>
public interface IMcpGatewayClient
{
    Task<IReadOnlyList<GatewayRegistration>> ListAsync(CancellationToken cancellationToken = default);
    Task<GatewayRegistration> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GatewayRegistration>> SearchAsync(GatewaySearch search, CancellationToken cancellationToken = default);
    Task<GatewayRegistration> CreateAsync(GatewayRegistration registration, CancellationToken cancellationToken = default);
    Task<GatewayRegistration> UpdateAsync(string id, GatewayRegistration registration, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>HTTP registry client. The supplied HttpClient remains owned by its caller.</summary>
public sealed class McpGatewayClient : IMcpGatewayClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly McpGatewayOptions _options;
    private readonly Uri _endpoint;

    public McpGatewayClient(HttpClient http, McpGatewayOptions options, bool ownsHttpClient = false)
    {
        _ownsHttp = ownsHttpClient;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateEndpoint(options.RegistryUri);
        if (options.RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        _endpoint = new Uri(options.RegistryUri.AbsoluteUri.TrimEnd('/') + "/api/GatewayRegistry/");
    }

    public async Task<IReadOnlyList<GatewayRegistration>> ListAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<List<GatewayRegistration>>(HttpMethod.Get, "", null, cancellationToken).ConfigureAwait(false);
    public Task<GatewayRegistration> GetAsync(string id, CancellationToken cancellationToken = default) =>
        SendAsync<GatewayRegistration>(HttpMethod.Get, Segment(id), null, cancellationToken);
    public async Task<IReadOnlyList<GatewayRegistration>> SearchAsync(GatewaySearch search, CancellationToken cancellationToken = default) =>
        await SendAsync<List<GatewayRegistration>>(HttpMethod.Post, "search", search, cancellationToken).ConfigureAwait(false);
    public Task<GatewayRegistration> CreateAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        SendAsync<GatewayRegistration>(HttpMethod.Post, "", registration, cancellationToken);
    public Task<GatewayRegistration> UpdateAsync(string id, GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        SendAsync<GatewayRegistration>(HttpMethod.Put, Segment(id), registration, cancellationToken);
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        _ = await SendAsync<object>(HttpMethod.Delete, Segment(id), null, cancellationToken).ConfigureAwait(false);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            if (_options.TokenProvider is { } tokenProvider)
            {
                var token = await tokenProvider(attempt > 0, timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && _options.TokenProvider is not null) continue;
            if (!response.IsSuccessStatusCode)
                throw new McpGatewayException(response.StatusCode, $"Gateway registry returned HTTP {(int)response.StatusCode} for {method} {path}.");
            if (response.StatusCode == HttpStatusCode.NoContent) return default!;
            return await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token).ConfigureAwait(false)
                ?? throw new JsonException("Gateway registry returned an empty response.");
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private static string Segment(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or "..") throw new ArgumentException("Invalid registry id.", nameof(id));
        return Uri.EscapeDataString(id);
    }

    internal static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.Query))
            throw new ArgumentException("An absolute HTTP(S) endpoint without credentials, query or fragment is required.", nameof(endpoint));
    }

    /// <summary>Connects directly to a registered MCP endpoint; registry credentials are never forwarded.</summary>
    public static async Task<bool> CheckHealthAsync(GatewayRegistration registration, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            var endpoint = new Uri(registration.Endpoint);
            ValidateEndpoint(endpoint);
            await using var transport = new StreamableHttpClientTransport(new() { Endpoint = endpoint, EnableServerSseStream = false });
            await using var client = await McpClient.ConnectAsync(transport, cancellationToken: linked.Token).ConfigureAwait(false);
            await client.PingAsync(linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException or McpException)
        { return false; }
    }
}
