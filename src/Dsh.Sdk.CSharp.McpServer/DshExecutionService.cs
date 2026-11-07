using DeepSeek.Harness.Sdk;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsh.Sdk.CSharp.McpServer;

public sealed class DshExecutionService : IDshExecutionService, IAsyncDisposable
{
    private readonly DeepSeekHarnessClient _client;
    private readonly McpBridgeSettings _settings;
    private readonly ILogger<DshExecutionService> _logger;
    private readonly string? _smtpPassword;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeSessions = new(StringComparer.Ordinal);
    private static readonly Regex SensitivePromptPattern = new(
        @"(?i)password|passwd|passphrase|secret|credential|api[\s_-]*key|access[\s_-]*token|refresh[\s_-]*token|authorization|bearer\s|private[\s_-]*key|密码|口令|密钥|凭据|令牌|[a-z0-9_+/=-]{24,}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public DshExecutionService(
        McpBridgeSettings settings,
        IConfiguration configuration,
        ILogger<DshExecutionService> logger)
    {
        _settings = settings;
        _logger = logger;
        _smtpPassword = configuration["SMTP_PASSWORD"] ?? configuration["Email:SmtpPassword"];
        Directory.CreateDirectory(settings.DshWorkspace);
        var dshOptions = new DeepSeekHarnessOptions
        {
            DshExecutable = settings.DshExecutable,
            DshHome = settings.DshHome,
            Profile = settings.DshProfile,
            ProcessWorkingDirectory = settings.DshWorkspace,
            WorkingDirectory = settings.DshWorkspace,
        };
        logger.LogInformation(
            "DSH MCP bridge configuration: executable={Executable}, home={DshHome}, profile={Profile}, workspace={Workspace}, provider={Provider}, model={Model}",
            settings.DshExecutable,
            settings.DshHome,
            settings.DshProfile,
            settings.DshWorkspace,
            dshOptions.Provider,
            dshOptions.Model);
        _client = new DeepSeekHarnessClient(dshOptions);
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _client.StartAsync(cancellationToken);

    public async IAsyncEnumerable<HarnessNotification> ReadNotificationsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var subscription = _client.SubscribeNotifications();
        await _client.StartAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var notification in subscription.ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return notification;
        }
    }

    public bool IsSessionRunActive(string sessionId) =>
        _activeSessions.ContainsKey(sessionId);

    public async Task<HarnessRunResult> RunAsync(
        string prompt,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (prompt.Length > _settings.MaxPromptCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(prompt),
                $"Prompt exceeds the configured {_settings.MaxPromptCharacters}-character limit.");
        }

        var gate = _runGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _activeSessions.TryAdd(sessionId, 0);
            _logger.LogInformation(
                "Sending DSH prompt for session {SessionId}; prompt length is {PromptLength} characters.",
                sessionId,
                prompt.Length);
            if (_settings.LogDshMessages)
            {
                if (prompt.Contains(_settings.ApiKey, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(_smtpPassword)
                        && prompt.Contains(_smtpPassword, StringComparison.Ordinal))
                    || SensitivePromptPattern.IsMatch(prompt))
                {
                    _logger.LogWarning(
                        "DSH prompt content for session {SessionId} was omitted because it may contain sensitive data.",
                        sessionId);
                }
                else
                {
                    _logger.LogInformation(
                        "DSH prompt for session {SessionId}: {Prompt}",
                        sessionId,
                        TruncateForLog(prompt));
                }
            }

            var result = await _client.RunAsync(
                    prompt,
                    sessionId,
                    onNotification: notification => LogNotification(notification, sessionId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "DSH run finished for session {SessionId} with finish reason {FinishReason}; response length is {ResponseLength} characters.",
                sessionId,
                result.FinishReason ?? "(not reported)",
                result.FinalResponse.Length);
            return result;
        }
        finally
        {
            _activeSessions.TryRemove(sessionId, out _);
            gate.Release();
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private void LogNotification(HarnessNotification notification, string requestedSessionId)
    {
        if (notification.Method != "session.event"
            || !notification.Parameters.TryGetProperty("event", out var eventElement)
            || eventElement.ValueKind != JsonValueKind.Object
            || !TryGetString(eventElement, "type", out var eventType))
        {
            _logger.LogDebug(
                "Received DSH notification {NotificationMethod} for session {SessionId}.",
                notification.Method,
                requestedSessionId);
            return;
        }

        if (eventType == "turn/end"
            && eventElement.TryGetProperty("data", out var turnData)
            && turnData.TryGetProperty("reason", out var reason)
            && TryGetString(reason, "kind", out var finishReason))
        {
            if (string.Equals(finishReason, "error", StringComparison.OrdinalIgnoreCase))
            {
                if (_settings.LogDshMessages)
                {
                    _logger.LogError(
                        "DSH turn ended with error for session {SessionId}: {ErrorDetails}",
                        requestedSessionId,
                        TruncateForLog(reason.GetRawText()));
                }
                else
                {
                    _logger.LogWarning(
                        "DSH turn ended with error for session {SessionId}.",
                        requestedSessionId);
                }
            }
            else
            {
                _logger.LogInformation(
                    "DSH turn ended for session {SessionId} with finish reason {FinishReason}.",
                    requestedSessionId,
                    finishReason);
            }

            return;
        }

        if (eventType != "assistant/message")
        {
            _logger.LogDebug(
                "Received DSH event {EventType} for session {SessionId}.",
                eventType,
                requestedSessionId);
            return;
        }

        var message = ExtractAssistantText(eventElement);
        if (string.IsNullOrEmpty(message))
        {
            var contentTypes = ExtractAssistantContentTypes(eventElement);
            _logger.LogInformation(
                "Received a DSH assistant message with no text for session {SessionId}; content block types: {ContentBlockTypes}.",
                requestedSessionId,
                contentTypes);
            return;
        }

        if (!_settings.LogDshMessages)
        {
            _logger.LogInformation(
                "Received DSH assistant message for session {SessionId}; message length is {MessageLength} characters.",
                requestedSessionId,
                message.Length);
            return;
        }

        _logger.LogInformation(
            "Received DSH assistant message for session {SessionId}: {AssistantMessage}",
            requestedSessionId,
            TruncateForLog(message));
    }

    private string TruncateForLog(string value) =>
        value.Length <= _settings.MaxResultCharacters
            ? value
            : $"{value[.._settings.MaxResultCharacters]} [truncated in log]";

    private static string ExtractAssistantText(JsonElement eventElement)
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

    private static string ExtractAssistantContentTypes(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return "(unavailable)";
        }

        var types = content.EnumerateArray()
            .Select(block => TryGetString(block, "type", out var type) ? type : "(unknown)")
            .Distinct(StringComparer.Ordinal)
            .Take(10);
        var description = string.Join(", ", types);
        return description.Length == 0 ? "(none)" : description;
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
}
