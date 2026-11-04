using DeepSeek.Harness.Sdk;

namespace Dsh.Sdk.CSharp.McpServer;

public interface IEmailNotificationService
{
    void ValidateRecipient(string recipient);

    Task<EmailNotificationResult> SendResultAsync(
        string recipient,
        HarnessRunResult result,
        CancellationToken cancellationToken);
}

public sealed record EmailNotificationResult(string Status, string Recipient, string? Error);
