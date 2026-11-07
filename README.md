# dsh-sdk-csharp

**A community-maintained C#/.NET SDK for the official DeepSeek Harness runtime.**

This project drives the official `dsh` runtime; it does not reimplement or fork DeepSeek Harness. The SDK starts `dsh --profile sdk` as a child process and communicates over the runtime's newline-delimited JSON-RPC 2.0 stdio protocol.

[简体中文](README.zh-CN.md)

> **Unofficial and early-stage:** This SDK is not developed, endorsed, or supported by DeepSeek. DeepSeek Harness is in developer preview, and its SDK protocol currently has no version negotiation or compatibility guarantee. Pin and test the DSH version you deploy.

## What it provides

- .NET 10-compatible library, targeting `net10.0`
- Starts and owns the DSH SDK-profile process
- Performs the SDK `initialize` handshake and routes prompts to sessions
- `RunAsync` collects the matching session activity through its idle transition
- Low-level prompt, resume, wait, and working-directory operations
- Independent asynchronous notification subscriptions
- One-shot operation approvals and native user-question callbacks
- Graceful protocol shutdown and child-process cleanup
- No third-party runtime dependencies

The NuGet package ID is `DeepSeek.Harness.Sdk`; the GitHub repository name is `dsh-sdk-csharp` so the target language and ecosystem are easy to discover. Both names are subject to change before the first stable release.
The SDK protocol does not expose a Session-list method; persist Session IDs in the calling application when it needs to resume or present prior Sessions.
The default model ID is `deepseek-flash`, DeepSeek's current canonical Flash API model ID. DSH's SDK protocol still requires provider and model fields in `initialize`; applications normally leave the provider at `deepseek-official` and use this model default.

## Requirements

1. Install the official DeepSeek Harness CLI and make `dsh` available on `PATH`.
2. Ensure the `sdk` profile is available in that installation.
3. Configure a model provider and its credentials in the selected DSH home.
4. Use a disposable or appropriately isolated workspace when running model-generated commands.

The SDK does not embed credentials or download/replace the Harness runtime. It inherits the parent process environment and supports explicit environment overrides.

## Quick start

The package has not been published to NuGet yet. From a checkout, reference `src/Dsh.Sdk.CSharp/Dsh.Sdk.CSharp.csproj`:

```csharp
using DeepSeek.Harness.Sdk;

var options = new DeepSeekHarnessOptions
{
    DshExecutable = "dsh",
    Profile = "sdk",
    DshHome = Path.GetFullPath(".dsh-home"),
    WorkingDirectory = Path.GetFullPath("./workspace"),
    Provider = "deepseek-official",
    Model = "deepseek-flash",
};

await using var harness = new DeepSeekHarnessClient(options);
var result = await harness.RunAsync("Summarize this project.");

Console.WriteLine(result.FinalResponse);
Console.WriteLine($"Session: {result.SessionId}; finish: {result.FinishReason}");
```

## Console demo and the official CLI

DeepSeek Harness already ships its own CLI. For a one-shot task, use `dsh headless "your task"`; `dsh tui` provides a terminal UI, and `dsh web` starts the browser UI. These are official Harness app profiles, not clients implemented with this C# SDK.

Use the interactive C# demo when you want to test how a .NET application controls DSH, keeps one Session across turns, and receives status notifications:

Choose a profile name that does not already exist. `--from-default-profile` replaces the named profile with a fresh copy.

```powershell
dsh --profile dsh-sdk-csharp-chat --from-default-profile sdk --dump-config | Out-Null
dotnet run --project .\examples\ConsoleChat\ConsoleChat.csproj -- `
  "C:\path\to\dsh.cmd" `
  "$HOME\.dsh" `
  dsh-sdk-csharp-chat
```

The demo exits on `/exit` or Ctrl+C. It continuously listens for assistant messages from its Session, so Schedule replies delivered to the **current Session** can appear in the console; deliveries to a different or newly created Session are not shown by this demo. The DSH host must stay running to deliver due work, and the minimum fixed schedule interval is one minute.

Do not treat an assistant message such as “I will wait a minute and remind you” as proof that a Schedule task was created. Confirm that Schedule actually created a task and that it targets the current Session. A model-generated countdown chain is only best-effort while that session/runtime remains active; it is not a durable alarm or an OS push notification. Use a phone or system alarm for anything time-critical.

On Windows, `sandbox-local windows-acl temp grant materialization failed` is a DSH runtime sandbox error, not an SDK approval error. Do not switch to Full Access or run broad ACL-repair scripts to fix a reminder. An upstream-reported workaround is to set `TEMP` and `TMP` for the DSH child process to a user directory on a volume where integrity labels can be set; the SDK's `EnvironmentVariables` option can pass those values. See [the upstream Windows ACL discussion](https://github.com/deepseek-ai/deepseek-harness/discussions/8624).

The demo uses the model credentials already configured in the specified DSH home; it does not prompt for or print API keys. On Windows Desktop installations, pass the bundled `dsh.cmd`; the SDK launches batch files through `cmd.exe`.

## Streamable HTTP MCP bridge

The separate [`Dsh.Sdk.CSharp.McpServer`](./src/Dsh.Sdk.CSharp.McpServer/README.md) project wraps this SDK with the official MCP C# SDK and exposes a bearer-key-protected Streamable HTTP endpoint at `/mcp`. It provides a short synchronous `dsh_run` tool and SQLite-backed asynchronous task, session, and email-outbox tools for clients with short timeouts.

This bridge exposes a standard Streamable HTTP MCP endpoint for compatible clients. Device-specific integrations should use a separate adapter or gateway; for example, [verdure-mcp-for-xiaozhi](https://github.com/maker-community/verdure-mcp-for-xiaozhi) adapts MCP services for Xiaozhi and Tuya smart devices.

The MCP service starts the local DSH executable through this repository's SDK; it does not call a remote DSH service. The SDK uses its default provider/model route and supplies the protocol-required fields during initialization; the MCP service has no duplicate model settings. The DSH home/profile supplies local runtime configuration and credentials. The MCP project includes non-secret defaults in `appsettings.json`; environment variables or command-line settings can override them at runtime. The MCP API key and SMTP credentials are not stored in that file and should be provided through a secret store or environment variables.

The MCP bridge reads the caller's `X-User-Id` and `X-User-Email` from HTTP headers set by a trusted gateway. Email recipients are validated against the configured allowlist and are not model-supplied tool arguments. SQLite stores MCP-owned session mappings, active-session pointers, jobs, results, and email outbox records outside the repository by default.

The bridge's async job worker waits for the SDK's observed run completion before queuing the email. A separate notification monitor also watches SDK notifications for DSH host runs on sessions registered by the bridge and emails completions it can correlate. Schedule-event correlation depends on the DSH runtime's notifications and is best-effort; the bridge does not create schedules or discover arbitrary DSH sessions. SQLite preserves app mappings but does not guarantee DSH conversation restoration after a host restart.

`RunAsync` returns after the prompt's durable inbox receipt and the root Agent's idle transition. This is the runtime's observed activity interval; it is not a causal guarantee if another caller or queued input is sharing that Session. Use a separate `sessionId`, workspace, and DSH home when independent work must be isolated.
Calls through one client are serialized per `sessionId`; separate sessions can run concurrently.

To keep a session and runtime open across multiple requests:

```csharp
await harness.StartAsync();
var sessionId = "scheduled-job-42";

