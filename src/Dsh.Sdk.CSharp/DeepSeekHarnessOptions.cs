namespace DeepSeek.Harness.Sdk;

/// <summary>Options for launching and configuring a local DeepSeek Harness SDK runtime.</summary>
public sealed class DeepSeekHarnessOptions
{
    /// <summary>The DSH executable or executable path. Defaults to <c>dsh</c> on PATH.</summary>
    public string DshExecutable { get; init; } = "dsh";

    /// <summary>Arguments inserted before the SDK-owned profile arguments.</summary>
    public IReadOnlyList<string> DshArguments { get; init; } = [];

    /// <summary>Optional profile patch files applied in order after the selected profile.</summary>
    public IReadOnlyList<string> Patches { get; init; } = [];

    /// <summary>The DSH profile that serves the SDK JSON-RPC protocol.</summary>
    public string Profile { get; init; } = "sdk";

    /// <summary>Optional isolated DSH home directory.</summary>
    public string? DshHome { get; init; }

    /// <summary>Process working directory. Defaults to the current directory.</summary>
    public string ProcessWorkingDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Workspace recorded on SDK-created sessions. Defaults to the current directory.</summary>
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Provider route for SDK-created agents.</summary>
    public string Provider { get; init; } = "deepseek-official";

    /// <summary>Model name for SDK-created agents. Defaults to DeepSeek's current canonical Flash API model ID.</summary>
    public string Model { get; init; } = "deepseek-flash";

    /// <summary>Optional provider-specific reasoning effort identifier.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Optional positive output-token limit for model requests.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Enables client-owned one-shot approval requests. Approvals remain fail-closed
    /// when <see cref="OnApprovalRequest"/> is not provided.
    /// </summary>
    public bool ApprovalRequests { get; init; }

    /// <summary>Handles an SDK-owned one-shot operation approval.</summary>
    public ApprovalRequestHandler? OnApprovalRequest { get; init; }

    /// <summary>
    /// Handles native user questions and plan reviews. Supplying this callback
    /// advertises the user-question capability to the runtime.
    /// </summary>
    public UserQuestionHandler? OnUserQuestion { get; init; }

    /// <summary>Optional timeout for each JSON-RPC request. Null waits without a deadline.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>Maximum time to complete the runtime initialization handshake.</summary>
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum time allowed for the protocol shutdown request.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Time to wait for graceful process exit after closing stdin.</summary>
    public TimeSpan ProcessShutdownGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Environment variables added to or used to override the parent environment.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>();
}
