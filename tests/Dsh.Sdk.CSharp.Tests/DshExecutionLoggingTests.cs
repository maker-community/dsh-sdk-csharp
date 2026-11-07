using Dsh.Sdk.CSharp.McpServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dsh.Sdk.CSharp.Tests;

public sealed class DshExecutionLoggingTests
{
    [Theory]
    [InlineData(false, "hello", false)]
    [InlineData(true, "hello", true)]
    [InlineData(true, "password=private-value", false)]
    [InlineData(true, "hello private-value", false)]
    [InlineData(true, "the-long-local-api-key-value-123456", false)]
    public async Task RunAsync_LogsPromptOnlyWhenEnabledAndNotSensitive(
        bool logMessages,
        string prompt,
        bool shouldLogPrompt)
    {
        var logger = new RecordingLogger<DshExecutionService>();
        await using var service = CreateService(logMessages, logger);

        await service.RunAsync(prompt, "test-session", CancellationToken.None);

        Assert.Equal(shouldLogPrompt, logger.Messages.Any(
            message => message.Contains($"DSH prompt for session test-session: {prompt}", StringComparison.Ordinal)));
        if (logMessages && !shouldLogPrompt)
        {
            Assert.Contains(logger.Messages, message =>
                message.Contains("was omitted because it may contain sensitive data", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, message =>
                message.Contains(prompt, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RunAsync_LogsNonTextBlockTypeWithoutToolPayload()
    {
        var logger = new RecordingLogger<DshExecutionService>();
        await using var service = CreateService(false, logger);

        var result = await service.RunAsync("test no text", "test-session", CancellationToken.None);

        Assert.Equal("completed", result.FinishReason);
        Assert.Contains(logger.Messages, message =>
            message.Contains("content block types: tool_use", StringComparison.Ordinal));
    }

    private static DshExecutionService CreateService(
        bool logMessages,
        ILogger<DshExecutionService> logger)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test build configuration could not be determined.");
        var executable = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "FakeDshRuntime", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "FakeDshRuntime.exe" : "FakeDshRuntime"));
        return new DshExecutionService(
            new McpBridgeSettings
            {
                ApiKey = "the-long-local-api-key-value-123456",
                DshExecutable = executable,
                DshWorkspace = Environment.CurrentDirectory,
                LogDshMessages = logMessages,
            },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:SmtpPassword"] = "private-value",
            }).Build(),
            logger);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