var first = await harness.RunAsync("Inspect the latest deployment.", sessionId);
var second = await harness.RunAsync("Compare it with yesterday's deployment.", sessionId);
```

For progress notifications:

```csharp
var result = await harness.RunAsync(
    "Run the checks and summarize failures.",
    onNotification: notification =>
        Console.WriteLine($"{notification.Method}: {notification.Parameters}"));
```

For an application-owned approval prompt, opt in explicitly. Only `AllowOnce` grants the current operation; the SDK never grants approvals by default:

```csharp
var options = new DeepSeekHarnessOptions
{
    ApprovalRequests = true,
    OnApprovalRequest = async (request, cancellationToken) =>
    {
        Console.WriteLine(
            $"Allow {request.ToolName} once? {request.Reason ?? string.Empty}");
        var answer = await Console.In.ReadLineAsync(cancellationToken);
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            ? ApprovalRequestDecision.AllowOnce
            : ApprovalRequestDecision.Reject;
    },
};
```

Set `OnUserQuestion` to handle `ask_user_question` and plan reviews. The callback receives the original question ids and a cancellation token; answer each id once, or return `SdkUserQuestionResponse.Dismiss()` when the user cancels. The SDK advertises this capability only when the callback is configured. A cancelled question cancels its callback and discards any late answer.

For direct access to the event stream, subscribe before submitting work:

```csharp
await using var notifications = harness.SubscribeNotifications();
var messageId = await harness.PromptAsync("job-42", "Run the checks.");

// PromptAsync returns when DSH accepts the message, not when the Agent finishes.
// Consume notifications or call WaitForSessionAsync("job-42") to observe completion.
await harness.WaitForSessionAsync("job-42");
Console.WriteLine($"Queued message: {messageId}");
```

## API notes

- The protocol uses one JSON-RPC message per newline on stdin/stdout. DSH stdout is reserved for protocol frames; do not add a stdout logger to the SDK profile.
- `RunAsync` routes input through `session/query`, matching the official SDK behavior for ordinary prompts and supported native commands.
- When the runtime reports that the requested session already exists, `RunAsync` attempts `session/resume` and retries the original request once; a failed resume is reported, not silently replaced with a new session. When an older runtime reports that `session/query` is unavailable, `RunAsync` falls back to `session/prompt`; that fallback does not provide slash-command routing.
- `PromptAsync` uses the lower-level `session/prompt` method and returns an enqueue receipt only.
- Cancellation or timeout stops the caller's wait; it does not cancel or roll back work already accepted by DSH.
- The runtime sends notifications for all sessions. The high-level run result filters activity to the requested Session and discovered in-process descendants; a raw subscription is intentionally unfiltered.
- Unsupported server-to-client requests are rejected. Approval requests are opt-in and one-shot; malformed decisions and callback failures return protocol errors, so operations fail closed. Native user questions require an `OnUserQuestion` handler unless the DSH composition supplies its own answerer.
- The SDK should be treated as a local process client, not as a network server. Put authentication and authorization in a trusted service boundary if you expose jobs remotely.

## Build and test

```powershell
dotnet test .\dsh-sdk-csharp.slnx --configuration Release
dotnet pack .\src\Dsh.Sdk.CSharp\Dsh.Sdk.CSharp.csproj --configuration Release
```

The tests use a small fake JSON-RPC runtime and do not require API credentials or a live model.

For a real local DSH integration check, see [`examples/LiveSmoke`](./examples/LiveSmoke). It invokes the configured model and writes a session to the selected DSH home.

## Project direction

The first milestone is wire compatibility and safe process ownership. Follow-up work should be prioritized around:

1. User-question and one-shot approval callbacks
2. More protocol conformance tests against pinned official DSH versions
3. NuGet packaging and release automation
4. Examples for scheduled workers and application-hosted integrations

Issues and pull requests are welcome. Please include the DSH version, operating system, and a sanitized protocol/error trace when reporting compatibility problems; never attach API keys or personal session data.

## License

MIT. DeepSeek Harness is a separate project; see its [repository](https://github.com/deepseek-ai/deepseek-harness) for its own license and notices.
