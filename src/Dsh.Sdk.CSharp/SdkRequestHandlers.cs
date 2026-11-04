using System.Text.Json;

namespace DeepSeek.Harness.Sdk;

/// <summary>One-shot decision for an SDK-owned operation approval.</summary>
public sealed record ApprovalRequestDecision(string Outcome)
{
    /// <summary>Allows only the operation currently awaiting this decision.</summary>
    public static ApprovalRequestDecision AllowOnce { get; } = new("allowed-once");

    /// <summary>Rejects the operation currently awaiting this decision.</summary>
    public static ApprovalRequestDecision Reject { get; } = new("rejected");

    /// <summary>Marks the current approval as cancelled.</summary>
    public static ApprovalRequestDecision Cancel { get; } = new("cancelled");
}

/// <summary>Data presented to the host before an SDK-owned operation is approved.</summary>
public sealed record ApprovalRequest(
    string SessionId,
    string ToolName,
    string? CallId,
    string? Reason);

/// <summary>One answer corresponding to one user-question id.</summary>
public sealed record SdkUserQuestionAnswer(
    string Id,
    IReadOnlyList<string> Selected,
    string? Custom = null);

/// <summary>
/// A batch of user-question answers, or an explicit user dismissal.
/// Exactly one of <see cref="Answers"/> and <see cref="Cancelled"/> must be set.
/// </summary>
public sealed record SdkUserQuestionResponse
{
    private SdkUserQuestionResponse(
        IReadOnlyList<SdkUserQuestionAnswer>? answers,
        bool? cancelled)
    {
        Answers = answers;
        Cancelled = cancelled;
    }

    /// <summary>Structured answers, one for each question in the request.</summary>
    public IReadOnlyList<SdkUserQuestionAnswer>? Answers { get; }

    /// <summary>True when the user explicitly dismissed the whole question request.</summary>
    public bool? Cancelled { get; }

    /// <summary>Creates a completed answer batch.</summary>
    public static SdkUserQuestionResponse Answer(
        IReadOnlyList<SdkUserQuestionAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return new SdkUserQuestionResponse(answers, null);
    }

    /// <summary>Creates an explicit user dismissal without fabricated answers.</summary>
    public static SdkUserQuestionResponse Dismiss() => new(null, true);
}

/// <summary>The structured request for user input sent by the Harness runtime.</summary>
public sealed record SdkUserQuestionRequest(
    string RequestId,
    string SessionId,
    IReadOnlyList<JsonElement> Questions);

/// <summary>Callback for one-shot SDK approval requests.</summary>
public delegate Task<ApprovalRequestDecision> ApprovalRequestHandler(
    ApprovalRequest request,
    CancellationToken cancellationToken);

/// <summary>Callback for native Harness user questions and plan reviews.</summary>
public delegate Task<SdkUserQuestionResponse> UserQuestionHandler(
    SdkUserQuestionRequest request,
    CancellationToken cancellationToken);
