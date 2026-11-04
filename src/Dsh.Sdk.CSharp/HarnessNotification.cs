using System.Text.Json;

namespace DeepSeek.Harness.Sdk;

/// <summary>A server notification received from the Harness runtime.</summary>
public sealed record HarnessNotification(string Method, JsonElement Parameters);

/// <summary>One image block accepted by the current Harness SDK wire protocol.</summary>
public sealed record PromptImage(string Data, string MimeType, string? Name = null);

/// <summary>The result of one prompt or native Harness command.</summary>
public sealed record HarnessRunResult(
    string SessionId,
    string FinalResponse,
    string? FinishReason,
    IReadOnlyList<JsonElement> Events,
    IReadOnlyList<HarnessNotification> Notifications);
