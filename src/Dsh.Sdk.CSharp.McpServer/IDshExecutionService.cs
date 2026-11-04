using DeepSeek.Harness.Sdk;

namespace Dsh.Sdk.CSharp.McpServer;

public interface IDshExecutionService
{
    Task<HarnessRunResult> RunAsync(
        string prompt,
        string? conversationId,
        CancellationToken cancellationToken);
}
