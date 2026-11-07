using DeepSeek.Harness.Sdk;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.McpServer;

public sealed class DshHostScheduleMonitor(
    IDshExecutionService execution,
    IMcpStateStore state,
    ILogger<DshHostScheduleMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, PendingRun> _pendingRuns = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var notification in execution.ReadNotificationsAsync(stoppingToken)
                                   .ConfigureAwait(false))
                {
                    await HandleNotificationAsync(notification, stoppingToken).ConfigureAwait(false);
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    throw new HarnessTransportException("The DSH notification stream closed unexpectedly.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "The DSH host-schedule notification monitor stopped with {ExceptionType}.",
                    exception.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleNotificationAsync(
        HarnessNotification notification,
        CancellationToken cancellationToken)
    {
        if (notification.Method == "session.event"
            && TryGetString(notification.Parameters, "sessionId", out var eventSessionId)
            && notification.Parameters.TryGetProperty("event", out var eventElement)
            && eventElement.ValueKind == JsonValueKind.Object
            && TryGetString(eventElement, "type", out var eventType))
        {
            if (eventType == "agent/inbox/spliced")
            {
                await StartObservedRunAsync(
                    eventSessionId, eventElement, cancellationToken).ConfigureAwait(false);
            }
            else if (_pendingRuns.TryGetValue(eventSessionId, out var pendingRun))
            {
                if (eventType == "assistant/message")
                {
                    pendingRun.Response = ExtractText(eventElement);
                }
                else if (eventType == "turn/end"
                         && eventElement.TryGetProperty("data", out var turnData)
                         && turnData.TryGetProperty("reason", out var reason)
                         && TryGetString(reason, "kind", out var finishReason))
                {
                    pendingRun.FinishReason = finishReason;
                }
            }

            return;
        }

        if (notification.Method == "session.status"
            && TryGetString(notification.Parameters, "sessionId", out var statusSessionId)
            && TryGetString(notification.Parameters, "status", out var status)
            && status == "idle"
            && _pendingRuns.TryRemove(statusSessionId, out var completedRun)
            && !string.IsNullOrWhiteSpace(completedRun.Response))
        {
            var persisted = await state.PersistObservedHostResultAsync(
                statusSessionId,
                completedRun.MessageId,
                completedRun.Response,
                completedRun.FinishReason,
                cancellationToken).ConfigureAwait(false);
            if (persisted)
            {
                logger.LogInformation(
                    "Persisted an observed DSH host run for session {SessionId}.",
                    statusSessionId);
            }
        }
    }

    private async Task StartObservedRunAsync(
        string sessionId,
        JsonElement eventElement,
        CancellationToken cancellationToken)
    {
        if (execution.IsSessionRunActive(sessionId)
            || await state.IsMcpJobRunningAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var session = await state.FindSessionByDshSessionIdAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null
            || !eventElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("inserted", out var inserted)
            || inserted.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in inserted.EnumerateArray())
        {
            if (TryGetString(item, "id", out var messageId))
            {
                _pendingRuns.TryAdd(sessionId, new PendingRun(messageId));
                return;
            }
        }
    }

    private static string ExtractText(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(content.EnumerateArray()
            .Where(block => TryGetString(block, "type", out var type) && type == "text")
            .Select(block => TryGetString(block, "text", out var text) ? text : string.Empty));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } result)
        {
            value = result;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed class PendingRun(string messageId)
    {
        public string MessageId { get; } = messageId;
        public string Response { get; set; } = string.Empty;
        public string? FinishReason { get; set; }
    }
}
