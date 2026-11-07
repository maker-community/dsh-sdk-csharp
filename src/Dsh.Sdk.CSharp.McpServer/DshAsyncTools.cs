using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.McpServer;

[McpServerToolType]
public static class DshAsyncTools
{
    [McpServerTool(Name = "dsh_submit_task")]
    [Description("Submit a long-running DSH task. Returns immediately with a job ID. The result is persisted and emailed only when email notifications are enabled.")]
    public static async Task<string> SubmitAsync(
        [Description("The task to give the DSH agent.")] string prompt,
        IMcpStateStore state,
        McpCallerContext callerContext,
        IEmailNotificationService email,
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

        var caller = callerContext.GetRequired(requireEmail: email.IsEnabled);
        if (email.IsEnabled)
        {
            email.ValidateRecipient(caller.Email);
        }
        var job = await state.SubmitJobAsync(
            caller.UserId, caller.Email, prompt, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            status = job.Status,
            sessionId = job.ConversationId,
            emailNotifications = email.IsEnabled ? "enabled" : "disabled",
            message = email.IsEnabled
                ? "Task accepted. The full result will be emailed when execution finishes."
                : "Task accepted. The result will be saved to local task history; email notifications are disabled.",
        });
    }

    [McpServerTool(Name = "dsh_task_status")]
    [Description("Get the concise status of a previously submitted DSH task.")]
    public static async Task<string> GetStatusAsync(
        [Description("The job ID returned by dsh_submit_task.")] string jobId,
        IMcpStateStore state,
        McpCallerContext callerContext,
        McpBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var caller = callerContext.GetRequired();
        var job = await state.GetJobAsync(caller.UserId, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The requested DSH job was not found.");
        var preview = job.Result;
        var truncated = preview is not null && preview.Length > Math.Min(1_000, settings.MaxResultCharacters);
        if (truncated)
        {
            preview = preview![..Math.Min(1_000, settings.MaxResultCharacters)];
        }

        return JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            status = job.Status,
            emailStatus = job.EmailStatus,
            resultPreview = preview,
            error = job.Error,
            truncated,
            createdAt = job.CreatedAt,
            updatedAt = job.UpdatedAt,
        });
    }

    [McpServerTool(Name = "dsh_task_result")]
    [Description("Read a bounded slice of a completed DSH job result. Use startIndex to retrieve the next slice.")]
    public static async Task<string> GetResultAsync(
        [Description("The job ID returned by dsh_submit_task.")] string jobId,
        IMcpStateStore state,
        McpCallerContext callerContext,
        McpBridgeSettings settings,
        [Description("Character offset in the result.")] int startIndex = 0,
        [Description("Maximum characters to return in this response.")] int maxCharacters = 2_000,
        CancellationToken cancellationToken = default)
    {
        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        var caller = callerContext.GetRequired();
        var job = await state.GetJobAsync(caller.UserId, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The requested DSH job was not found.");
        var content = job.Result ?? job.Error ?? string.Empty;
        var boundedLength = Math.Min(maxCharacters, settings.MaxResultCharacters);
        var start = Math.Min(startIndex, content.Length);
        var length = Math.Min(boundedLength, content.Length - start);
        var nextIndex = start + length;

        return JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            status = job.Status,
            startIndex = start,
            nextIndex = nextIndex < content.Length ? nextIndex : (int?)null,
            totalCharacters = content.Length,
            content = content.Substring(start, length),
        });
    }
}
