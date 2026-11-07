using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.McpServer;

[McpServerToolType]
public static class DshTools
{
    [McpServerTool(Name = "dsh_run")]
    [Description("Run a short coding or analysis task synchronously using the current session. For long-running work on XiaoZhi, use dsh_submit_task and receive the result by email.")]
    public static async Task<string> RunAsync(
        [Description("The task to give the DeepSeek Harness agent.")] string prompt,
        IDshExecutionService execution,
        IMcpStateStore state,
        McpCallerContext callerContext,
        McpBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A non-empty prompt is required.", nameof(prompt));
        }

        if (prompt.Length > settings.MaxPromptCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(prompt),
                $"Prompt exceeds the configured {settings.MaxPromptCharacters}-character limit.");
        }

        var caller = callerContext.GetRequired();
        var session = await state.GetOrCreateActiveSessionAsync(
            caller.UserId, caller.Email, cancellationToken).ConfigureAwait(false);
        var result = await execution.RunAsync(prompt, session.DshSessionId, cancellationToken)
            .ConfigureAwait(false);
        var finalResponse = result.FinalResponse.Length <= settings.MaxResultCharacters
            ? result.FinalResponse
            : result.FinalResponse[..settings.MaxResultCharacters];

        return JsonSerializer.Serialize(new
        {
            result.SessionId,
            result.FinishReason,
            FinalResponse = finalResponse,
            Truncated = finalResponse.Length < result.FinalResponse.Length,
        });
    }
}
