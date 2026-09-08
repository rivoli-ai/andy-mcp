using System.Net;
using System.Net.Sockets;

namespace Andy.MCP.Auth;

/// <summary>Default OAuth HTTP transport. Resolves once and connects only to vetted addresses.</summary>
internal static class OAuthHttpTransport
{
    internal static HttpClient CreateClient() => new(new EndpointHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint,
            (host, token) => Dns.GetHostAddressesAsync(host, token),
            ConnectSocketAsync, ct)
    }));

    private sealed class EndpointHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var result = SecurityHelpers.ValidateUrl(request.RequestUri?.AbsoluteUri ?? "");
            if (!result.IsValid) throw new HttpRequestException(result.Error);
            return base.SendAsync(request, ct);
        }
    }

    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect,
        CancellationToken ct)
    {
        var addresses = await resolve(endpoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(SecurityHelpers.IsPrivateOrReservedIp))
            throw new HttpRequestException("OAuth endpoint resolves to a private, reserved, or empty address set.");
        // Never pass the hostname to the socket: a second DNS lookup could rebind it.
        Exception? last = null;
        foreach (var address in addresses)
        {
            try { return await connect(new IPEndPoint(address, endpoint.Port), ct); }
            catch (SocketException ex) { last = ex; }
        }
        throw new HttpRequestException("Could not connect to the validated OAuth endpoint.", last);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
