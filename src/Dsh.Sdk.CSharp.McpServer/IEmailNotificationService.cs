using DeepSeek.Harness.Sdk;

namespace Dsh.Sdk.CSharp.McpServer;

public interface IEmailNotificationService
{
    bool IsEnabled { get; }

    void ValidateRecipient(string recipient);

    Task<EmailNotificationResult> SendResultAsync(
        string recipient,
        HarnessRunResult result,
        CancellationToken cancellationToken);

    Task<EmailNotificationResult> SendMessageAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken);
}

public sealed record EmailNotificationResult(string Status, string Recipient, string? Error);
