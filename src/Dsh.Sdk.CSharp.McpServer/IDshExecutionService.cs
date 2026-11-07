using DeepSeek.Harness.Sdk;

namespace Dsh.Sdk.CSharp.McpServer;

public interface IDshExecutionService
{
    Task StartAsync(CancellationToken cancellationToken);

    Task<HarnessRunResult> RunAsync(
        string prompt,
        string sessionId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<HarnessNotification> ReadNotificationsAsync(CancellationToken cancellationToken);

    bool IsSessionRunActive(string sessionId);
}
