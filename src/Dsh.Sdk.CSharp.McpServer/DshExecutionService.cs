using DeepSeek.Harness.Sdk;
using Microsoft.Extensions.Logging;

namespace Dsh.Sdk.CSharp.McpServer;

public sealed class DshExecutionService : IDshExecutionService, IAsyncDisposable
{
    private readonly DeepSeekHarnessClient _client;
    private readonly McpBridgeSettings _settings;
    private readonly string _sessionPrefix = $"dsh-mcp-{Guid.NewGuid():N}";

    public DshExecutionService(McpBridgeSettings settings, ILogger<DshExecutionService> logger)
    {
        _settings = settings;
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

    public async Task<HarnessRunResult> RunAsync(
        string prompt,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > _settings.MaxPromptCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(prompt),
                $"Prompt exceeds the configured {_settings.MaxPromptCharacters}-character limit.");
        }

        var sessionId = CreateSessionId(conversationId);
        return await _client.RunAsync(prompt, sessionId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private string CreateSessionId(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return _sessionPrefix;
        }

        if (conversationId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(conversationId),
                "conversationId must not exceed 128 characters.");
        }

        return $"{_sessionPrefix}-{Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(conversationId)))[..24].ToLowerInvariant()}";
    }
}
