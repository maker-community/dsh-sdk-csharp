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

            var settings = LoadSettings();

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

    private static McpBridgeSettings LoadSettings(
        params (string Key, string Value)[] additionalSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["MCP_API_KEY"] = new string('k', 32),
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
