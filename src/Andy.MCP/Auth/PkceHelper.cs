using System.Security.Cryptography;
using System.Text;

namespace Andy.MCP.Auth;

/// <summary>
/// PKCE (Proof Key for Code Exchange) helper for OAuth 2.1.
/// Generates code_verifier and code_challenge per RFC 7636.
/// </summary>
public static class PkceHelper
{
    /// <summary>
    /// Generate a cryptographically random code_verifier (43-128 characters, URL-safe).
    /// </summary>
    public static string GenerateCodeVerifier(int length = 64)
    {
        if (length < 43 || length > 128)
            throw new ArgumentOutOfRangeException(nameof(length), "Code verifier must be 43-128 characters.");

        var bytes = RandomNumberGenerator.GetBytes(length);
        return Base64UrlEncode(bytes)[..length];
    }

    /// <summary>
    /// Compute the S256 code_challenge from a code_verifier.
    /// code_challenge = BASE64URL(SHA256(code_verifier))
    /// </summary>
    public static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Generate a PKCE pair (verifier + challenge).
    /// </summary>
    public static (string CodeVerifier, string CodeChallenge) Generate(int verifierLength = 64)
    {
        var verifier = GenerateCodeVerifier(verifierLength);
        var challenge = ComputeCodeChallenge(verifier);
        return (verifier, challenge);
    }

    /// <summary>
    /// Generate a random state parameter for CSRF protection.
    /// </summary>
    public static string GenerateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
}
