using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Dsh.Sdk.CSharp.McpServer;

[McpServerToolType]
public static class DshSessionTools
{
    [McpServerTool(Name = "dsh_host_runs")]
    [Description("List recent DSH host-originated runs observed on this caller's registered sessions. Results are persisted even when email notifications are disabled.")]
    public static async Task<string> HostRunsAsync(
        IMcpStateStore state,
        McpCallerContext callerContext,
        McpBridgeSettings settings,
        [Description("Maximum number of recent host runs to return, from 1 to 50.")] int count = 5,
        CancellationToken cancellationToken = default)
    {
        var caller = callerContext.GetRequired();
        var runs = await state.GetRecentHostRunsAsync(
            caller.UserId, count, cancellationToken).ConfigureAwait(false);
        var maxResponseCharacters = Math.Min(1_000, settings.MaxResultCharacters);

        return JsonSerializer.Serialize(runs.Select(run => new
        {
            run.SessionId,
            run.SessionTitle,
            run.MessageId,
            Response = run.Response.Length <= maxResponseCharacters
                ? run.Response
                : run.Response[..maxResponseCharacters],
            Truncated = run.Response.Length > maxResponseCharacters,
            run.FinishReason,
            run.EmailStatus,
            run.CreatedAt,
        }));
    }

    [McpServerTool(Name = "dsh_current_session")]
    [Description("Return the caller's current DSH session. Normal tasks stay in this session until an explicit switch.")]
    public static async Task<string> CurrentAsync(
        IMcpStateStore state,
        McpCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        var caller = callerContext.GetRequired();
        var session = await state.GetOrCreateActiveSessionAsync(
            caller.UserId, caller.Email, cancellationToken).ConfigureAwait(false);
        return Serialize(session);
    }

    [McpServerTool(Name = "dsh_list_sessions")]
    [Description("List sessions created by this MCP service for the authenticated caller.")]
    public static async Task<string> ListAsync(
        IMcpStateStore state,
        McpCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        var caller = callerContext.GetRequired();
        var sessions = await state.ListSessionsAsync(caller.UserId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(sessions.Select(ToResponse));
    }

    [McpServerTool(Name = "dsh_create_session")]
    [Description("Create and activate a new DSH session. This is an explicit session switch.")]
    public static async Task<string> CreateAsync(
        [Description("A short label for the new session.")] string title,
        IMcpStateStore state,
        McpCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        var caller = callerContext.GetRequired();
        var session = await state.CreateAndActivateSessionAsync(
            caller.UserId, caller.Email, title, cancellationToken).ConfigureAwait(false);
        return Serialize(session);
    }

    [McpServerTool(Name = "dsh_switch_session")]
    [Description("Switch the caller's active DSH session. Existing tasks remain attached to the session they started with.")]
    public static async Task<string> SwitchAsync(
        [Description("The MCP session ID returned by dsh_list_sessions or dsh_create_session.")] string sessionId,
        IMcpStateStore state,
        McpCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        var caller = callerContext.GetRequired();
        var session = await state.SwitchActiveSessionAsync(
            caller.UserId, caller.Email, sessionId, cancellationToken).ConfigureAwait(false);
        return Serialize(session);
    }

    private static string Serialize(McpConversation session) =>
        JsonSerializer.Serialize(ToResponse(session));

    private static object ToResponse(McpConversation session) => new
    {
        sessionId = session.Id,
        title = session.Title,
        isActive = session.IsActive,
        createdAt = session.CreatedAt,
        updatedAt = session.UpdatedAt,
    };
}
