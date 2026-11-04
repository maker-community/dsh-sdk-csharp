namespace Dsh.Sdk.CSharp.McpServer;

public sealed class McpBridgeSettings
{
    public required string ApiKey { get; init; }
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string DshExecutable { get; init; } = "dsh";
    public string? DshHome { get; init; }
    public string DshProfile { get; init; } = "sdk";
    public string DshWorkspace { get; init; } = Environment.CurrentDirectory;
    public int MaxPromptCharacters { get; init; } = 20_000;

    public static McpBridgeSettings Load(IConfiguration configuration)
    {
        var apiKey = configuration["MCP_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Set MCP_API_KEY to a random secret of at least 32 characters.");
        }

        var maxPromptCharacters = ParsePositiveInt(
            configuration["DSH_MCP_MAX_PROMPT_CHARACTERS"],
            defaultValue: 20_000,
            "DSH_MCP_MAX_PROMPT_CHARACTERS");

        return new McpBridgeSettings
        {
            ApiKey = apiKey,
            AllowedOrigins = SplitValues(configuration["MCP_ALLOWED_ORIGINS"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            DshExecutable = configuration["DSH_EXECUTABLE"] ?? "dsh",
            DshHome = EmptyToNull(configuration["DSH_HOME"]),
            DshProfile = configuration["DSH_PROFILE"] ?? "sdk",
            DshWorkspace = Path.GetFullPath(
                configuration["DSH_WORKSPACE"] ?? Environment.CurrentDirectory),
            MaxPromptCharacters = maxPromptCharacters,
        };
    }

    private static int ParsePositiveInt(string? value, int defaultValue, string key)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        throw new InvalidOperationException($"{key} must be a positive integer.");
    }

    private static IEnumerable<string> SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
