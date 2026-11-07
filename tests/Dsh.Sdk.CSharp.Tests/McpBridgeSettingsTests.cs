using Dsh.Sdk.CSharp.McpServer;
using Microsoft.Extensions.Configuration;

namespace Dsh.Sdk.CSharp.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("Process environment")]
public sealed class McpBridgeSettingsTests
{
    [Fact]
    public void Load_discoversDshFromPath_whenExecutableIsNotConfigured()
    {
        var directory = Directory.CreateTempSubdirectory();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var executableName = OperatingSystem.IsWindows() ? "dsh.cmd" : "dsh";
            var expectedPath = Path.Combine(directory.FullName, executableName);
            File.WriteAllText(expectedPath, string.Empty);
            Environment.SetEnvironmentVariable("PATH", directory.FullName);

            var settings = LoadSettings(("DSH_EXECUTABLE", string.Empty));

            Assert.Equal(expectedPath, settings.DshExecutable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_prefersConfiguredExecutableOverPathDiscovery()
    {
        var directory = Directory.CreateTempSubdirectory();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var discoveredExecutable = Path.Combine(
                directory.FullName,
                OperatingSystem.IsWindows() ? "dsh.cmd" : "dsh");
            File.WriteAllText(discoveredExecutable, string.Empty);
            var configuredExecutable = Path.Combine(directory.FullName, "custom-dsh");
            Environment.SetEnvironmentVariable("PATH", directory.FullName);

            var settings = LoadSettings(("DSH_EXECUTABLE", configuredExecutable));

            Assert.Equal(configuredExecutable, settings.DshExecutable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_usesCurrentUserDshHomeWhenHomeIsNotConfigured()
    {
        var settings = LoadSettings();

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"),
            settings.DshHome);
    }

    [Fact]
    public void Load_usesConfiguredProfileAndWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "dsh-mcp-configured-workspace");
        var settings = LoadSettings(
            ("DSH_PROFILE", "dsh-sdk-csharp-chat"),
            ("DSH_WORKSPACE", workspace));

        Assert.Equal("dsh-sdk-csharp-chat", settings.DshProfile);
        Assert.Equal(Path.GetFullPath(workspace), settings.DshWorkspace);
    }

    [Fact]
    public void Load_DisablesEmailNotificationsByDefaultAndAllowsExplicitEnablement()
    {
        Assert.False(LoadSettings().EmailNotificationsEnabled);
        Assert.True(LoadSettings(("Email:Enabled", "true")).EmailNotificationsEnabled);
    }

    [Fact]
    public void Load_DisablesDshMessageContentLoggingByDefaultAndAllowsExplicitEnablement()
    {
        Assert.False(LoadSettings().LogDshMessages);
        Assert.True(LoadSettings(("DSH_MCP_LOG_DSH_MESSAGES", "true")).LogDshMessages);
    }

    [Fact]
    public void Load_OptionalDefaultCallerIsTrimmed()
    {
        Assert.Null(LoadSettings().DefaultCaller);
        Assert.Null(LoadSettings(
            ("Caller:DefaultUserId", " "),
            ("Caller:DefaultEmail", "")).DefaultCaller);
        var settings = LoadSettings(
            ("Caller:DefaultUserId", " local-user "),
            ("Caller:DefaultEmail", " local@example.com "));

        Assert.Equal(new McpCaller("local-user", "local@example.com"), settings.DefaultCaller);
    }

    [Theory]
    [InlineData("local-user", "")]
    [InlineData("", "local@example.com")]
    [InlineData("local-user", " ")]
    public void Load_RejectsPartialDefaultCaller(string userId, string email)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LoadSettings(
            ("Caller:DefaultUserId", userId),
            ("Caller:DefaultEmail", email)));
        Assert.Contains("must either both be set", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_RejectsInvalidDefaultCallerValues(bool invalidUserId)
    {
        Assert.Throws<InvalidOperationException>(() => LoadSettings(
            ("Caller:DefaultUserId", invalidUserId ? new string('x', 129) : "local-user"),
            ("Caller:DefaultEmail", invalidUserId ? "local@example.com" : "bad\nemail")));
    }

    private static McpBridgeSettings LoadSettings(
        params (string Key, string Value)[] additionalSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["MCP_API_KEY"] = new string('k', 32),
            ["DSH_EXECUTABLE"] = Path.Combine(Path.GetTempPath(), "mcp-settings-test-dsh"),
        };
        foreach (var (key, value) in additionalSettings)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        return McpBridgeSettings.Load(configuration);
    }
}
