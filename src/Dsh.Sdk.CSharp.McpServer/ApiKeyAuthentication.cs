using System.Security.Cryptography;
using System.Text;

namespace Dsh.Sdk.CSharp.McpServer;

internal static class ApiKeyAuthentication
{
    public static bool IsAuthorized(string? authorizationHeader, string expectedApiKey)
    {
        const string prefix = "Bearer ";
        if (authorizationHeader is null
            || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedKey = authorizationHeader[prefix.Length..].Trim();
        if (suppliedKey.Length == 0)
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedApiKey));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
