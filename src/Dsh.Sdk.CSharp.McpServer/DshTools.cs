using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.McpServer;

[McpServerToolType]
public static class DshTools
{
    [McpServerTool(Name = "dsh_run")]
    [Description("Run a coding or analysis task using the configured DeepSeek Harness workspace. Returns the final response and Session ID. Optionally emails the result to an allowlisted address.")]
    public static async Task<string> RunAsync(
        [Description("The task to give the DeepSeek Harness agent.")] string prompt,
        IDshExecutionService execution,
        IEmailNotificationService email,
        [Description("Optional conversation key to keep a separate DSH Session for this conversation.")] string? conversationId = null,
        [Description("Optional verified/allowlisted email address to receive the completed result.")] string? notifyEmail = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(notifyEmail))
        {
            email.ValidateRecipient(notifyEmail);
        }

        var result = await execution.RunAsync(prompt, conversationId, cancellationToken).ConfigureAwait(false);
        EmailNotificationResult? emailResult = null;
        if (!string.IsNullOrWhiteSpace(notifyEmail))
        {
            emailResult = await email.SendResultAsync(notifyEmail, result, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            result.SessionId,
            result.FinishReason,
            result.FinalResponse,
            Email = emailResult,
        });
    }
}
