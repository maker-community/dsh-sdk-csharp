using DeepSeek.Harness.Sdk;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.McpServer;

public sealed class DshJobWorker(
    IMcpStateStore state,
    IDshExecutionService execution,
    McpBridgeSettings settings,
    ILogger<DshJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await state.TryClaimNextJobAsync(stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var result = await execution.RunAsync(
                        job.Prompt, job.DshSessionId, stoppingToken).ConfigureAwait(false);
                    if (string.Equals(result.FinishReason, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        var error = settings.LogDshMessages
                            ? GetDshFailureDetails(result)
                            : "DSH finished the task with reason 'error'. Enable DSH_MCP_LOG_DSH_MESSAGES for event details.";
                        logger.LogError(
                            "DSH task {JobId} finished with error; response length is {ResponseLength} characters. {DshErrorDetails}",
                            job.Id,
                            result.FinalResponse.Length,
                            settings.LogDshMessages ? error : string.Empty);
                        await state.FailJobAsync(job.Id, error, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await state.CompleteJobAsync(job.Id, result, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "DSH task {JobId} completed with finish reason {FinishReason}.",
                        job.Id,
                        result.FinishReason);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var error = settings.LogDshMessages
                        ? exception.Message
                        : $"DSH execution failed ({exception.GetType().Name}). Check the MCP service diagnostics using job ID {job.Id}.";
                    if (settings.LogDshMessages)
                    {
                        logger.LogError(
                            "DSH task {JobId} failed: {DshErrorMessage}",
                            job.Id,
                            error);
                    }
                    else
                    {
                        logger.LogError(
                            "DSH task {JobId} failed with {ExceptionType}.",
                            job.Id,
                            exception.GetType().Name);
                    }

                    await state.FailJobAsync(job.Id, error, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "The DSH job worker encountered {ExceptionType}.",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static string GetDshFailureDetails(HarnessRunResult result)
    {
        for (var index = result.Events.Count - 1; index >= 0; index--)
        {
            var eventElement = result.Events[index];
            if (eventElement.ValueKind == JsonValueKind.Object
                && eventElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "turn/end"
                && eventElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("reason", out var reason))
            {
                var details = reason.GetRawText();
                return details.Length <= 4_000
                    ? $"DSH turn ended with error: {details}"
                    : $"DSH turn ended with error: {details[..4_000]} [truncated]";
            }
        }

        return "DSH finished the task with reason 'error', but provided no turn/end reason details.";
    }
}

public sealed class EmailOutboxWorker(
    IMcpStateStore state,
    IEmailNotificationService email,
    ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!email.IsEnabled)
        {
            logger.LogInformation("Email notifications are disabled; queued email delivery is paused.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await state.TryClaimNextEmailAsync(stoppingToken).ConfigureAwait(false);
                if (message is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var result = await email.SendMessageAsync(
                        message.Recipient,
                        message.Subject,
                        message.Body,
                        stoppingToken).ConfigureAwait(false);
                    if (result.Status == "sent")
                    {
                        await state.MarkEmailSentAsync(message.Id, stoppingToken).ConfigureAwait(false);
                        logger.LogInformation("Email outbox item {OutboxId} was delivered.", message.Id);
                    }
                    else
                    {
                        var error = result.Error ?? "Email provider reported an unspecified delivery failure.";
                        await state.MarkEmailFailedAsync(message.Id, error, stoppingToken).ConfigureAwait(false);
                        logger.LogWarning("Email outbox item {OutboxId} failed: {Error}", message.Id, error);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var error = $"Email delivery failed ({exception.GetType().Name}).";
                    await state.MarkEmailFailedAsync(message.Id, error, stoppingToken).ConfigureAwait(false);
                    logger.LogError(
                        "Email outbox item {OutboxId} failed with {ExceptionType}.",
                        message.Id,
                        exception.GetType().Name);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "The email outbox worker encountered {ExceptionType}.",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
