namespace Dsh.Sdk.CSharp.McpServer;

public sealed class McpBridgeSettings
{
    public required string ApiKey { get; init; }
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string DshExecutable { get; init; } = "dsh";
    public string? DshHome { get; init; }
    public string DshProfile { get; init; } = "sdk";
    public string DshWorkspace { get; init; } = Environment.CurrentDirectory;
    public string DatabasePath { get; init; } = string.Empty;
    public int MaxPromptCharacters { get; init; } = 20_000;
    public int MaxResultCharacters { get; init; } = 4_000;
    public bool EmailNotificationsEnabled { get; init; }
    public bool LogDshMessages { get; init; }
    public McpCaller? DefaultCaller { get; init; }

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
        var maxResultCharacters = ParsePositiveInt(
            configuration["DSH_MCP_MAX_RESULT_CHARACTERS"],
            defaultValue: 4_000,
            "DSH_MCP_MAX_RESULT_CHARACTERS");
        var emailNotificationsEnabled = ParseBoolean(
            configuration["Email:Enabled"],
            defaultValue: false,
            "Email:Enabled");
        var logDshMessages = ParseBoolean(
            configuration["DSH_MCP_LOG_DSH_MESSAGES"],
            defaultValue: false,
            "DSH_MCP_LOG_DSH_MESSAGES");
        var defaultUserId = EmptyToNull(configuration["Caller:DefaultUserId"])?.Trim();
        var defaultEmail = EmptyToNull(configuration["Caller:DefaultEmail"])?.Trim();
        if ((defaultUserId is null) != (defaultEmail is null))
        {
            throw new InvalidOperationException(
                "Caller:DefaultUserId and Caller:DefaultEmail must either both be set or both be omitted.");
        }

        if (defaultUserId is not null && defaultEmail is not null
            && (!McpCallerContext.IsValidValue(defaultUserId, 128)
                || !McpCallerContext.IsValidValue(defaultEmail, 254)))
        {
            throw new InvalidOperationException(
                "Caller:DefaultUserId or Caller:DefaultEmail is invalid.");
        }

        return new McpBridgeSettings
        {
            ApiKey = apiKey,
            AllowedOrigins = SplitValues(configuration["MCP_ALLOWED_ORIGINS"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            DshExecutable = ResolveDshExecutable(configuration["DSH_EXECUTABLE"]),
            DshHome = Path.GetFullPath(
                EmptyToNull(configuration["DSH_HOME"])
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dsh")),
            DshProfile = configuration["DSH_PROFILE"] ?? "dsh-sdk-csharp-chat",
            DshWorkspace = Path.GetFullPath(
                configuration["DSH_WORKSPACE"] ?? Environment.CurrentDirectory),
            DatabasePath = Path.GetFullPath(
                configuration["DSH_MCP_DATABASE_PATH"] ?? GetDefaultDatabasePath()),
            MaxPromptCharacters = maxPromptCharacters,
            MaxResultCharacters = maxResultCharacters,
            EmailNotificationsEnabled = emailNotificationsEnabled,
            LogDshMessages = logDshMessages,
            DefaultCaller = defaultUserId is not null && defaultEmail is not null
                ? new McpCaller(defaultUserId, defaultEmail)
                : null,
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

    private static bool ParseBoolean(string? value, bool defaultValue, string key)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"{key} must be true or false.");
    }

    private static string GetDefaultDatabasePath()
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(dataDirectory, "Dsh.Sdk.CSharp.McpServer", "mcp-state.db");
    }

    private static string ResolveDshExecutable(string? configuredExecutable)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            var explicitExecutable = configuredExecutable.Trim();
            if (Path.IsPathRooted(explicitExecutable)
                || explicitExecutable.Contains(Path.DirectorySeparatorChar)
                || explicitExecutable.Contains(Path.AltDirectorySeparatorChar))
            {
                return Path.GetFullPath(explicitExecutable);
            }

            var configuredFromPath = FindExecutableOnPath(explicitExecutable);
            return configuredFromPath ?? throw new FileNotFoundException(
                $"Could not find '{explicitExecutable}' on PATH. Set DSH_EXECUTABLE to the full path of dsh or dsh.cmd.");
        }

        var fromPath = FindExecutableOnPath("dsh");
        if (fromPath is not null)
        {
            return fromPath;
        }

        if (OperatingSystem.IsWindows())
        {
            var desktopCli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "DeepSeek Harness",
                "resources",
                "runtime",
                "cli",
                "bin",
                "dsh.cmd");
            if (File.Exists(desktopCli))
            {
                return desktopCli;
            }
        }

        throw new FileNotFoundException(
            "Could not find DSH on PATH or in the standard Windows Desktop installation. Set DSH_EXECUTABLE to the full path of dsh or dsh.cmd.");
    }

    private static string? FindExecutableOnPath(string executable)
    {
        if (Path.IsPathRooted(executable)
            || executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(executable);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string[] executableNames = OperatingSystem.IsWindows() && !Path.HasExtension(executable)
            ? [$"{executable}.cmd", $"{executable}.bat", $"{executable}.exe"]
            : [executable];

        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (searchPath is not null)
        {
            foreach (var directory in searchPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalizedDirectory = directory.Trim('"');
                foreach (var executableName in executableNames)
                {
                    var candidate = Path.Combine(normalizedDirectory, executableName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }

        return null;
    }
}
