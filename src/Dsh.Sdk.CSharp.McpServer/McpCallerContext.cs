namespace Dsh.Sdk.CSharp.McpServer;

public sealed record McpCaller(string UserId, string Email);

public sealed class McpCallerContext(IHttpContextAccessor httpContextAccessor)
{
    public McpCaller GetRequired(bool requireEmail = false)
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("The MCP caller context is unavailable.");
        var userId = ReadRequiredHeader(context, "X-User-Id", 128);
        var email = ReadOptionalHeader(context, "X-User-Email", 254);
        if (requireEmail && email is null)
        {
            throw new InvalidOperationException(
                "The authenticated MCP gateway must provide an X-User-Email header for email notifications.");
        }

        return new McpCaller(userId, email ?? string.Empty);
    }

    private static string ReadRequiredHeader(HttpContext context, string name, int maximumLength)
    {
        var value = context.Request.Headers[name].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"The authenticated MCP gateway must provide a valid {name} header.");
        }

        return value;
    }

    private static string? ReadOptionalHeader(HttpContext context, string name, int maximumLength)
    {
        var value = context.Request.Headers[name].ToString().Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"The {name} header is invalid.");
        }

        return value;
    }
}
