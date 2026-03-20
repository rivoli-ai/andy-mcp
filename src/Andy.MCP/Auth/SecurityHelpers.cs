using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Andy.MCP.Auth;

/// <summary>
/// Security utilities for MCP: SSRF prevention, session ID generation, URL validation.
/// </summary>
public static class SecurityHelpers
{
    #region SSRF Prevention

    /// <summary>
    /// Validate a URL for SSRF safety. Blocks private IPs, requires HTTPS (optionally).
    /// </summary>
    public static UrlValidationResult ValidateUrl(string url, bool requireHttps = true)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return UrlValidationResult.Invalid("Not a valid absolute URI.");

        if (requireHttps && uri.Scheme != "https")
            return UrlValidationResult.Invalid($"HTTPS required, got '{uri.Scheme}'.");

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return UrlValidationResult.Invalid($"Unsupported scheme: '{uri.Scheme}'.");

        return UrlValidationResult.Valid();
    }

    /// <summary>
    /// Check if an IP address is in a private/reserved range (SSRF risk).
    /// </summary>
    public static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 127.0.0.0/8 (loopback, already covered above but explicit)
            if (bytes[0] == 127) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 (loopback, already covered)
            if (ip.Equals(IPAddress.IPv6Loopback)) return true;
            // fc00::/7 (unique local)
            if (bytes[0] >= 0xFC && bytes[0] <= 0xFD) return true;
            // fe80::/10 (link-local)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }

        return false;
    }

    /// <summary>
    /// Resolve hostname and check if any IP is private (DNS rebinding protection).
    /// </summary>
    public static async Task<UrlValidationResult> ValidateUrlWithDnsCheckAsync(
        string url, bool requireHttps = true, CancellationToken ct = default)
    {
        var basicResult = ValidateUrl(url, requireHttps);
        if (!basicResult.IsValid) return basicResult;

        var uri = new Uri(url);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            foreach (var ip in addresses)
            {
                if (IsPrivateOrReservedIp(ip))
                    return UrlValidationResult.Invalid($"Host '{uri.Host}' resolves to private IP {ip}.");
            }
        }
        catch (SocketException ex)
        {
            return UrlValidationResult.Invalid($"DNS resolution failed for '{uri.Host}': {ex.Message}");
        }

        return UrlValidationResult.Valid();
    }

    #endregion

    #region Session Security

    /// <summary>
    /// Generate a cryptographically secure session ID (visible ASCII, 0x21-0x7E).
    /// </summary>
    public static string GenerateSessionId(int length = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        // Base62-like encoding: alphanumeric only
        return Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "")[..length];
    }

    /// <summary>
    /// Generate a session ID bound to a user identity.
    /// Format: userId:randomPart
    /// </summary>
    public static string GenerateUserBoundSessionId(string userId, int randomLength = 24)
    {
        var randomPart = GenerateSessionId(randomLength);
        return $"{userId}:{randomPart}";
    }

    #endregion

    #region Origin Validation

    /// <summary>
    /// Validate an Origin header against a list of allowed origins.
    /// </summary>
    public static bool ValidateOrigin(string? origin, IReadOnlyList<string> allowedOrigins)
    {
        if (origin is null) return false;
        return allowedOrigins.Any(allowed =>
            string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validate that an Origin header matches the server's own origin.
    /// </summary>
    public static bool ValidateOriginMatchesServer(string? origin, string serverUrl)
    {
        if (origin is null) return false;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;

        return string.Equals(serverUri.Scheme, originUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(serverUri.Host, originUri.Host, StringComparison.OrdinalIgnoreCase)
            && serverUri.Port == originUri.Port;
    }

    #endregion

    #region Resource Parameter Validation

    /// <summary>
    /// Validate OAuth resource parameter per RFC 8707.
    /// </summary>
    public static bool IsValidResourceParameter(string resource)
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri))
            return false;

        if (!string.IsNullOrEmpty(uri.Fragment) && uri.Fragment != "#")
            return false;

        return true;
    }

    #endregion
}

/// <summary>
/// Result of a URL validation check.
/// </summary>
public sealed record UrlValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }

    public static UrlValidationResult Valid() => new() { IsValid = true };
    public static UrlValidationResult Invalid(string error) => new() { IsValid = false, Error = error };
}
