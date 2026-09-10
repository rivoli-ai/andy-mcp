using System.Net;

namespace Andy.MCP.Gateway;

public enum McpAdapterType { StreamableHttp = 1, Sse = 2 }
public sealed record McpAdapterDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Description { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool Enabled { get; init; } = true;
    public McpAdapterType Type { get; init; } = McpAdapterType.StreamableHttp;
    /// <summary>Only administrative export includes header values. Discovery responses redact them.</summary>
    public Dictionary<string, string> Headers { get; init; } = [];
    public bool IsHealthy { get; init; }
    public DateTimeOffset? LastHealthCheck { get; init; }
    public int? LastResponseTimeMs { get; init; }
    public string? LastError { get; init; }
    public long Revision { get; init; }
}
public sealed record CreateMcpAdapterDto
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Description { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public bool Enabled { get; init; } = true;
    public McpAdapterType Type { get; init; } = McpAdapterType.StreamableHttp;
    /// <summary>Null preserves existing credentials on update; an empty dictionary clears them.</summary>
    public Dictionary<string, string>? Headers { get; init; }
    public string? CreatedBy { get; init; }
}
public sealed record AdapterListDto
{
    public List<McpAdapterDto> Adapters { get; init; } = [];
    public int Total { get; init; }
    public int Healthy { get; init; }
    public int Unhealthy { get; init; }
    public int Disabled { get; init; }
}
public sealed record AdapterHealthDto(Guid Id, string Name, string Url, string Status,
    DateTimeOffset? LastCheck, int? ResponseTimeMs, string? LastError);

public partial interface IMcpGatewayClient
{
    Task<AdapterListDto> ListAdaptersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<McpAdapterDto>> ListEnabledAdaptersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<McpAdapterDto> GetAdapterAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<McpAdapterDto> GetAdapterByNameAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<McpAdapterDto>> SearchAdaptersAsync(string? name = null, bool? enabled = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<McpAdapterDto> CreateAdapterAsync(CreateMcpAdapterDto adapter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<McpAdapterDto> UpdateAdapterAsync(Guid id, CreateMcpAdapterDto adapter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteAdapterAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<AdapterHealthDto> GetAdapterHealthAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<AdapterHealthDto>> CheckAdaptersHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task ReloadAdaptersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<McpAdapterDto>> ExportAdaptersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task ImportAdaptersAsync(IReadOnlyList<CreateMcpAdapterDto> adapters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed partial class McpGatewayClient
{
    public Task<AdapterListDto> ListAdaptersAsync(CancellationToken cancellationToken = default) => AdapterAsync<AdapterListDto>(HttpMethod.Get, "", null, cancellationToken);
    public async Task<IReadOnlyList<McpAdapterDto>> ListEnabledAdaptersAsync(CancellationToken cancellationToken = default) => await AdapterAsync<List<McpAdapterDto>>(HttpMethod.Get, "enabled", null, cancellationToken).ConfigureAwait(false);
    public Task<McpAdapterDto> GetAdapterAsync(Guid id, CancellationToken cancellationToken = default) => AdapterAsync<McpAdapterDto>(HttpMethod.Get, id.ToString(), null, cancellationToken);
    public Task<McpAdapterDto> GetAdapterByNameAsync(string name, CancellationToken cancellationToken = default) => AdapterAsync<McpAdapterDto>(HttpMethod.Get, "name/" + Segment(name), null, cancellationToken);
    public async Task<IReadOnlyList<McpAdapterDto>> SearchAdaptersAsync(string? name = null, bool? enabled = null, CancellationToken cancellationToken = default) =>
        await AdapterAsync<List<McpAdapterDto>>(HttpMethod.Get, "search?name=" + Uri.EscapeDataString(name ?? "") + (enabled is null ? "" : "&enabled=" + enabled.Value.ToString().ToLowerInvariant()), null, cancellationToken).ConfigureAwait(false);
    public Task<McpAdapterDto> CreateAdapterAsync(CreateMcpAdapterDto adapter, CancellationToken cancellationToken = default) => AdapterAsync<McpAdapterDto>(HttpMethod.Post, "", adapter, cancellationToken);
    public Task<McpAdapterDto> UpdateAdapterAsync(Guid id, CreateMcpAdapterDto adapter, CancellationToken cancellationToken = default) => AdapterAsync<McpAdapterDto>(HttpMethod.Put, id.ToString(), adapter, cancellationToken);
    public async Task DeleteAdapterAsync(Guid id, CancellationToken cancellationToken = default) => _ = await AdapterAsync<object>(HttpMethod.Delete, id.ToString(), null, cancellationToken).ConfigureAwait(false);
    public Task<AdapterHealthDto> GetAdapterHealthAsync(Guid id, CancellationToken cancellationToken = default) => AdapterAsync<AdapterHealthDto>(HttpMethod.Get, id + "/health", null, cancellationToken);
    public async Task<IReadOnlyList<AdapterHealthDto>> CheckAdaptersHealthAsync(CancellationToken cancellationToken = default) => await AdapterAsync<List<AdapterHealthDto>>(HttpMethod.Post, "health-check", null, cancellationToken).ConfigureAwait(false);
    public async Task ReloadAdaptersAsync(CancellationToken cancellationToken = default) => _ = await AdapterAsync<object>(HttpMethod.Post, "reload", null, cancellationToken).ConfigureAwait(false);
    public async Task<IReadOnlyList<McpAdapterDto>> ExportAdaptersAsync(CancellationToken cancellationToken = default) => await AdapterAsync<List<McpAdapterDto>>(HttpMethod.Get, "export", null, cancellationToken).ConfigureAwait(false);
    public async Task ImportAdaptersAsync(IReadOnlyList<CreateMcpAdapterDto> adapters, CancellationToken cancellationToken = default) => _ = await AdapterAsync<object>(HttpMethod.Post, "import", adapters, cancellationToken).ConfigureAwait(false);
    private Task<T> AdapterAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) => SendAsync<T>(method, path, body, ct, adapters: true);
}
